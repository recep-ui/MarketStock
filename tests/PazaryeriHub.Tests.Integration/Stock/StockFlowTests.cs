using Dapper;
using PazaryeriHub.Application.Stock;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Stock;

/// <summary>Aşama 1 kabul kriteri (b): rezerve → iptal → sevk akışı bakiyeleri doğru bırakır; hatalı işlemler hiçbir şey değiştirmez.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class StockFlowTests(TestDatabase db)
{
    private StockChangeRequest Req(int sku, int qty) => new(sku, db.DefaultWarehouseId, qty, "TEST", "akis", "test-kullanici");

    [Fact]
    public async Task Rezerve_iptal_sevk_akisi_bakiyeleri_dogru_birakir()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var sku = await db.CreateSkuAsync();

        var r1 = await db.Ledger.ReceiveAsync(Req(sku, 10), ct);
        r1.Should().BeEquivalentTo(new { Physical = 10, Reserved = 0, Available = 10 });

        var r2 = await db.Ledger.ReserveAsync(Req(sku, 4), ct);
        r2.Should().BeEquivalentTo(new { Physical = 10, Reserved = 4, Available = 6 });

        var r3 = await db.Ledger.CancelReservationAsync(Req(sku, 1), ct);
        r3.Should().BeEquivalentTo(new { Physical = 10, Reserved = 3, Available = 7 });

        var r4 = await db.Ledger.ShipAsync(Req(sku, 3), ct);
        r4.Should().BeEquivalentTo(new { Physical = 7, Reserved = 0, Available = 7 });

        var balance = await db.GetBalanceAsync(sku);
        balance.Should().BeEquivalentTo(new BalanceRow(Fiziksel: 7, Rezerve: 0, GuvenlikStogu: 0, Satilabilir: 7));
    }

    [Fact]
    public async Task Rezerveden_fazla_sevk_reddedilir_ve_hicbir_sey_degismez()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var sku = await db.CreateSkuAsync();
        await db.Ledger.ReceiveAsync(Req(sku, 10), ct);
        await db.Ledger.ReserveAsync(Req(sku, 2), ct);
        var movementsBefore = await db.CountMovementsAsync(sku);
        var outboxBefore = await db.CountOutboxAsync(sku);

        var act = () => db.Ledger.ShipAsync(Req(sku, 3), ct);

        (await act.Should().ThrowAsync<StockOperationException>()).Which.Code.Should().Be(StockErrorCode.InsufficientReservationForShipment);
        (await db.GetBalanceAsync(sku)).Should().BeEquivalentTo(new { Fiziksel = 10, Rezerve = 2 });
        (await db.CountMovementsAsync(sku)).Should().Be(movementsBefore);
        (await db.CountOutboxAsync(sku)).Should().Be(outboxBefore);
    }

    [Fact]
    public async Task Rezerveden_fazla_iptal_reddedilir()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var sku = await db.CreateSkuAsync();
        await db.Ledger.ReceiveAsync(Req(sku, 5), ct);
        await db.Ledger.ReserveAsync(Req(sku, 1), ct);

        var act = () => db.Ledger.CancelReservationAsync(Req(sku, 2), ct);

        (await act.Should().ThrowAsync<StockOperationException>()).Which.Code.Should().Be(StockErrorCode.InsufficientReservationForCancel);
        (await db.GetBalanceAsync(sku)).Should().BeEquivalentTo(new { Fiziksel = 5, Rezerve = 1 });
    }

    [Fact]
    public async Task Bakiyesi_olmayan_skuda_rezervasyon_stok_yetersiz_firlatir()
    {
        db.SkipIfUnavailable();
        var sku = await db.CreateSkuAsync();

        var act = () => db.Ledger.ReserveAsync(Req(sku, 1), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InsufficientStockException>();
        (await db.GetBalanceAsync(sku)).Should().BeNull();
        (await db.CountMovementsAsync(sku)).Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task Gecersiz_adet_reddedilir(int quantity)
    {
        db.SkipIfUnavailable();
        var sku = await db.CreateSkuAsync();

        var act = () => db.Ledger.ReceiveAsync(Req(sku, quantity), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<StockOperationException>()).Which.Code.Should().Be(StockErrorCode.InvalidQuantity);
    }

    [Fact]
    public async Task Iade_girisi_fiziksel_stogu_artirir()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var sku = await db.CreateSkuAsync();
        await db.Ledger.ReceiveAsync(Req(sku, 3), ct);

        var result = await db.Ledger.ReceiveReturnAsync(Req(sku, 2), ct);

        result.Physical.Should().Be(5);
        (await db.CountMovementsAsync(sku, "IADE_GIRIS")).Should().Be(1);
    }

    [Fact]
    public async Task Sayim_fiziksel_stogu_esitler_ve_farki_harekete_yazar()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var sku = await db.CreateSkuAsync();
        await db.Ledger.ReceiveAsync(Req(sku, 10), ct);
        await db.Ledger.ReserveAsync(Req(sku, 4), ct);

        var result = await db.Ledger.AdjustToCountAsync(Req(sku, 8), ct);

        result.Should().BeEquivalentTo(new { Physical = 8, Reserved = 4, Available = 4 });
        await using var connection = db.Open();
        var adet = await connection.ExecuteScalarAsync<int>(
            "SELECT Adet FROM dbo.StockMovement WHERE Id = @id AND Tip = 'SAYIM'", new { id = result.MovementId });
        adet.Should().Be(-2, "sayım hareketi işaretli farkı (8 - 10) tutar");
    }

    [Fact]
    public async Task Sayim_rezerveden_az_olamaz()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var sku = await db.CreateSkuAsync();
        await db.Ledger.ReceiveAsync(Req(sku, 10), ct);
        await db.Ledger.ReserveAsync(Req(sku, 4), ct);

        var act = () => db.Ledger.AdjustToCountAsync(Req(sku, 3), ct);

        (await act.Should().ThrowAsync<StockOperationException>()).Which.Code.Should().Be(StockErrorCode.CountBelowReserved);
        (await db.GetBalanceAsync(sku)).Should().BeEquivalentTo(new { Fiziksel = 10, Rezerve = 4 });
    }

    [Fact]
    public async Task Guvenlik_stogu_satilabiliri_dusurur_ama_rezervasyonu_engellemez()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var sku = await db.CreateSkuAsync();
        await db.Ledger.ReceiveAsync(Req(sku, 5), ct);
        await using (var connection = db.Open())
        {
            await connection.ExecuteAsync(
                "UPDATE dbo.StockBalance SET GuvenlikStogu = 2 WHERE SkuId = @sku", new { sku });
        }

        (await db.GetBalanceAsync(sku))!.Satilabilir.Should().Be(3);

        // Spesifikasyon §4: rezerve koşulu Fiziksel - Rezerve >= Adet (güvenlik stoğu dahil değil)
        var result = await db.Ledger.ReserveAsync(Req(sku, 5), ct);
        result.Should().BeEquivalentTo(new { Physical = 5, Reserved = 5, Available = -2 });
    }
}
