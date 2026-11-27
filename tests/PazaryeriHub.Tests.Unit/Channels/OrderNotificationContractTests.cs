using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PazaryeriHub.Adapters.Beymen;
using PazaryeriHub.Adapters.Hepsiburada;
using PazaryeriHub.Adapters.Mock;
using PazaryeriHub.Adapters.Trendyol;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Domain.Orders;
using PazaryeriHub.Infrastructure.Channels;

namespace PazaryeriHub.Tests.Unit.Channels;

/// <summary>
/// M2 (ADR-026): sipariş durumu bildirimi contract testleri ve yazma kapısı (GuardedChannelAdapter).
/// ÖRNEK YANITLAR / İSTEK ŞEMALARI GERÇEK YANIT DEĞİLDİR; resmi dokümanlardan alınmıştır (2026-11-18 okundu):
/// Trendyol getShipmentPackages (Order V2), Notify Packages (updatePackage), Shipping Alternative Delivery;
/// Beymen Order Processing (Picking), Manual Progression Of Order Statuses; Hepsiburada "Kalem veya Kalemleri Paketleme".
/// </summary>
public sealed class OrderNotificationContractTests
{
    private const string TrendyolCreds = """{"sellerId":12345,"apiKey":"test-key","apiSecret":"test-secret","environment":"Prod"}""";
    private const string BeymenCreds = """{"apiKey":"by-key","apiPassword":"by-password","environment":"Stage"}""";
    private const string HbMerchant = "b2910839-83b9-4d45-adb6-86bad457edcb";
    private static readonly string HbCreds = $$"""{"merchantId":"{{HbMerchant}}","userName":"hb-kullanici","password":"hb-servis-anahtari","environment":"Test"}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeCredentials(string? json) : IChannelCredentialStore
    {
        public Task<string?> GetJsonAsync(string channelCode, CancellationToken ct) => Task.FromResult(json);
        public Task SaveJsonAsync(string channelCode, string json, CancellationToken ct) => Task.CompletedTask;
        public Task ClearAsync(string channelCode, CancellationToken ct) => Task.CompletedTask;
    }

    private static (TrendyolAdapter, FakeHandler) Trendyol()
    {
        var h = new FakeHandler();
        return (new TrendyolAdapter(new HttpClient(h), new FakeCredentials(TrendyolCreds), TimeProvider.System, NullLogger<TrendyolAdapter>.Instance), h);
    }

    // ------------------------------------------------------------------ Trendyol Order V2 eşlemesi

    [Fact]
    public void Trendyol_V2_paketi_paket_ve_satir_kimlikleriyle_esleniri()
    {
        // Dokümandaki V2 örneğinin bu adaptörün kullandığı alanları (kişisel veri alanları çıkarıldı)
        var json = """
            { "orderNumber": "10654411111", "shipmentPackageId": 3330111111, "shipmentPackageStatus": "Created", "status": "Created",
              "packageTotalPrice": 498.90, "orderDate": 1762253333685, "cargoTrackingNumber": 7280027504111111,
              "cargoProviderName": "Trendyol Express", "packageHistories": [ { "createdDate": 1762860180000, "status": "Created" } ],
              "lines": [ { "quantity": 1, "stockCode": "111111", "lineId": 4765111111, "barcode": "8683772071724", "lineUnitPrice": 498.90 } ] }
            """;
        var package = JsonSerializer.Deserialize<ShipmentPackage>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var order = TrendyolAdapter.MapPackage(package)!;

        order.ChannelOrderNo.Should().Be("10654411111/3330111111");
        order.ChannelPackageId.Should().Be("3330111111");
        order.Status.Should().Be(OrderStatus.Yeni);
        order.TotalAmount.Should().Be(498.90m);
        order.OrderDateUtc.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1762253333685).UtcDateTime, "V2 orderDate alanı");
        order.Lines.Single().Should().Be(new NormalizedOrderLine("8683772071724", "8683772071724", "111111", 1, 498.90m, ChannelLineId: "4765111111"));
        order.Cargo!.TrackingNumber.Should().Be("7280027504111111");
    }

    // ------------------------------------------------------------------ Trendyol bildirim

    [Fact]
    public async Task Trendyol_hazirlaniyor_Picking_olarak_satir_kimlikleriyle_gider_Invoiced_gonderilmez()
    {
        var (adapter, http) = Trendyol();

        await adapter.UpdateOrderStatusAsync(new OrderStatusUpdate("10654411111/3330111111", null, OrderStatus.Hazirlaniyor,
            [new("4765111111", 3)], null, false), Ct);

        var r = http.Requests.Single();
        r.Method.Should().Be(HttpMethod.Put);
        r.Uri.Should().Be(new Uri("https://apigw.trendyol.com/integration/order/sellers/12345/shipment-packages/3330111111"),
            "paket kimliği istekte yoksa sipariş no'sunun '/' sonrasından çözülür");
        JsonDocument.Parse(r.Body!).RootElement.GetRawText().Should().Be("""{"lines":[{"lineId":4765111111,"quantity":3}],"params":{},"status":"Picking"}""");
    }

    [Fact]
    public async Task Trendyol_kendi_kargo_ile_kargoda_alternatif_teslimat_takip_linkiyle_gider_link_yoksa_istek_cikmaz()
    {
        var (adapter, http) = Trendyol();

        var noUrl = () => adapter.UpdateOrderStatusAsync(new OrderStatusUpdate("1/77", "77", OrderStatus.Kargoda, [], new CargoInfo("X", "123", null), true), Ct);
        await noUrl.Should().ThrowAsync<ArgumentException>();
        http.Requests.Should().BeEmpty();

        await adapter.UpdateOrderStatusAsync(new OrderStatusUpdate("1/77", "77", OrderStatus.Kargoda, [], new CargoInfo(null, null, "https://kargo.example/t/123"), true), Ct);
        var r = http.Requests.Single();
        r.Uri.AbsolutePath.Should().Be("/integration/order/sellers/12345/shipment-packages/77/alternative-delivery");
        JsonDocument.Parse(r.Body!).RootElement.GetRawText().Should().Be("""{"isPhoneNumber":false,"trackingInfo":"https://kargo.example/t/123","params":{}}""");
    }

    [Fact]
    public void Trendyol_kurallari_kanal_kargosunda_kargoda_bildirimi_gerektirmez()
    {
        var (adapter, _) = Trendyol();
        adapter.DescribeOrderNotification(OrderStatus.Hazirlaniyor, false).Should().Match<OrderNotificationRule>(r => r.Supported && r.RequiresLineIds);
        adapter.DescribeOrderNotification(OrderStatus.Kargoda, false).Should().Match<OrderNotificationRule>(r => r.NotRequired && !r.Supported);
        adapter.DescribeOrderNotification(OrderStatus.Kargoda, true).Should().Match<OrderNotificationRule>(r => r.Supported && r.RequiresTrackingUrl);
        adapter.DescribeOrderNotification(OrderStatus.TeslimEdildi, false).Supported.Should().BeFalse();
    }

    // ------------------------------------------------------------------ Beymen

    [Fact]
    public async Task Beymen_picking_ve_kendi_kargo_shipped_bildirimi_dokumandaki_uclara_gider()
    {
        var h = new FakeHandler();
        var adapter = new BeymenAdapter(new HttpClient(h), new FakeCredentials(BeymenCreds), TimeProvider.System, NullLogger<BeymenAdapter>.Instance);
        const string id = "5671f644-4c19-4bcc-ba4b-5d49ae2590a7";

        await adapter.UpdateOrderStatusAsync(new OrderStatusUpdate("BEY-1/MP1", id, OrderStatus.Hazirlaniyor, [], null, false), Ct);
        h.Requests[0].Method.Should().Be(HttpMethod.Put);
        h.Requests[0].Uri.Should().Be(new Uri($"https://stage-partner-api.beymen.com/orders/{id}/status/picking"));
        h.Requests[0].Body.Should().BeNull("picking gövdesiz");

        var missing = () => adapter.UpdateOrderStatusAsync(new OrderStatusUpdate("BEY-1/MP1", id, OrderStatus.Kargoda, [], new CargoInfo(null, "123", null), true), Ct);
        await missing.Should().ThrowAsync<ArgumentException>("kargo firması kodu zorunlu");

        await adapter.UpdateOrderStatusAsync(new OrderStatusUpdate("BEY-1/MP1", id, OrderStatus.Kargoda, [], new CargoInfo("YRTICI", "123", null), true), Ct);
        h.Requests.Should().HaveCount(2);
        h.Requests[1].Uri.AbsolutePath.Should().Be($"/orders/{id}/shipment/status");
        var body = JsonDocument.Parse(h.Requests[1].Body!).RootElement;
        body.GetProperty("trackingNumber").GetString().Should().Be("123");
        body.GetProperty("cargoCompanyCode").GetString().Should().Be("YRTICI");
        body.GetProperty("status").GetInt32().Should().Be(4, "Shipped = 4");

        var noId = () => adapter.UpdateOrderStatusAsync(new OrderStatusUpdate("BEY-1/MP1", null, OrderStatus.Hazirlaniyor, [], null, false), Ct);
        await noId.Should().ThrowAsync<ArgumentException>("Beymen sipariş id'si (GUID) olmadan istek çıkmaz");
    }

    // ------------------------------------------------------------------ Hepsiburada

    [Fact]
    public async Task Hepsiburada_hazirlaniyor_paketleme_istegiyle_gider_kendi_kargo_kargoda_desteklenmiyor()
    {
        var h = new FakeHandler();
        var adapter = new HepsiburadaAdapter(new HttpClient(h), new FakeCredentials(HbCreds), TimeProvider.System, NullLogger<HepsiburadaAdapter>.Instance);

        await adapter.UpdateOrderStatusAsync(new OrderStatusUpdate("041241341", null, OrderStatus.Hazirlaniyor, [new("k-1", 1), new("k-2", 2)], null, false), Ct);

        var r = h.Requests.Single();
        r.Method.Should().Be(HttpMethod.Post);
        r.Uri.Should().Be(new Uri($"https://oms-external-sit.hepsiburada.com/packages/merchantid/{HbMerchant}"));
        JsonDocument.Parse(r.Body!).RootElement.GetRawText().Should().Be("""{"lineItemRequests":[{"id":"k-1","quantity":1},{"id":"k-2","quantity":2}]}""");

        adapter.DescribeOrderNotification(OrderStatus.Kargoda, true).Supported.Should().BeFalse("paket numarası doğrulanmadı");
        var act = () => adapter.UpdateOrderStatusAsync(new OrderStatusUpdate("041241341", null, OrderStatus.Kargoda, [], new CargoInfo("X", "1", null), true), Ct);
        await act.Should().ThrowAsync<NotSupportedException>();
        h.Requests.Should().HaveCount(1);
    }

    // ------------------------------------------------------------------ Yazma kapısı

    private sealed class FakeGate(bool allow) : IChannelWriteGate
    {
        public List<string> Checked { get; } = [];

        public Task EnsureAllowedAsync(string channelCode, string capability, CancellationToken ct)
        {
            Checked.Add(capability);
            return allow ? Task.CompletedTask : throw new ChannelWriteBlockedException(channelCode, capability, "test");
        }

        public Task<string> GetStateAsync(int channelId, string capability, CancellationToken ct) => Task.FromResult(CapabilityStates.Closed);
    }

    [Fact]
    public async Task Kapi_kapaliyken_hicbir_yazma_istegi_kanala_cikmaz_okumalar_gecer()
    {
        var store = new MockChannelStore();
        var gate = new FakeGate(allow: false);
        var guarded = new GuardedChannelAdapter(new MockChannelAdapter("TEST", store), gate);

        var stock = () => guarded.PushStockAsync([new StockUpdate("K", "K", 5)], Ct);
        var price = () => guarded.PushPriceAsync([new PriceUpdate("K", "K", 10, 9)], Ct);
        var status = () => guarded.UpdateOrderStatusAsync(new OrderStatusUpdate("S1", null, OrderStatus.Hazirlaniyor, [], null, false), Ct);

        await stock.Should().ThrowAsync<ChannelWriteBlockedException>();
        await price.Should().ThrowAsync<ChannelWriteBlockedException>();
        await status.Should().ThrowAsync<ChannelWriteBlockedException>();
        gate.Checked.Should().Equal(ChannelCapabilities.Stock, ChannelCapabilities.Price, ChannelCapabilities.OrderStatus);
        store.StockOf("TEST").Should().BeEmpty();
        store.StatusUpdatesOf("S1").Should().BeEmpty();

        (await guarded.FetchOrdersAsync(DateTime.UtcNow.AddDays(-1), Ct)).Should().BeEmpty("okuma kapıdan geçmez");
        gate.Checked.Should().HaveCount(3);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add($"{request.Method} {request.RequestUri}");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"x"}""", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FixedCredentials(string json) : IChannelCredentialStore
    {
        public Task<string?> GetJsonAsync(string channelCode, CancellationToken ct) => Task.FromResult<string?>(json);
        public Task SaveJsonAsync(string channelCode, string json, CancellationToken ct) => Task.CompletedTask;
        public Task ClearAsync(string channelCode, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// ADR-039: fiyat yeteneği kanal mock'tayken açılmış olabilir (mock adaptör izin verir). Gerçek adaptörün kuralı (KDV doğrulanmadı)
    /// izin vermiyorsa, yetenek AÇIK olsa bile fiyat isteği gönderim anında engellenir ve kanala çıkmaz; stok etkilenmez.
    /// </summary>
    [Fact]
    public async Task Yetenek_acik_olsa_da_adaptor_kurali_izin_vermiyorsa_fiyat_kanala_cikmaz()
    {
        // KDV durumu doğrulanmamış kanal (ADR-065'ten beri Hepsiburada değil; kural mock ile kurulur)
        var store = new MockChannelStore();
        store.PriceRules["TEST"] = new PriceUpdateRule(true, null, false, false, "test: KDV dokümanda yok");
        store.FailPricePush["TEST"] = new InvalidOperationException("fiyat kanala çıktı");
        var adapter = new MockChannelAdapter("TEST", store);
        adapter.DescribePriceUpdate().CanEnable.Should().BeFalse("ön koşul: KDV durumu doğrulanmamış");
        var guarded = new GuardedChannelAdapter(adapter, new FakeGate(allow: true));

        var price = () => guarded.PushPriceAsync([new PriceUpdate("V1", null, 100m, 90m)], Ct);

        (await price.Should().ThrowAsync<ChannelWriteBlockedException>("fiyat isteği kanala çıkmamalı")).Which.Message.Should().Contain("KDV");

        var stock = await guarded.PushStockAsync([new StockUpdate("V1", null, 3)], Ct);
        stock.Accepted.Should().BeTrue("stok gönderimi etkilenmez");
    }

    [Fact]
    public async Task Kapi_aciksa_yazma_ic_adaptore_ulasir()
    {
        var store = new MockChannelStore();
        var guarded = new GuardedChannelAdapter(new MockChannelAdapter("TEST", store), new FakeGate(allow: true));

        await guarded.UpdateOrderStatusAsync(new OrderStatusUpdate("S1", null, OrderStatus.Hazirlaniyor, [new("L1", 1)], null, false), Ct);

        store.StatusUpdatesOf("S1").Single().Status.Should().Be(OrderStatus.Hazirlaniyor);
    }
}
