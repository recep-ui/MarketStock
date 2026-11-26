using Dapper;
using Microsoft.EntityFrameworkCore;
using PazaryeriHub.Application.Catalog;
using PazaryeriHub.Application.ChannelCatalog;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.DepotMigration;
using PazaryeriHub.Application.Stock;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.DepotMigration;

/// <summary>Geçişin sonucu: plan ve (uygulandıysa) yapılanlar.</summary>
public sealed record DepotMigrationResult(DepotMigrationPlan Plan, bool Applied, int SkusCreated, int StockCounted, int ListingsMoved, int UnmatchedLinked,
    int OldSkusZeroed, IReadOnlyList<string> Failures);

/// <summary>
/// Depo Excel'i → ortak SKU geçişi (ADR-059). Plan <see cref="DepotMigrationPlanner"/> ile kurulur; uygulama mevcut servislerle yapılır:
/// ürün/SKU (<see cref="ICatalogService"/>), stok sayımı (<see cref="IStockLedger.AdjustToCountAsync"/>: hareket + outbox aynı transaction'da),
/// ilan taşıma (<see cref="IChannelService.UpsertListingAsync"/>) ve eşleşmeyen bağlama (<see cref="IChannelCatalogService.LinkUnmatchedAsync"/>).
/// Her adım kendi transaction'ında çalışır; yarıda kalırsa aynı girdiyle yeniden çalıştırmak güvenlidir (var olan SKU'nun stoğuna dokunulmaz,
/// yerindeki ilan taşınmaz, bağlanmış kayıt atlanır). Fiyat kopyalanmaz: yeni SKU'da fiyat yok → kanala fiyat gitmez.
/// Kanala stok gönderimi kanal yeteneğine bağlıdır (kapalıysa hiçbir şey gönderilmez).
/// </summary>
public sealed class DepotMigrationService(
    PazaryeriHubDbContext db,
    SqlConnectionFactory connections,
    ICatalogService catalog,
    IChannelService channels,
    IChannelCatalogService channelCatalog,
    IStockLedger ledger)
{
    public const string SourceType = "DEPO_GECIS";

    public async Task<DepotState> ReadStateAsync(CancellationToken ct)
    {
        var codes = await db.Skus.AsNoTracking().Select(s => new { s.SkuCode, s.Id }).ToDictionaryAsync(s => s.SkuCode, s => s.Id, StringComparer.Ordinal, ct);
        var listings = await db.Listings.AsNoTracking().Select(l => new { l.Id, l.SkuId }).ToListAsync(ct);
        var pending = await db.UnmatchedChannelItems.AsNoTracking().Where(u => u.Status == UnmatchedStatuses.Pending).Select(u => u.Id).ToListAsync(ct);
        await using var c = connections.Create();
        var balances = (await c.QueryAsync<(int SkuId, int Physical, int Reserved)>(
                new CommandDefinition("SELECT SkuId, SUM(Fiziksel), SUM(Rezerve) FROM dbo.StockBalance GROUP BY SkuId", cancellationToken: ct)))
            .ToDictionary(b => b.SkuId, b => (b.Physical, b.Reserved));
        return new DepotState(
            codes,
            listings.ToDictionary(l => l.Id, l => l.SkuId),
            listings.GroupBy(l => l.SkuId).ToDictionary(g => g.Key, g => (IReadOnlyCollection<int>)g.Select(l => l.Id).ToList()),
            pending.ToHashSet(),
            balances);
    }

    /// <param name="warehouseId">Stok sayımının yapılacağı depo; verilmezse tek depo olmalı (ADR-015).</param>
    public async Task<DepotMigrationResult> RunAsync(IReadOnlyList<DepotRow> rows, IReadOnlyList<DepotAssignment> assignments, bool apply, string user,
        CancellationToken ct, int? warehouseId = null)
    {
        var plan = DepotMigrationPlanner.Plan(rows, assignments, await ReadStateAsync(ct));
        if (!apply || plan.Errors.Count > 0)
        {
            return new DepotMigrationResult(plan, false, 0, 0, 0, 0, 0, []);
        }

        if (warehouseId is null)
        {
            var warehouses = await catalog.GetWarehousesAsync(ct);
            if (warehouses.Count != 1)
            {
                throw new InvalidOperationException($"Geçiş tek depo varsayar; {warehouses.Count} depo var.");
            }
            warehouseId = warehouses[0].Id;
        }
        List<string> failures = [];

        // 1) Yeni SKU'lar: aynı modeldeki satırlar tek ürün altında
        var skuIdByRow = plan.Skus.Where(s => s.ExistingSkuId is not null).ToDictionary(s => s.Row.Row, s => s.ExistingSkuId!.Value);
        var created = 0;
        foreach (var model in plan.Skus.Where(s => s.ExistingSkuId is null).GroupBy(s => s.Row.Model.Trim(), StringComparer.Ordinal))
        {
            var first = model.First().Row;
            var product = await catalog.CreateProductAsync(new CreateProductRequest(first.Model.Trim(), null, Clean(first.Kind),
                model.Select(s => new SkuInput(s.Row.SkuCode.Trim(), null, Clean(s.Row.Color), Clean(s.Row.Size))).ToList()), ct);
            foreach (var s in model)
            {
                skuIdByRow[s.Row.Row] = product.Skus.Single(x => x.SkuCode == s.Row.SkuCode.Trim()).Id;
                created++;
            }
        }

        // 2) Stok: yalnızca bu çalıştırmada oluşturulan SKU'lar, depo Excel'indeki miktara sayılır
        var counted = 0;
        foreach (var s in plan.Skus.Where(s => s.ExistingSkuId is null))
        {
            await ledger.AdjustToCountAsync(new StockChangeRequest(skuIdByRow[s.Row.Row], warehouseId.Value, s.Row.Quantity, SourceType, $"depo-satir-{s.Row.Row}", user), ct);
            counted++;
        }

        // 3) İlanlar yeni SKU'ya
        var moved = 0;
        var listingInfo = await db.Listings.AsNoTracking().Where(l => plan.Moves.Select(m => m.ListingId).Contains(l.Id))
            .Select(l => new { l.Id, l.ChannelId, l.ChannelProductCode, l.ChannelBarcode, l.Status }).ToDictionaryAsync(l => l.Id, ct);
        foreach (var m in plan.Moves)
        {
            var l = listingInfo[m.ListingId];
            try
            {
                await channels.UpsertListingAsync(new ListingInput(skuIdByRow[m.Row.Row], l.ChannelId, l.ChannelProductCode, l.ChannelBarcode, l.Status, l.Id), ct);
                moved++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"İlan {m.ListingId} → depo satırı {m.Row.Row}: {ex.Message}");
            }
        }

        // 4) Eşleşmeyen kanal ürünleri yeni SKU'ya
        var linked = 0;
        foreach (var link in plan.Links)
        {
            try
            {
                await channelCatalog.LinkUnmatchedAsync(link.UnmatchedId, skuIdByRow[link.Row.Row], user, ct);
                linked++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"Eşleşmeyen {link.UnmatchedId} → depo satırı {link.Row.Row}: {ex.Message}");
            }
        }

        // 5) Bütün ilanları taşınan eski SKU'lar 0'a sayılır (yalnızca taşıma gerçekten olduysa)
        var zeroed = 0;
        var stillListed = await db.Listings.AsNoTracking().Select(l => l.SkuId).Distinct().ToListAsync(ct);
        foreach (var z in plan.ZeroOldSkus.Where(z => !stillListed.Contains(z.SkuId)))
        {
            await ledger.AdjustToCountAsync(new StockChangeRequest(z.SkuId, warehouseId.Value, 0, SourceType, "eski-sku", user), ct);
            zeroed++;
        }

        return new DepotMigrationResult(plan, true, created, counted, moved, linked, zeroed, failures);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
