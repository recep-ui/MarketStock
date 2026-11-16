using System.Text.Json;
using Dapper;
using PazaryeriHub.Application.Stock;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Stock;

/// <summary>Aşama 1 kabul kriteri (c): her stok değişikliğinde tam olarak bir hareket ve bir outbox kaydı oluşur.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class LedgerAuditTests(TestDatabase db)
{
    [Fact]
    public async Task Her_degisiklikte_bir_hareket_ve_bir_outbox_kaydi_olusur()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var sku = await db.CreateSkuAsync();
        var wh = db.DefaultWarehouseId;

        var steps = new (string Tip, Func<Task<StockChangeResult>> Run)[]
        {
            ("GIRIS",         () => db.Ledger.ReceiveAsync(new(sku, wh, 10, "TEST", "denetim-1", "u1"), ct)),
            ("REZERVE",       () => db.Ledger.ReserveAsync(new(sku, wh, 4, "SIPARIS", "S-1", "u1"), ct)),
            ("REZERVE_IPTAL", () => db.Ledger.CancelReservationAsync(new(sku, wh, 1, "SIPARIS", "S-1", "u1"), ct)),
            ("SEVK",          () => db.Ledger.ShipAsync(new(sku, wh, 3, "SIPARIS", "S-1", "u1"), ct)),
            ("IADE_GIRIS",    () => db.Ledger.ReceiveReturnAsync(new(sku, wh, 1, "IADE", "I-1", "u1"), ct)),
            ("SAYIM",         () => db.Ledger.AdjustToCountAsync(new(sku, wh, 6, "SAYIM", "SY-1", "u1"), ct))
        };

        var expectedCount = 0;
        foreach (var (tip, run) in steps)
        {
            var result = await run();
            expectedCount++;

            (await db.CountMovementsAsync(sku)).Should().Be(expectedCount, because: $"{tip} tam olarak bir hareket yazmalı");
            (await db.CountOutboxAsync(sku)).Should().Be(expectedCount, because: $"{tip} tam olarak bir outbox kaydı yazmalı");

            await using var connection = db.Open();
            var movement = await connection.QuerySingleAsync<(string Tip, string KaynakTip, string KaynakId, string Kullanici)>(
                "SELECT Tip, KaynakTip, KaynakId, Kullanici FROM dbo.StockMovement WHERE Id = @id", new { id = result.MovementId });
            movement.Tip.Should().Be(tip);
            movement.Kullanici.Should().Be("u1");

            var payload = await connection.QuerySingleAsync<string>(
                "SELECT Payload FROM dbo.Outbox WHERE CAST(JSON_VALUE(Payload, '$.HareketId') AS BIGINT) = @id", new { id = result.MovementId });
            using var json = JsonDocument.Parse(payload);
            json.RootElement.GetProperty("Tip").GetString().Should().Be(tip);
            json.RootElement.GetProperty("SkuId").GetInt32().Should().Be(sku);
            json.RootElement.GetProperty("Fiziksel").GetInt32().Should().Be(result.Physical);
            json.RootElement.GetProperty("Rezerve").GetInt32().Should().Be(result.Reserved);
            json.RootElement.GetProperty("Satilabilir").GetInt32().Should().Be(result.Available);
        }

        (await db.GetBalanceAsync(sku)).Should().BeEquivalentTo(new { Fiziksel = 6, Rezerve = 0 });
    }

    [Fact]
    public async Task Basarisiz_islem_hareket_ve_outbox_yazmaz()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var sku = await db.CreateSkuAsync();
        await db.Ledger.ReceiveAsync(new(sku, db.DefaultWarehouseId, 1), ct);

        var act = () => db.Ledger.ReserveAsync(new(sku, db.DefaultWarehouseId, 2), ct);

        await act.Should().ThrowAsync<InsufficientStockException>();
        (await db.CountMovementsAsync(sku)).Should().Be(1, "yalnızca GIRIS hareketi olmalı");
        (await db.CountOutboxAsync(sku)).Should().Be(1);
    }

    [Fact]
    public async Task Hareket_yazma_yordami_transaction_disinda_cagrilamaz()
    {
        db.SkipIfUnavailable();
        await using var connection = db.Open();

        var act = () => connection.ExecuteAsync("""
            DECLARE @h BIGINT;
            EXEC dbo.usp_StokHareketYaz 1, 1, 'GIRIS', 1, NULL, NULL, NULL, 1, 0, 1, @h OUTPUT;
            """);

        (await act.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>()).Which.Number.Should().Be(50020);
    }
}
