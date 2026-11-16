using PazaryeriHub.Application.Stock;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Stock;

/// <summary>Aşama 1 kabul kriteri (a): 100 paralel rezervasyonda stok eksiye düşmez.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class StockConcurrencyTests(TestDatabase db)
{
    [Fact]
    public async Task Yuz_paralel_rezervasyonda_stok_eksiye_dusmez()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var sku = await db.CreateSkuAsync();
        await db.Ledger.ReceiveAsync(new StockChangeRequest(sku, db.DefaultWarehouseId, 50), ct);

        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 100).Select(i => Task.Run(async () =>
        {
            gate.Wait(ct);
            try
            {
                await db.Ledger.ReserveAsync(new StockChangeRequest(sku, db.DefaultWarehouseId, 1, "TEST", $"paralel-{i}"), ct);
                return true;
            }
            catch (InsufficientStockException)
            {
                return false;
            }
        }, ct)).ToList();

        gate.Set(); // hepsini aynı anda başlat
        var results = await Task.WhenAll(tasks);

        results.Count(r => r).Should().Be(50, "yalnızca mevcut 50 adet rezerve edilebilir");
        results.Count(r => !r).Should().Be(50, "kalan 50 istek 'stok yetersiz' almalı");

        var balance = await db.GetBalanceAsync(sku);
        balance!.Fiziksel.Should().Be(50);
        balance.Rezerve.Should().Be(50);
        balance.Satilabilir.Should().Be(0);

        (await db.CountMovementsAsync(sku, "REZERVE")).Should().Be(50, "başarısız rezervasyon hareket yazmamalı");
        (await db.CountOutboxAsync(sku, "REZERVE")).Should().Be(50, "başarısız rezervasyon outbox yazmamalı");
    }

    [Fact]
    public async Task Farkli_miktarlarla_yuz_paralel_rezervasyonda_toplam_tutarli_kalir()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var sku = await db.CreateSkuAsync();
        await db.Ledger.ReceiveAsync(new StockChangeRequest(sku, db.DefaultWarehouseId, 100), ct);

        var quantities = Enumerable.Range(0, 100).Select(i => 1 + i % 3).ToArray(); // 1,2,3,... toplam 199 > 100
        using var gate = new ManualResetEventSlim(false);
        var tasks = quantities.Select(q => Task.Run(async () =>
        {
            gate.Wait(ct);
            try
            {
                await db.Ledger.ReserveAsync(new StockChangeRequest(sku, db.DefaultWarehouseId, q), ct);
                return q;
            }
            catch (InsufficientStockException)
            {
                return 0;
            }
        }, ct)).ToList();

        gate.Set();
        var reserved = (await Task.WhenAll(tasks)).Sum();

        var balance = await db.GetBalanceAsync(sku);
        balance!.Rezerve.Should().Be(reserved, "bakiye, başarılı rezervasyonların toplamına eşit olmalı");
        balance.Rezerve.Should().BeLessThanOrEqualTo(balance.Fiziksel);
        balance.Fiziksel.Should().Be(100);
        // Talep stoğu aştığı için serbest stok 3'ten az kalmış olmalı (yoksa en az bir 3'lük istek sığardı)
        (balance.Fiziksel - balance.Rezerve).Should().BeLessThan(3);
    }
}
