using Dapper;
using Microsoft.Extensions.DependencyInjection;
using PazaryeriHub.Application.DepotMigration;
using PazaryeriHub.Application.Stock;
using PazaryeriHub.Infrastructure.DepotMigration;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.DepotMigration;

/// <summary>Depo Excel'i → ortak SKU geçişi (ADR-059): deneme yazmaz; uygulama SKU açar, stok sayar, ilan taşır, eşleşmeyen bağlar, eski SKU'yu sıfırlar.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class DepotMigrationServiceTests(TestDatabase db)
{
    private const string Channel = "SOFTCOTTON"; // mock kanal

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deneme_yazmaz_uygulama_gecisi_yapar_yeniden_calistirma_bir_sey_degistirmez()
    {
        db.SkipIfUnavailable();
        var p = "DG" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var skuA = await db.CreateSkuAsync($"{p}-A");
        var skuB = await db.CreateSkuAsync($"{p}-B");
        int channelId, l1, l2, l3, unmatched;
        await using (var c = db.Open())
        {
            channelId = await c.ExecuteScalarAsync<int>("SELECT Id FROM dbo.Channel WHERE Kod = @Channel", new { Channel });
            const string ins = "INSERT dbo.ChannelListing (SkuId, ChannelId, KanalBarkod) OUTPUT inserted.Id VALUES (@sku, @channelId, @b)";
            l1 = await c.ExecuteScalarAsync<int>(ins, new { sku = skuA, channelId, b = $"{p}-L1" });
            l2 = await c.ExecuteScalarAsync<int>(ins, new { sku = skuB, channelId, b = $"{p}-L2" });
            l3 = await c.ExecuteScalarAsync<int>(ins, new { sku = skuB, channelId, b = $"{p}-L3" });
            unmatched = await c.ExecuteScalarAsync<int>("""
                INSERT dbo.UnmatchedChannelItem (ChannelId, Kaynak, KanalAnahtar, KanalBarkod, UrunAdi, Neden)
                OUTPUT inserted.Id VALUES (@channelId, 'API', @k, @k, N'Gloria Bornoz', 'ESLESME_YOK')
                """, new { channelId, k = $"{p}-U1" });
        }
        using (var scope = db.Services.CreateScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IStockLedger>();
            await ledger.AdjustToCountAsync(new StockChangeRequest(skuA, db.DefaultWarehouseId, 9), Ct);
            await ledger.AdjustToCountAsync(new StockChangeRequest(skuB, db.DefaultWarehouseId, 4), Ct);
        }

        DepotRow[] rows = [new(2, $"{p} GLORIA BORNOZ", "YELLOW/SARI", "BORNOZ", "L", 7, $"{p}-GLORIA-SARI-L"),
                           new(3, $"{p} GLORIA BORNOZ", "DUSTY ROSE/GÜLKURUSU", "BORNOZ", "M", 0, $"{p}-GLORIA-GULKURUSU-M")];
        DepotAssignment[] assignments = [new(2, l1, null), new(2, l2, null), new(3, null, unmatched)];

        // Deneme: plan var, veritabanı değişmez
        var dry = await RunAsync(rows, assignments, apply: false);
        dry.Applied.Should().BeFalse();
        dry.Plan.Errors.Should().BeEmpty();
        dry.Plan.NewSkuCount.Should().Be(2);
        dry.Plan.Moves.Should().HaveCount(2);
        dry.Plan.ZeroOldSkus.Should().Equal(new PlannedZero(skuA, 9));
        await using (var c = db.Open())
        {
            (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Sku WHERE SkuKodu LIKE @p", new { p = p + "-GLORIA%" })).Should().Be(0);
        }

        // Uygulama
        var applied = await RunAsync(rows, assignments, apply: true);
        applied.Failures.Should().BeEmpty();
        (applied.SkusCreated, applied.StockCounted, applied.ListingsMoved, applied.UnmatchedLinked, applied.OldSkusZeroed).Should().Be((2, 2, 2, 1, 1));

        await using (var c = db.Open())
        {
            var newSkus = (await c.QueryAsync<(int Id, string SkuKodu, string? Renk, string? Beden, string Ad, string? Kategori)>("""
                SELECT s.Id, s.SkuKodu, s.Renk, s.Beden, p.Ad, p.Kategori FROM dbo.Sku s JOIN dbo.Product p ON p.Id = s.ProductId
                WHERE s.SkuKodu LIKE @p ORDER BY s.SkuKodu DESC
                """, new { p = p + "-GLORIA%" })).ToList();
            newSkus.Select(s => (s.Renk, s.Beden, s.Ad, s.Kategori)).Should().Equal(
                ("SARI", "L", $"{p} GLORIA BORNOZ", "BORNOZ"), ("GÜLKURUSU", "M", $"{p} GLORIA BORNOZ", "BORNOZ"));
            var sari = newSkus[0].Id;
            var gul = newSkus[1].Id;

            (await db.GetBalanceAsync(sari))!.Fiziksel.Should().Be(7);
            (await db.CountMovementsAsync(sari, "SAYIM")).Should().Be(1);
            (await db.CountOutboxAsync(sari)).Should().BeGreaterThan(0, "stok değişimi + outbox aynı transaction'da");
            (await c.QueryAsync<int>("SELECT Id FROM dbo.ChannelListing WHERE SkuId = @sari ORDER BY Id", new { sari })).Should().Equal(l1, l2);
            (await c.ExecuteScalarAsync<int>("SELECT SkuId FROM dbo.ChannelListing WHERE Id = @l3", new { l3 })).Should().Be(skuB, "atanmamış ilan yerinde kalır");
            (await c.ExecuteScalarAsync<string>("SELECT Durum FROM dbo.UnmatchedChannelItem WHERE Id = @unmatched", new { unmatched })).Should().Be("BAGLANDI");
            (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.ChannelListing WHERE SkuId = @gul AND KanalBarkod = @b", new { gul, b = $"{p}-U1" })).Should().Be(1);

            (await db.GetBalanceAsync(skuA))!.Fiziksel.Should().Be(0, "bütün ilanları taşınan eski SKU depo Excel'i esas alınarak sıfırlanır");
            (await db.GetBalanceAsync(skuB))!.Fiziksel.Should().Be(4, "ilanı kalan eski SKU'nun stoğu korunur");
        }

        // Yeniden çalıştırma: SKU'lar var → stok değişmez, ilanlar yerinde, eşleşmeyen bağlı
        var again = await RunAsync(rows, assignments, apply: true);
        again.Failures.Should().BeEmpty();
        (again.SkusCreated, again.StockCounted, again.ListingsMoved, again.UnmatchedLinked, again.OldSkusZeroed).Should().Be((0, 0, 0, 0, 0));
    }

    private async Task<DepotMigrationResult> RunAsync(DepotRow[] rows, DepotAssignment[] assignments, bool apply)
    {
        using var scope = db.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DepotMigrationService>().RunAsync(rows, assignments, apply, "test", Ct, db.DefaultWarehouseId);
    }
}
