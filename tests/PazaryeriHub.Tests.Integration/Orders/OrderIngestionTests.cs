using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using PazaryeriHub.Adapters.Mock;
using PazaryeriHub.Api.Endpoints;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Domain.Orders;
using PazaryeriHub.Infrastructure.BackgroundJobs;
using PazaryeriHub.Tests.Integration.Excel;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Orders;

/// <summary>
/// Aşama 3 kabul kriterleri: aynı sipariş 5 kez gelse tek kayıt ve tek rezervasyon oluşur; geçersiz durum geçişi reddedilir.
/// Ek: oversell bayrağı, iptal/sevk stok etkileri, kanal ileri atlaması, SiparisCek job'ı (mock adaptörle).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class OrderIngestionTests(TestDatabase db)
{
    private const string Channel = ChannelCodes.Trendyol;
    private static readonly JsonSerializerOptions Json = ExcelImportApiTests.Json;

    private static string NewOrderNo() => "TST-" + Guid.NewGuid().ToString("N")[..12];

    private static NormalizedOrder Order(string no, string barcode, int qty, OrderStatus status = OrderStatus.Yeni) =>
        new(no, DateTime.UtcNow, status, 99.90m * qty,
            [new NormalizedOrderLine("KANAL-" + barcode, barcode, null, qty, 99.90m, 15m)],
            new CustomerSummary("Ali Veli", "İstanbul", "Kadıköy"));

    private async Task<(int SkuId, string Barcode)> SkuWithStockAsync(int physical)
    {
        var barcode = "869" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999);
        var skuId = await db.CreateSkuAsync(barcode);
        if (physical > 0)
        {
            await db.Ledger.ReceiveAsync(new(skuId, db.DefaultWarehouseId, physical), TestContext.Current.CancellationToken);
        }
        return (skuId, barcode);
    }

    private async Task<IngestResult> IngestAsync(NormalizedOrder order, string channel = Channel)
    {
        using var scope = db.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IOrderIngestionService>()
            .IngestAsync(channel, order, TestContext.Current.CancellationToken);
    }

    private async Task<(int Orders, int Reserve, int Ship, int Release)> CountsAsync(string orderNo)
    {
        await using var connection = db.Open();
        return await connection.QuerySingleAsync<(int, int, int, int)>("""
            SELECT (SELECT COUNT(*) FROM dbo.[Order] WHERE KanalSiparisNo = @orderNo),
                   (SELECT COUNT(*) FROM dbo.StockMovement m JOIN dbo.OrderLine l ON m.KaynakId = CONCAT('OL:', l.Id) JOIN dbo.[Order] o ON o.Id = l.OrderId
                     WHERE o.KanalSiparisNo = @orderNo AND m.KaynakTip = 'SIPARIS_SATIRI' AND m.Tip = 'REZERVE'),
                   (SELECT COUNT(*) FROM dbo.StockMovement m JOIN dbo.OrderLine l ON m.KaynakId = CONCAT('OL:', l.Id) JOIN dbo.[Order] o ON o.Id = l.OrderId
                     WHERE o.KanalSiparisNo = @orderNo AND m.KaynakTip = 'SIPARIS_SATIRI' AND m.Tip = 'SEVK'),
                   (SELECT COUNT(*) FROM dbo.StockMovement m JOIN dbo.OrderLine l ON m.KaynakId = CONCAT('OL:', l.Id) JOIN dbo.[Order] o ON o.Id = l.OrderId
                     WHERE o.KanalSiparisNo = @orderNo AND m.KaynakTip = 'SIPARIS_SATIRI' AND m.Tip = 'REZERVE_IPTAL')
            """, new { orderNo });
    }

    // ------------------------------------------------------------------ Kabul: idempotentlik

    [Fact]
    public async Task Ayni_siparis_5_kez_arka_arkaya_gelse_tek_kayit_ve_tek_rezervasyon()
    {
        db.SkipIfUnavailable();
        var (skuId, barcode) = await SkuWithStockAsync(10);
        var order = Order(NewOrderNo(), barcode, 3);

        var results = new List<IngestResult>();
        for (var i = 0; i < 5; i++)
        {
            results.Add(await IngestAsync(order));
        }

        results.Count(r => r.Created).Should().Be(1);
        results.Select(r => r.OrderId).Distinct().Should().ContainSingle();
        (await CountsAsync(order.ChannelOrderNo)).Should().Be((1, 1, 0, 0));
        (await db.GetBalanceAsync(skuId)).Should().BeEquivalentTo(new { Fiziksel = 10, Rezerve = 3 });
        (await db.CountOutboxAsync(skuId, "REZERVE")).Should().Be(1);
    }

    [Fact]
    public async Task Ayni_siparis_5_kez_ayni_anda_gelse_tek_kayit_ve_tek_rezervasyon()
    {
        db.SkipIfUnavailable();
        var (skuId, barcode) = await SkuWithStockAsync(10);
        var order = Order(NewOrderNo(), barcode, 2);

        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            gate.Wait(TestContext.Current.CancellationToken);
            return await IngestAsync(order);
        })).ToList();
        gate.Set();
        var results = await Task.WhenAll(tasks);

        results.Count(r => r.Created).Should().Be(1, "yalnızca bir işleme siparişi oluşturabilir");
        results.Select(r => r.OrderId).Distinct().Should().ContainSingle();
        (await CountsAsync(order.ChannelOrderNo)).Should().Be((1, 1, 0, 0));
        (await db.GetBalanceAsync(skuId))!.Rezerve.Should().Be(2);
    }

    [Fact]
    public async Task SiparisCek_job_ortusen_pencerelerle_iki_kez_calissa_da_tek_kayit()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        const string channel = ChannelCodes.Softcotton;
        var (skuId, barcode) = await SkuWithStockAsync(5);
        var order = Order(NewOrderNo(), barcode, 1);
        db.Services.GetRequiredService<MockChannelStore>().UpsertOrder(channel, order);

        await using (var c = db.Open())
        {
            await c.ExecuteAsync("UPDATE dbo.Channel SET Aktif = 1 WHERE Kod = @channel", new { channel });
        }
        try
        {
            var first = await RunFetchJobAsync(channel, ct);
            var second = await RunFetchJobAsync(channel, ct);

            first.Skipped.Should().BeFalse();
            first.Created.Should().BeGreaterThanOrEqualTo(1);
            second.Fetched.Should().BeGreaterThanOrEqualTo(1, "örtüşmeli pencere aynı siparişi tekrar getirir");
            second.Created.Should().Be(0, "tekrar gelen sipariş yeni kayıt açmaz");
            (await CountsAsync(order.ChannelOrderNo)).Should().Be((1, 1, 0, 0));
            (await db.GetBalanceAsync(skuId))!.Rezerve.Should().Be(1);

            await using var c = db.Open();
            var state = await c.QuerySingleAsync<(DateTime? SonBasarili, int ArdisikHata)>("""
                SELECT s.SonBasarili, s.ArdisikHata FROM dbo.ChannelJobState s JOIN dbo.Channel ch ON ch.Id = s.ChannelId
                WHERE ch.Kod = @channel AND s.IsTipi = 'SIPARIS_CEK'
                """, new { channel });
            state.SonBasarili.Should().NotBeNull();
            state.ArdisikHata.Should().Be(0);
        }
        finally
        {
            await using var c = db.Open();
            await c.ExecuteAsync("UPDATE dbo.Channel SET Aktif = 0 WHERE Kod = @channel", new { channel });
        }
    }

    // ------------------------------------------------------------------ Kabul: geçersiz geçiş

    [Fact]
    public async Task Gecersiz_durum_gecisi_reddedilir_ve_siparis_degismez()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var (_, barcode) = await SkuWithStockAsync(5);
        var created = await IngestAsync(Order(NewOrderNo(), barcode, 1));

        async Task<HttpResponseMessage> Change(OrderStatus to) =>
            await db.Api.PostAsJsonAsync($"/api/orders/{created.OrderId}/status", new ChangeOrderStatusRequest(to), Json, ct);

        var skip = await Change(OrderStatus.TeslimEdildi);       // Yeni → TeslimEdildi: tek adımda olamaz
        skip.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await skip.Content.ReadAsStringAsync(ct)).Should().Contain("izin verilmiyor");

        (await Change(OrderStatus.Onaylandi)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Change(OrderStatus.Hazirlaniyor)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Change(OrderStatus.Kargoda)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Change(OrderStatus.Iptal)).StatusCode.Should().Be(HttpStatusCode.Conflict, "kargodaki sipariş iptal edilemez");
        (await Change(OrderStatus.Yeni)).StatusCode.Should().Be(HttpStatusCode.Conflict, "geri dönüş yok");

        var detail = await db.Api.GetFromJsonAsync<OrderDetail>($"/api/orders/{created.OrderId}", Json, ct);
        detail!.Status.Should().Be(OrderStatus.Kargoda);
        detail.Timeline.Select(e => e.To).Should().Equal(OrderStatus.Yeni, OrderStatus.Onaylandi, OrderStatus.Hazirlaniyor, OrderStatus.Kargoda);
        detail.Timeline.Skip(1).Should().OnlyContain(e => e.Source == "PANEL");
        detail.Lines.Should().ContainSingle().Which.Shipped.Should().BeTrue();
    }

    [Fact]
    public async Task Kanaldan_gelen_geri_donus_reddedilir_ileri_atlama_ara_adimlarla_uygulanir()
    {
        db.SkipIfUnavailable();
        var (skuId, barcode) = await SkuWithStockAsync(10);
        var no = NewOrderNo();
        await IngestAsync(Order(no, barcode, 4));

        var jump = await IngestAsync(Order(no, barcode, 4, OrderStatus.Kargoda));
        jump.StatusChanged.Should().BeTrue();
        (await db.GetBalanceAsync(skuId)).Should().BeEquivalentTo(new { Fiziksel = 6, Rezerve = 0 }, "Kargoda: sevk edildi");

        var stale = await IngestAsync(Order(no, barcode, 4, OrderStatus.Yeni));
        stale.RejectedTransition.Should().Contain("izin verilmiyor");

        var again = await IngestAsync(Order(no, barcode, 4, OrderStatus.Kargoda));
        again.StatusChanged.Should().BeFalse();
        (await CountsAsync(no)).Should().Be((1, 1, 1, 0), "tekrar gelen Kargoda ikinci kez sevk etmez");

        using var scope = db.Services.CreateScope();
        var detail = await scope.ServiceProvider.GetRequiredService<IOrderService>().GetAsync(jump.OrderId, TestContext.Current.CancellationToken);
        detail.Timeline.Select(e => e.To).Should().Equal(OrderStatus.Yeni, OrderStatus.Onaylandi, OrderStatus.Hazirlaniyor, OrderStatus.Kargoda);
        detail.Timeline.Should().OnlyContain(e => e.Source == "KANAL:TRENDYOL");
    }

    // ------------------------------------------------------------------ Stok etkileri

    [Fact]
    public async Task Stok_yetersizse_siparis_reddedilmez_bayrakla_kaydedilir_stok_gelince_rezerve_edilir()
    {
        db.SkipIfUnavailable();
        var (skuId, barcode) = await SkuWithStockAsync(1);
        var order = Order(NewOrderNo(), barcode, 2);

        var first = await IngestAsync(order);

        first.Created.Should().BeTrue("oversell'de sipariş reddedilmez");
        first.InsufficientStock.Should().BeTrue();
        (await CountsAsync(order.ChannelOrderNo)).Should().Be((1, 0, 0, 0));
        (await db.GetBalanceAsync(skuId))!.Rezerve.Should().Be(0);

        var list = await db.Api.GetFromJsonAsync<PagedResult<OrderListItem>>(
            $"/api/orders?insufficientStock=true&search={order.ChannelOrderNo}", Json, TestContext.Current.CancellationToken);
        list!.Items.Should().ContainSingle().Which.InsufficientStock.Should().BeTrue();

        await db.Ledger.ReceiveAsync(new(skuId, db.DefaultWarehouseId, 5), TestContext.Current.CancellationToken);
        var retry = await IngestAsync(order);

        retry.InsufficientStock.Should().BeFalse("stok geldikten sonraki işlemede rezerve edilir ve bayrak kalkar");
        (await CountsAsync(order.ChannelOrderNo)).Should().Be((1, 1, 0, 0));
        (await db.GetBalanceAsync(skuId))!.Rezerve.Should().Be(2);
    }

    [Fact]
    public async Task Iptal_rezervi_iade_eder_ve_tekrar_rezerve_edilmez()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var (skuId, barcode) = await SkuWithStockAsync(5);
        var order = Order(NewOrderNo(), barcode, 3);
        var created = await IngestAsync(order);
        (await db.GetBalanceAsync(skuId))!.Rezerve.Should().Be(3);

        var response = await db.Api.PostAsJsonAsync($"/api/orders/{created.OrderId}/status", new ChangeOrderStatusRequest(OrderStatus.Iptal), Json, ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await db.GetBalanceAsync(skuId)).Should().BeEquivalentTo(new { Fiziksel = 5, Rezerve = 0 });

        // Kanaldan eski "Yeni" durumuyla tekrar gelirse: Iptal son durum → reddedilir, rezerve geri gelmez
        var stale = await IngestAsync(order);
        stale.RejectedTransition.Should().NotBeNull();
        (await CountsAsync(order.ChannelOrderNo)).Should().Be((1, 1, 0, 1));
        (await db.GetBalanceAsync(skuId))!.Rezerve.Should().Be(0);
    }

    [Fact]
    public async Task Ilk_kez_kargoda_gelen_siparis_rezerve_ve_sevk_edilir()
    {
        db.SkipIfUnavailable();
        var (skuId, barcode) = await SkuWithStockAsync(4);
        var order = Order(NewOrderNo(), barcode, 4, OrderStatus.Kargoda);

        var result = await IngestAsync(order);

        result.Created.Should().BeTrue();
        (await CountsAsync(order.ChannelOrderNo)).Should().Be((1, 1, 1, 0));
        (await db.GetBalanceAsync(skuId)).Should().BeEquivalentTo(new { Fiziksel = 0, Rezerve = 0 });
    }

    [Fact]
    public async Task Eslesmeyen_urunlu_siparis_kaydedilir_stogu_etkilemez()
    {
        db.SkipIfUnavailable();
        var order = Order(NewOrderNo(), "BILINMEYEN-" + Guid.NewGuid().ToString("N")[..8], 1);

        var result = await IngestAsync(order);

        result.Created.Should().BeTrue();
        result.UnmatchedLines.Should().Be(1);
        result.InsufficientStock.Should().BeFalse();
        (await CountsAsync(order.ChannelOrderNo)).Should().Be((1, 0, 0, 0));
    }

    [Fact]
    public async Task Kanal_eslestirmesindeki_barkod_ile_sku_bulunur()
    {
        db.SkipIfUnavailable();
        var (skuId, _) = await SkuWithStockAsync(3);
        var channelBarcode = "TYB-" + Guid.NewGuid().ToString("N")[..10];
        await using (var c = db.Open())
        {
            await c.ExecuteAsync("""
                INSERT dbo.ChannelListing (SkuId, ChannelId, KanalBarkod) SELECT @skuId, Id, @channelBarcode FROM dbo.Channel WHERE Kod = 'TRENDYOL'
                """, new { skuId, channelBarcode });
        }

        var result = await IngestAsync(Order(NewOrderNo(), channelBarcode, 1));

        result.UnmatchedLines.Should().Be(0);
        (await db.GetBalanceAsync(skuId))!.Rezerve.Should().Be(1);
    }

    [Fact]
    public async Task Gecersiz_siparis_verisi_reddedilir()
    {
        db.SkipIfUnavailable();
        var bad = new NormalizedOrder(NewOrderNo(), DateTime.UtcNow, OrderStatus.Yeni, 10, [new NormalizedOrderLine(null, "X", null, 0, 10)]);

        var act = () => IngestAsync(bad);

        (await act.Should().ThrowAsync<RequestValidationException>()).Which.Errors.Should().ContainKey("lines[0].quantity");
        (await CountsAsync(bad.ChannelOrderNo)).Orders.Should().Be(0);
    }

    [Fact]
    public async Task Satirsiz_durum_guncellemesi_kayitli_siparisi_ilerletir_kayitsiz_siparisi_olusturmaz()
    {
        db.SkipIfUnavailable();
        var (skuId, barcode) = await SkuWithStockAsync(5);
        var order = Order(NewOrderNo(), barcode, 2);
        await IngestAsync(order);

        // Hepsiburada kargo listesi gibi: yalnızca sipariş no + durum
        var statusOnly = new NormalizedOrder(order.ChannelOrderNo, DateTime.UtcNow, OrderStatus.Kargoda, 0, []);
        var result = await IngestAsync(statusOnly);

        result.StatusChanged.Should().BeTrue();
        (await db.GetBalanceAsync(skuId)).Should().BeEquivalentTo(new { Fiziksel = 3, Rezerve = 0 }, "Kargoda: sevk edildi");

        var unknown = new NormalizedOrder(NewOrderNo(), DateTime.UtcNow, OrderStatus.Kargoda, 0, []);
        var act = () => IngestAsync(unknown);
        (await act.Should().ThrowAsync<RequestValidationException>()).Which.Errors.Should().ContainKey("lines");
        (await CountsAsync(unknown.ChannelOrderNo)).Orders.Should().Be(0);
    }

    // ------------------------------------------------------------------ Job durumları

    [Fact]
    public async Task Pasif_kanalda_job_atlanir_hata_durumunda_pencere_ilerlemez()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        const string channel = ChannelCodes.Beymen;

        (await RunFetchJobAsync(channel, ct)).Skipped.Should().BeTrue("Beymen başlangıçta pasif");

        await using (var c = db.Open())
        {
            await c.ExecuteAsync("UPDATE dbo.Channel SET Aktif = 1 WHERE Kod = @channel", new { channel });
        }
        try
        {
            await RunFetchJobAsync(channel, ct); // başarılı çalışma: SonBasarili oluşur
            var before = await JobStateAsync(channel);

            db.Services.GetRequiredService<MockChannelStore>().FailNextFetch[channel] = new HttpRequestException("kanal yanıt vermedi");
            var act = () => RunFetchJobAsync(channel, ct);
            await act.Should().ThrowAsync<HttpRequestException>();

            var after = await JobStateAsync(channel);
            after.SonBasarili.Should().Be(before.SonBasarili, "hata durumunda pencere ilerletilmez");
            after.ArdisikHata.Should().Be(1);
            after.SonHata.Should().Contain("kanal yanıt vermedi");

            await RunFetchJobAsync(channel, ct);
            (await JobStateAsync(channel)).ArdisikHata.Should().Be(0);
        }
        finally
        {
            await using var c = db.Open();
            await c.ExecuteAsync("UPDATE dbo.Channel SET Aktif = 0 WHERE Kod = @channel", new { channel });
        }
    }

    [Fact]
    public async Task Gelistirme_ucu_ile_mock_siparis_yerlestirilir_ve_liste_filtrelenir()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var (_, barcode) = await SkuWithStockAsync(2);
        var order = Order(NewOrderNo(), barcode, 1);

        (await db.Api.PostAsJsonAsync("/api/dev/mock/trendyol/orders", order, Json, ct)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await IngestAsync(order);

        var trendyolId = (await db.Api.GetFromJsonAsync<List<ChannelDto>>("/api/channels", Json, ct))!.Single(c => c.Code == Channel).Id;
        var list = await db.Api.GetFromJsonAsync<PagedResult<OrderListItem>>(
            $"/api/orders?channelId={trendyolId}&status=Yeni&search={order.ChannelOrderNo}", Json, ct);
        list!.Items.Should().ContainSingle().Which.Should().Match<OrderListItem>(o => o.ChannelCode == Channel && o.LineCount == 1);

        var detail = await db.Api.GetFromJsonAsync<OrderDetail>($"/api/orders/{list.Items[0].Id}", Json, ct);
        detail!.Customer.Should().BeEquivalentTo(new CustomerSummary("Ali Veli", "İstanbul", "Kadıköy"));
        detail.Lines.Single().Reserved.Should().BeTrue();

        (await db.Api.GetAsync("/api/orders/9223372036854775807", ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<OrderFetchResult> RunFetchJobAsync(string channel, CancellationToken ct)
    {
        using var scope = db.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OrderFetchJob>().RunAsync(channel, ct);
    }

    private async Task<(DateTime? SonBasarili, int ArdisikHata, string? SonHata)> JobStateAsync(string channel)
    {
        await using var c = db.Open();
        return await c.QuerySingleAsync<(DateTime?, int, string?)>("""
            SELECT s.SonBasarili, s.ArdisikHata, s.SonHata FROM dbo.ChannelJobState s JOIN dbo.Channel ch ON ch.Id = s.ChannelId
            WHERE ch.Kod = @channel AND s.IsTipi = 'SIPARIS_CEK'
            """, new { channel });
    }
}
