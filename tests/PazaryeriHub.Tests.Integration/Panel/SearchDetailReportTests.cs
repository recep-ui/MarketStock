using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Application.Panel;
using PazaryeriHub.Application.Reports;
using PazaryeriHub.Domain.Orders;
using PazaryeriHub.Tests.Integration.Excel;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Panel;

/// <summary>ADR-034: genel arama, SKU detay ekranı, satış raporu ve sipariş Excel'i.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class SearchDetailReportTests(TestDatabase db)
{
    private const string Channel = ChannelCodes.Trendyol;
    private static readonly JsonSerializerOptions Json = ExcelImportApiTests.Json;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique() => Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

    private sealed record TestSku(int Id, string Code, string Barcode, string ProductName);

    /// <summary>Teste özel ürün + SKU (benzersiz kod, barkod ve ürün adı) ve isteğe bağlı stok girişi.</summary>
    private async Task<TestSku> CreateSkuAsync(int physical = 0)
    {
        var tag = Unique();
        var code = "ARA-" + tag;
        var barcode = "869" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999);
        var name = "Arama Ürünü " + tag;
        await using (var connection = db.Open())
        {
            var id = await connection.ExecuteScalarAsync<int>("""
                DECLARE @p INT;
                INSERT dbo.Product (Ad, Marka, Kategori) VALUES (@name, N'Marka-T', N'Havlu');
                SET @p = SCOPE_IDENTITY();
                INSERT dbo.Sku (ProductId, SkuKodu, Barkod, Renk, Beden) VALUES (@p, @code, @barcode, N'Mavi', N'50x90');
                SELECT CAST(SCOPE_IDENTITY() AS INT);
                """, new { name, code, barcode });
            if (physical > 0)
            {
                await db.Ledger.ReceiveAsync(new(id, db.DefaultWarehouseId, physical), Ct);
            }
            return new TestSku(id, code, barcode, name);
        }
    }

    private async Task<IngestResult> IngestAsync(string orderNo, DateTime dateUtc, OrderStatus status, string barcode, int qty, decimal unitPrice = 100m)
    {
        using var scope = db.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IOrderIngestionService>().IngestAsync(Channel,
            new NormalizedOrder(orderNo, dateUtc, status, unitPrice * qty,
                [new NormalizedOrderLine(null, barcode, null, qty, unitPrice, 12m)],
                new CustomerSummary("Ayşe Yılmaz", "İzmir", "Konak")), Ct);
    }

    // ------------------------------------------------------------------ Genel arama

    [Fact]
    public async Task Genel_arama_SKU_kodu_kucuk_harfle_barkod_urun_adi_ve_siparis_no_ile_bulur()
    {
        db.SkipIfUnavailable();
        var sku = await CreateSkuAsync(physical: 5);
        var orderNo = "ARAMA-" + Unique();
        await IngestAsync(orderNo, DateTime.UtcNow, OrderStatus.Yeni, sku.Barcode, 1);

        async Task<SearchResult> Search(string q) =>
            (await db.Api.GetFromJsonAsync<SearchResult>($"/api/search?q={Uri.EscapeDataString(q)}", Json, Ct))!;

        var byCode = await Search(sku.Code[..8].ToLowerInvariant());
        byCode.Skus.Should().Contain(h => h.SkuId == sku.Id && h.MatchedBy == "SKU", "SKU kodu öneki büyük/küçük harf duyarsız aranır");

        var byBarcode = await Search(sku.Barcode);
        byBarcode.Skus.Should().ContainSingle(h => h.SkuId == sku.Id).Which.MatchedBy.Should().Be("BARKOD");

        var byName = await Search(sku.ProductName.ToLowerInvariant());
        var hit = byName.Skus.Should().ContainSingle(h => h.SkuId == sku.Id).Which;
        hit.MatchedBy.Should().Be("URUN_ADI");
        hit.Available.Should().Be(4, "5 giriş − 1 rezerve");
        hit.Variant.Should().Be("Mavi / 50x90");

        var byOrder = await Search(orderNo.ToLowerInvariant());
        byOrder.Orders.Should().ContainSingle(o => o.ChannelOrderNo == orderNo).Which.ChannelCode.Should().Be(Channel);
    }

    [Fact]
    public async Task Genel_arama_tek_karakterde_bos_doner_ve_tum_rollere_acik()
    {
        db.SkipIfUnavailable();
        var empty = await db.Api.GetFromJsonAsync<SearchResult>("/api/search?q=a", Json, Ct);
        empty!.Term.Should().BeNull();
        empty.Skus.Should().BeEmpty();
        empty.Orders.Should().BeEmpty();

        foreach (var role in Roles.All)
        {
            using var client = await db.ApiHost.CreateClientAsAsync(role);
            (await client.GetAsync("/api/search?q=ARA-", Ct)).StatusCode.Should().Be(HttpStatusCode.OK, role);
        }
        using var anonymous = db.ApiHost.CreateClient();
        (await anonymous.GetAsync("/api/search?q=ARA-", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ------------------------------------------------------------------ SKU detay

    [Fact]
    public async Task SKU_detay_bakiye_satis_hareket_ve_siparisleri_gosterir()
    {
        db.SkipIfUnavailable();
        var sku = await CreateSkuAsync(physical: 10);
        var recent = "DETAY-" + Unique();
        var old = "DETAY-" + Unique();
        var cancelled = "DETAY-" + Unique();
        await IngestAsync(recent, DateTime.UtcNow.AddDays(-1), OrderStatus.Yeni, sku.Barcode, 2, 50m);
        await IngestAsync(old, DateTime.UtcNow.AddDays(-20), OrderStatus.Yeni, sku.Barcode, 3, 50m);
        await IngestAsync(cancelled, DateTime.UtcNow.AddDays(-2), OrderStatus.Yeni, sku.Barcode, 1, 50m);
        await IngestAsync(cancelled, DateTime.UtcNow.AddDays(-2), OrderStatus.Iptal, sku.Barcode, 1, 50m);

        var detail = (await db.Api.GetFromJsonAsync<SkuDetail>($"/api/skus/{sku.Id}/detail", Json, Ct))!;

        detail.SkuCode.Should().Be(sku.Code);
        detail.ProductName.Should().Be(sku.ProductName);
        detail.Brand.Should().Be("Marka-T");
        detail.Color.Should().Be("Mavi");
        (detail.Physical, detail.Reserved, detail.Available).Should().Be((10, 5, 5), "iptal edilen rezerve geri döner");
        detail.Warehouses.Should().ContainSingle().Which.Physical.Should().Be(10);
        detail.Sales.Units7Days.Should().Be(2, "iptal sayılmaz; 20 gün önceki 7 güne girmez");
        detail.Sales.Units30Days.Should().Be(5);
        detail.Sales.Orders30Days.Should().Be(2);
        detail.Sales.Revenue30Days.Should().Be(250m);
        detail.Sales.LastSoldUtc.Should().BeCloseTo(DateTime.UtcNow.AddDays(-1), TimeSpan.FromMinutes(1));
        detail.RecentOrders.Select(o => o.ChannelOrderNo).Should().Equal([recent, cancelled, old], "en yeni sipariş önce");
        detail.RecentOrders.Single(o => o.ChannelOrderNo == cancelled).Status.Should().Be(OrderStatus.Iptal);
        detail.RecentMovements.Select(m => m.Type).Should().Contain(["GIRIS", "REZERVE", "REZERVE_IPTAL"]);
    }

    [Fact]
    public async Task SKU_detay_olmayan_SKU_icin_404()
    {
        db.SkipIfUnavailable();
        (await db.Api.GetAsync("/api/skus/2147483000/detail", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ Satış raporu

    [Fact]
    public async Task Satis_raporu_net_ciro_iptal_iade_ve_en_cok_satani_hesaplar()
    {
        db.SkipIfUnavailable();
        // Başka testlerin verisinden yalıtım: geçmişte rastgele bir gün seçilir ve rapor ÖNCE/SONRA farkıyla doğrulanır.
        // (Gelecek tarih kullanılmaz: "son satış" ve dönem raporlarında gelecekte görünürdü.)
        var day = new DateOnly(1996, 1, 1).AddDays(Random.Shared.Next(0, 6900));
        var noonUtc = day.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc); // 12:00 TR
        var url = $"/api/reports/sales?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}";
        var before = (await db.Api.GetFromJsonAsync<SalesReport>(url, Json, Ct))!;

        var sku = await CreateSkuAsync(physical: 20);
        await IngestAsync("RPR-A-" + Unique(), noonUtc, OrderStatus.Yeni, sku.Barcode, 2);          // net 200
        await IngestAsync("RPR-B-" + Unique(), noonUtc, OrderStatus.Kargoda, sku.Barcode, 1);       // net 100
        var cancelled = "RPR-C-" + Unique();
        await IngestAsync(cancelled, noonUtc, OrderStatus.Yeni, sku.Barcode, 1);
        await IngestAsync(cancelled, noonUtc, OrderStatus.Iptal, sku.Barcode, 1);                  // iptal
        var returned = "RPR-D-" + Unique();
        await IngestAsync(returned, noonUtc, OrderStatus.Kargoda, sku.Barcode, 1);
        await IngestAsync(returned, noonUtc, OrderStatus.Iade, sku.Barcode, 1);                    // iade 100

        var after = (await db.Api.GetFromJsonAsync<SalesReport>(url, Json, Ct))!;

        after.Period.Should().Be(new ReportPeriod(day, day));
        (after.Totals.TotalOrders - before.Totals.TotalOrders).Should().Be(4);
        (after.Totals.NetOrders - before.Totals.NetOrders).Should().Be(2);
        (after.Totals.CancelledOrders - before.Totals.CancelledOrders).Should().Be(1);
        (after.Totals.ReturnedOrders - before.Totals.ReturnedOrders).Should().Be(1);
        (after.Totals.NetRevenue - before.Totals.NetRevenue).Should().Be(300m);
        (after.Totals.ReturnedAmount - before.Totals.ReturnedAmount).Should().Be(100m);
        (after.Totals.Units - before.Totals.Units).Should().Be(3);
        after.Daily.Should().ContainSingle().Which.Revenue.Should().Be(after.Totals.NetRevenue);

        var channel = after.ByChannel.Single(c => c.ChannelCode == Channel);
        var channelBefore = before.ByChannel.Single(c => c.ChannelCode == Channel);
        (channel.NetRevenue - channelBefore.NetRevenue).Should().Be(300m);

        var top = after.TopSkus.Should().ContainSingle(t => t.SkuId == sku.Id).Which;
        top.Units.Should().Be(3);
        top.Revenue.Should().Be(300m);
        top.Orders.Should().Be(2);
        after.SlowMovers.Should().NotContain(s => s.SkuId == sku.Id, "dönemde satıldı");
    }

    [Fact]
    public async Task Satis_raporu_kanal_filtresi_ve_gecersiz_donem()
    {
        db.SkipIfUnavailable();
        int beymen;
        await using (var connection = db.Open())
        {
            beymen = await connection.ExecuteScalarAsync<int>("SELECT Id FROM dbo.Channel WHERE Kod = 'BEYMEN'");
        }
        var filtered = (await db.Api.GetFromJsonAsync<SalesReport>($"/api/reports/sales?channelId={beymen}", Json, Ct))!;
        filtered.ChannelId.Should().Be(beymen);
        filtered.ByChannel.Should().OnlyContain(c => c.ChannelCode == "BEYMEN");
        filtered.Period.Days.Should().Be(ReportPeriod.DefaultDays);

        (await db.Api.GetAsync("/api/reports/sales?from=2026-11-10&to=2026-11-01", Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await db.Api.GetAsync("/api/reports/sales?from=2020-01-01&to=2026-01-01", Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ------------------------------------------------------------------ Sipariş Excel'i

    [Fact]
    public async Task Siparis_Excel_filtreyle_satir_bazli_doner_musteri_bilgisi_icermez()
    {
        db.SkipIfUnavailable();
        var sku = await CreateSkuAsync(physical: 5);
        var orderNo = "XLS-" + Unique();
        await IngestAsync(orderNo, DateTime.UtcNow, OrderStatus.Yeni, sku.Barcode, 2, 75.5m);

        using var support = await db.ApiHost.CreateClientAsAsync(Roles.Support);
        var response = await support.GetAsync($"/api/orders/export?search={orderNo}", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

        using var workbook = new XLWorkbook(await response.Content.ReadAsStreamAsync(Ct));
        var sheet = workbook.Worksheet(1);
        var headers = sheet.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
        headers.Should().Contain(["Kanal", "Sipariş no", "Sipariş tarihi", "Durum", "SKU", "Ürün", "Adet", "Birim fiyat", "Satır tutarı"]);
        sheet.LastRowUsed()!.RowNumber().Should().Be(2, "tek sipariş, tek satır");

        string Cell(string header) => sheet.Cell(2, headers.IndexOf(header) + 1).GetString();
        Cell("Sipariş no").Should().Be(orderNo);
        Cell("Kanal").Should().Be("Trendyol");
        Cell("Durum").Should().Be("Yeni");
        Cell("SKU").Should().Be(sku.Code);
        sheet.Cell(2, headers.IndexOf("Adet") + 1).GetValue<int>().Should().Be(2);
        sheet.Cell(2, headers.IndexOf("Satır tutarı") + 1).GetValue<decimal>().Should().Be(151m);

        var allText = string.Join("|", sheet.CellsUsed().Select(c => c.GetString()));
        allText.Should().NotContain("Ayşe").And.NotContain("İzmir", "KVKK: müşteri bilgisi dışa aktarılmaz");
    }
}
