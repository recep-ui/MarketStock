using Dapper;
using PazaryeriHub.Application.Catalog;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Application.Stock;
using PazaryeriHub.Domain.Orders;
using PazaryeriHub.Infrastructure.Persistence;
using PazaryeriHub.Infrastructure.Sync;

namespace PazaryeriHub.Api.Dev;

public sealed record DemoDataResult(
    string Tag, IReadOnlyList<string> SkuCodes, IReadOnlyList<string> OrderNos, long FailedSyncId,
    string DeviationSkuCode, int DeviationExpected, int DeviationChannelStock);

/// <summary>
/// Yalnızca Development/Testing: panel ekranlarını ve Playwright senaryolarını beslemek için etiketli demo veri üretir.
/// Gerçek kanal çağrısı yapmaz; siparişler mock kanal üzerinden değil doğrudan kayıt servisine verilir.
/// </summary>
public sealed class DemoDataSeeder(
    ICatalogService catalog,
    IStockLedger ledger,
    IOrderIngestionService ingestion,
    SyncStore syncStore,
    SqlConnectionFactory connectionFactory)
{
    public async Task<DemoDataResult> SeedAsync(CancellationToken ct)
    {
        var tag = DateTime.UtcNow.ToString("MMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + Random.Shared.Next(10, 99);
        var warehouseId = (await catalog.GetWarehousesAsync(ct)).First().Id;

        // 4 SKU: bol stok, az stok, sapmalı, stoksuz
        var stocks = new[] { 20, 3, 8, 0 };
        var product = await catalog.CreateProductAsync(new CreateProductRequest($"Demo Tişört {tag}", "Softcotton", "Tişört",
            stocks.Select((_, i) => new SkuInput($"DEMO-{tag}-{i + 1}", $"869{tag}{i + 1}", "Beyaz", new[] { "S", "M", "L", "XL" }[i])).ToList()), ct);
        for (var i = 0; i < stocks.Length; i++)
        {
            if (stocks[i] > 0)
            {
                await ledger.ReceiveAsync(new StockChangeRequest(product.Skus[i].Id, warehouseId, stocks[i], "DEMO", tag, "demo"), ct);
            }
        }

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var channels = (await connection.QueryAsync<(int Id, string Kod)>("SELECT Id, Kod FROM dbo.Channel")).ToDictionary(c => c.Kod, c => c.Id);

        // Eşleştirmeler: her SKU Trendyol ve Hepsiburada'da
        var listingIds = new Dictionary<(int Sku, string Channel), int>();
        foreach (var sku in product.Skus)
        {
            foreach (var channel in new[] { ChannelCodes.Trendyol, ChannelCodes.Hepsiburada })
            {
                listingIds[(sku.Id, channel)] = await connection.ExecuteScalarAsync<int>("""
                    INSERT dbo.ChannelListing (SkuId, ChannelId, KanalUrunKodu, KanalBarkod)
                    OUTPUT inserted.Id VALUES (@skuId, @channelId, @code, @barcode)
                    """, new { skuId = sku.Id, channelId = channels[channel], code = $"{channel[..2]}-{sku.SkuCode}", barcode = sku.Barcode });
            }
        }

        // Siparişler: farklı kanal ve durumlarda
        var orders = new (string Channel, int SkuIndex, int Qty, OrderStatus Status)[]
        {
            (ChannelCodes.Trendyol, 0, 2, OrderStatus.Yeni),
            (ChannelCodes.Trendyol, 0, 1, OrderStatus.Kargoda),
            (ChannelCodes.Hepsiburada, 2, 1, OrderStatus.Onaylandi),
            (ChannelCodes.Hepsiburada, 1, 1, OrderStatus.Iade),
            (ChannelCodes.Trendyol, 2, 1, OrderStatus.Iptal),
            (ChannelCodes.Hepsiburada, 3, 2, OrderStatus.Yeni) // stoksuz → stok yetersiz
        };
        var orderNos = new List<string>();
        for (var i = 0; i < orders.Length; i++)
        {
            var (channel, skuIndex, qty, status) = orders[i];
            var sku = product.Skus[skuIndex];
            var no = $"DEMO-{tag}-S{i + 1}";
            await ingestion.IngestAsync(channel, new NormalizedOrder(no, DateTime.UtcNow.AddMinutes(-i * 7), status, 249.90m * qty,
                [new NormalizedOrderLine($"{channel[..2]}-{sku.SkuCode}", sku.Barcode, sku.SkuCode, qty, 249.90m, 37.49m)],
                new CustomerSummary("Ayşe Yılmaz", "İzmir", "Karşıyaka"), status >= OrderStatus.Kargoda ? new CargoInfo("Yurtiçi", "YK" + tag + i, null) : null), ct);
            orderNos.Add(no);
        }

        // Siparişlerden SONRA: son gönderilen ve mutabakat görüntüsü = hesaplanan değer (uyumlu);
        // yalnızca 3. SKU Hepsiburada'da beklenenden 1 fazla görünür → sapma.
        var targets = (await syncStore.ComputeTargetsAsync(connection, null, listingIds.Values.ToList(), ct)).ToDictionary(t => t.ListingId, t => t.Target);
        var deviationListing = listingIds[(product.Skus[2].Id, ChannelCodes.Hepsiburada)];
        foreach (var listingId in listingIds.Values)
        {
            var channelStock = listingId == deviationListing ? targets[listingId] + 1 : targets[listingId];
            await connection.ExecuteAsync("""
                UPDATE dbo.ChannelListing SET SonGonderilenStok = @sent, SonSenkron = SYSUTCDATETIME() WHERE Id = @listingId;
                INSERT dbo.ChannelStockSnapshot (ListingId, KanalStok) VALUES (@listingId, @channelStock);
                """, new { listingId, sent = targets[listingId], channelStock });
        }

        // Hatalı senkron: 1. SKU Trendyol gönderimi 5 denemede başarısız
        var failedSyncId = await connection.ExecuteScalarAsync<long>("""
            INSERT dbo.SyncQueue (ListingId, Tip, Durum, Deneme, Hata, SonDeneme)
            OUTPUT inserted.Id VALUES (@listingId, 'STOK', 'HATA', 5, N'Kanal hata döndü (HTTP 503): Servis geçici olarak kullanılamıyor', SYSUTCDATETIME())
            """, new { listingId = listingIds[(product.Skus[0].Id, ChannelCodes.Trendyol)] });

        return new DemoDataResult(tag, product.Skus.Select(s => s.SkuCode).ToList(), orderNos, failedSyncId,
            product.Skus[2].SkuCode, targets[deviationListing], targets[deviationListing] + 1);
    }
}
