using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PazaryeriHub.Adapters.Hepsiburada;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Domain.Orders;

namespace PazaryeriHub.Tests.Unit.Channels;

/// <summary>
/// Hepsiburada adaptörü contract testleri. Örnek yanıtlar developers.hepsiburada.com'daki alan açıklamalarına ve örneklere göre
/// hazırlanmıştır; gerçek hesapla alınmış kayıt DEĞİLDİR (SIT hesabı gelince gerçek yanıtlarla değiştirilmelidir).
/// </summary>
public sealed class HepsiburadaAdapterContractTests
{
    private const string MerchantId = "b2910839-83b9-4d45-adb6-86bad457edcb";
    private static readonly string Creds = $$"""{"merchantId":"{{MerchantId}}","userName":"hb-kullanici","password":"hb-servis-anahtari","environment":"Test"}""";

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Auth, string? UserAgent, string? Body)> Requests { get; } = [];
        public Func<Uri, (HttpStatusCode, string)> Respond { get; set; } = _ => (HttpStatusCode.OK, "[]");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(),
                string.Join(' ', request.Headers.GetValues("User-Agent")),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            var (status, body) = Respond(request.RequestUri!);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeCredentials(string? json) : IChannelCredentialStore
    {
        public Task<string?> GetJsonAsync(string channelCode, CancellationToken ct) => Task.FromResult(json);
        public Task SaveJsonAsync(string channelCode, string json, CancellationToken ct) => Task.CompletedTask;
        public Task ClearAsync(string channelCode, CancellationToken ct) => Task.CompletedTask;
    }

    private static (HepsiburadaAdapter Adapter, FakeHandler Http) Create(string? creds = null)
    {
        var handler = new FakeHandler();
        return (new HepsiburadaAdapter(new HttpClient(handler), new FakeCredentials(creds ?? Creds), TimeProvider.System,
            NullLogger<HepsiburadaAdapter>.Instance), handler);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Kimlik_dogrulanir_test_ortami_sit_adreslerini_kullanir()
    {
        var c = HepsiburadaCredentials.Parse(Creds);
        c.ListingBaseUrl.Should().Be("https://listing-external-sit.hepsiburada.com");
        c.OrderBaseUrl.Should().Be("https://oms-external-sit.hepsiburada.com");
        HepsiburadaCredentials.Parse(Creds.Replace("\"Test\"", "\"Prod\"")).ListingBaseUrl
            .Should().Be("https://listing-external.hepsiburada.com", "canlı = '-sit' çıkarılmış adres");
        c.ToString().Should().NotContain("hb-servis-anahtari");

        FluentActions.Invoking(() => HepsiburadaCredentials.Parse("""{"merchantId":"123","userName":"","password":"x","environment":"Canli"}"""))
            .Should().Throw<RequestValidationException>().Which.Errors.Keys.Should().BeEquivalentTo(["merchantId", "userName", "environment"]);
    }

    [Fact]
    public async Task Stok_hbsku_ile_gonderilir_basic_auth_ve_user_agent_eklenir()
    {
        var (adapter, http) = Create();
        http.Respond = _ => (HttpStatusCode.OK, """{"id":"715bae7f-2b90-4e88-9067-uhu7656892123"}""");

        var result = await adapter.PushStockAsync([new StockUpdate("HBV0000106NM0", null, 7), new StockUpdate("HBV0000106NLG", null, -2)], Ct);

        result.Should().Be(new BatchResult("715bae7f-2b90-4e88-9067-uhu7656892123", true));
        var r = http.Requests.Single();
        r.Method.Should().Be(HttpMethod.Post);
        r.Uri.ToString().Should().Be($"https://listing-external-sit.hepsiburada.com/listings/merchantid/{MerchantId}/stock-uploads");
        r.Auth.Should().Be("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("hb-kullanici:hb-servis-anahtari")));
        r.UserAgent.Should().NotBeNullOrWhiteSpace("User-Agent zorunlu");
        using var body = JsonDocument.Parse(r.Body!);
        body.RootElement.EnumerateArray().Select(i => (i.GetProperty("hepsiburadaSku").GetString(), i.GetProperty("availableStock").GetInt32()))
            .Should().Equal(("HBV0000106NM0", 7), ("HBV0000106NLG", 0));
        r.Body.Should().NotContain("price");
    }

    [Fact]
    public void Yukleme_sonucu_durumlara_cevrilir()
    {
        static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;

        HepsiburadaAdapter.MapUploadStatus("1", J("""{"id":"1","status":"InProgress","total":2,"errors":null}""")).State.Should().Be(BatchState.Pending);
        HepsiburadaAdapter.MapUploadStatus("1", J("""{"id":"1","status":"Done","createdAt":"2023-10-02T11:09:16.334Z","total":2,"errors":null}""")).State
            .Should().Be(BatchState.Completed);

        var partial = HepsiburadaAdapter.MapUploadStatus("1",
            J("""{"id":"1","status":"Done","total":2,"errors":[{"hepsiburadaSku":"HBV0000106NLG","message":"Listing bulunamadı"}]}"""));
        partial.State.Should().Be(BatchState.PartiallyFailed);
        partial.Errors.Should().ContainSingle().Which.Should().Be(new BatchItemError("HBV0000106NLG", null, "Hepsiburada: Listing bulunamadı"));
    }

    private const string OpenItems = """
        {"totalCount":2,"limit":100,"offset":0,"pageCount":1,"items":[
          {"id":"52910839-8369-4dg5-adb6-86bad457edcb","sku":"HBV0000106NM0","orderId":"o-1","orderNumber":"041241341","orderDate":"2018-12-21T09:34:47",
           "quantity":1,"merchantId":"b2910839-83b9-4d45-adb6-86bad457edcb","totalPrice":{"currency":"TRY","amount":301.4},"unitPrice":{"currency":"TRY","amount":301.4},
           "customerName":"Test User","status":"Open","shippingAddress":{"city":"İstanbul","town":"Sisli","district":"Kustepe","phoneNumber":"902822613231"},
           "commission":{"currency":"TRY","amount":45.2},"cargoCompany":"Yurtiçi Kargo"},
          {"id":"62910839-8369-4dg5-adb6-86bad457edcb","sku":"HBV0000106NLG","orderNumber":"041241341","orderDate":"2018-12-21T09:34:47",
           "quantity":2,"totalPrice":{"currency":"TRY","amount":200},"unitPrice":{"currency":"TRY","amount":100},"customerName":"Test User","status":"Open"}
        ]}
        """;

    [Fact]
    public async Task Siparisler_kaynaklarindan_toplanir_kalemler_siparise_gruplanir()
    {
        var (adapter, http) = Create();
        http.Respond = uri => uri.AbsolutePath switch
        {
            var p when p.EndsWith("/paymentawaiting", StringComparison.Ordinal) =>
                (HttpStatusCode.OK, """{"items":[{"id":"x","sku":"HBV1","merchantSku":"SC-9","OrderNumber":"0999","orderDate":"2026-11-16T08:00:00","quantity":1}]}"""),
            var p when p.EndsWith($"/orders/merchantid/{MerchantId}", StringComparison.Ordinal) => (HttpStatusCode.OK, OpenItems),
            var p when p.EndsWith("/shipped", StringComparison.Ordinal) =>
                (HttpStatusCode.OK, """{"items":[{"orderNumber":"041241341","Id":"d-1","PackageNumber":"P-1","Barcode":"YK123","ShippedDate":"2026-11-16"}]}"""),
            var p when p.EndsWith("/cancelled", StringComparison.Ordinal) =>
                (HttpStatusCode.OK, """{"items":[{"orderNumber":"0777","lineItemId":"l-1","cancelledBy":"Customer","quantity":1,"sku":"HBV2","merchantSku":"SC-2"}]}"""),
            _ => (HttpStatusCode.OK, """{"items":[]}""")
        };

        var orders = await adapter.FetchOrdersAsync(DateTime.UtcNow.AddHours(-1), Ct);

        var awaiting = orders.Single(o => o.ChannelOrderNo == "0999");
        awaiting.Status.Should().Be(OrderStatus.Yeni, "ödemesi beklenen sipariş için de stok rezerve edilir (doküman)");
        awaiting.Lines.Single().Should().Be(new NormalizedOrderLine("HBV1", null, "SC-9", 1, 0m));

        var open = orders.Single(o => o.ChannelOrderNo == "041241341" && o.Status == OrderStatus.Onaylandi);
        open.Lines.Should().HaveCount(2, "aynı sipariş numarasındaki kalemler tek siparişte");
        open.Lines[0].Should().Be(new NormalizedOrderLine("HBV0000106NM0", null, null, 1, 301.4m, 45.2m));
        open.TotalAmount.Should().Be(501.4m);
        open.Customer.Should().Be(new CustomerSummary("Test User", "İstanbul", "Sisli"), "ilçe = town (district mahalledir); telefon alınmaz");

        var shipped = orders.Single(o => o.Status == OrderStatus.Kargoda);
        shipped.ChannelOrderNo.Should().Be("041241341");
        shipped.Lines.Should().BeEmpty("kargo listesi satır taşımaz; yalnızca durum ilerletir");
        orders.Single(o => o.ChannelOrderNo == "0777").Status.Should().Be(OrderStatus.Iptal);

        var openRequest = http.Requests.First(r => r.Uri.AbsolutePath.EndsWith($"/orders/merchantid/{MerchantId}", StringComparison.Ordinal));
        openRequest.Uri.Host.Should().Be("oms-external-sit.hepsiburada.com");
        openRequest.Uri.Query.Should().Contain("offset=0").And.Contain("limit=100");
        http.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/shipped", StringComparison.Ordinal))
            .Should().OnlyContain(r => r.Uri.Query.Contains("limit=50") && r.Uri.Query.Contains("begindate="));
    }

    [Fact]
    public void Tarih_pencereleri_24_saati_ve_son_ayi_asmaz()
    {
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

        var windows = HepsiburadaAdapter.Windows(now.AddHours(-60), now).ToList();
        windows.Should().HaveCount(3);
        windows.Should().OnlyContain(w => w.End - w.Start <= TimeSpan.FromHours(24));
        windows[^1].End.Should().Be(now);

        HepsiburadaAdapter.Windows(now.AddDays(-90), now).First().Start.Should().Be(now.AddDays(-30), "servis yalnızca son 1 ayı döner");
        HepsiburadaAdapter.Format(new DateTime(2026, 11, 16, 9, 30, 0, DateTimeKind.Utc)).Should().Be("2026-11-16 12:30");
    }

    [Fact]
    public async Task Sayfalama_totalCounta_kadar_devam_eder()
    {
        var (adapter, http) = Create();
        var page = 0;
        http.Respond = uri =>
        {
            if (!uri.AbsolutePath.EndsWith($"/orders/merchantid/{MerchantId}", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, """{"items":[]}""");
            }
            var items = Enumerable.Range(0, page++ == 0 ? 100 : 20)
                .Select(i => $$"""{"id":"{{page}}-{{i}}","sku":"HB{{i}}","orderNumber":"N{{page}}-{{i}}","quantity":1,"unitPrice":{"amount":1} }""");
            return (HttpStatusCode.OK, "{\"totalCount\":120,\"items\":[" + string.Join(',', items) + "]}");
        };

        var orders = await adapter.FetchOrdersAsync(DateTime.UtcNow.AddMinutes(-5), Ct);

        orders.Count(o => o.Status == OrderStatus.Onaylandi).Should().Be(120);
        http.Requests.Where(r => r.Uri.AbsolutePath.EndsWith($"/orders/merchantid/{MerchantId}", StringComparison.Ordinal)).Select(r => r.Uri.Query)
            .Should().HaveCount(2).And.Contain(q => q.Contains("offset=100"));
    }

    [Fact]
    public async Task Kanal_stogu_listing_sorgusundan_okunur()
    {
        var (adapter, http) = Create();
        http.Respond = _ => (HttpStatusCode.OK, """{"listings":[{"HepsiburadaSku":"HBV0000106NM0","MerchantSku":"SC-1","AvailableStock":14,"IsSalable":true}]}""");

        var stock = await adapter.FetchStockAsync(["HBV0000106NM0"], Ct);

        stock.Should().ContainSingle().Which.Should().Be(new ChannelStock("HBV0000106NM0", null, 14));
        http.Requests.Single().Uri.ToString().Should().StartWith($"https://listing-external-sit.hepsiburada.com/listings/merchantid/{MerchantId}?offset=0&limit=100&hbSkuList=");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "", "API bilgileri hatalı", false)]
    [InlineData(HttpStatusCode.TooManyRequests, "", "istek sınırı", true)]
    [InlineData(HttpStatusCode.Conflict, """{"message":"Bu ürün paketlenemez."}""", "Hepsiburada: Bu ürün paketlenemez.", false)]
    [InlineData(HttpStatusCode.InternalServerError, "", "geçici bir hata", true)]
    public void Ham_hata_sade_mesaja_cevrilir(HttpStatusCode status, string body, string expected, bool transient)
    {
        var ex = HepsiburadaAdapter.Translate(status, body);
        ex.Message.Should().Contain(expected);
        ex.Transient.Should().Be(transient);
    }

    [Fact]
    public async Task Dogrulanmamis_ve_kapsam_disi_islemler_desteklenmiyor()
    {
        var (adapter, http) = Create();
        await FluentActions.Invoking(() => adapter.FetchReturnsAsync(DateTime.UtcNow, Ct)).Should().ThrowAsync<NotSupportedException>();
        await FluentActions.Invoking(() => adapter.PushPriceAsync([], Ct)).Should().ThrowAsync<NotSupportedException>();
        http.Requests.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ katalog (ADR-022)

    [Fact]
    public async Task Katalog_listing_sorgusundan_cekilir_hbsku_kanal_kodu_merchantsku_stok_kodu()
    {
        var (adapter, http) = Create();
        http.Respond = _ => (HttpStatusCode.OK, """
            {"listings":[{"HepsiburadaSku":"HBV0000106NM0","MerchantSku":"SC-1","AvailableStock":14,"IsSalable":true},
                         {"HepsiburadaSku":"HBV0000106NM1","MerchantSku":null,"AvailableStock":0,"IsSalable":false}]}
            """);

        var page = await adapter.FetchCatalogAsync("200", Ct);

        http.Requests.Single().Uri.ToString().Should().Be($"https://listing-external-sit.hepsiburada.com/listings/merchantid/{MerchantId}?offset=200&limit=100");
        page.NextCursor.Should().BeNull("100'den az kayıt → son sayfa");
        page.Items.Should().Equal(
            new ChannelCatalogEntry("HBV0000106NM0", null, "SC-1", null, null, null, null, null, null, "Satışta", false),
            new ChannelCatalogEntry("HBV0000106NM1", null, null, null, null, null, null, null, null, "Satışta değil", false));
    }
}
