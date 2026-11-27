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

        /// <summary>Gövdeli isteklerin Content-Type başlığı (gövdesizde null).</summary>
        public List<string?> ContentTypes { get; } = [];
        public Func<Uri, (HttpStatusCode, string)> Respond { get; set; } = _ => (HttpStatusCode.OK, "[]");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ContentTypes.Add(request.Content?.Headers.ContentType?.ToString());
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
        // HB destek (2026-11-21): Content-Type charset parametresi olmadan gönderilmeli
        http.ContentTypes.Single().Should().Be("application/json");
    }

    [Fact]
    public async Task Fiyat_sayi_olarak_ve_charsetsiz_content_type_ile_gonderilir()
    {
        var (adapter, http) = Create();
        http.Respond = _ => (HttpStatusCode.OK, """{"id":"p-1"}""");

        await adapter.PushPriceAsync([new PriceUpdate("HBV0000106NM0", null, 199.90m, 199.90m)], Ct);

        using var body = JsonDocument.Parse(http.Requests.Single().Body!);
        var price = body.RootElement[0].GetProperty("price");
        price.ValueKind.Should().Be(JsonValueKind.Number, "HB destek: fiyat sayı (metin \"200.00\" 20000 kaydedildi)");
        price.GetDecimal().Should().Be(199.90m);
        http.ContentTypes.Single().Should().Be("application/json");
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

        // HB destek (2026-11-21): tamamlandı ancak "Done" + errors BOŞ ise. Boş dizi tamam; dizi olmayan dolu errors hata sayılır.
        HepsiburadaAdapter.MapUploadStatus("1", J("""{"id":"1","status":"Done","total":1,"errors":[]}""")).State.Should().Be(BatchState.Completed);
        HepsiburadaAdapter.MapUploadStatus("1", J("""{"id":"1","status":"Done","total":1}""")).State.Should().Be(BatchState.Completed);
        var odd = HepsiburadaAdapter.MapUploadStatus("1", J("""{"id":"1","status":"Done","total":1,"errors":{"code":"X","message":"Beklenmeyen"}}"""));
        odd.State.Should().Be(BatchState.Failed);
        odd.Errors.Should().ContainSingle().Which.Message.Should().Contain("Beklenmeyen");
        HepsiburadaAdapter.MapUploadStatus("1", J("""{"id":"1","status":"Done","total":1,"errors":"Hata metni"}""")).State.Should().Be(BatchState.Failed);
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
        awaiting.Lines.Single().Should().Be(new NormalizedOrderLine("HBV1", null, "SC-9", 1, 0m, ChannelLineId: "x"), "kalem id paketleme bildiriminde kullanılır");

        var open = orders.Single(o => o.ChannelOrderNo == "041241341" && o.Status == OrderStatus.Onaylandi);
        open.Lines.Should().HaveCount(2, "aynı sipariş numarasındaki kalemler tek siparişte");
        open.Lines[0].Should().Be(new NormalizedOrderLine("HBV0000106NM0", null, null, 1, 301.4m, 45.2m, ChannelLineId: "52910839-8369-4dg5-adb6-86bad457edcb"));
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

    // SIT'te kaydedilen gerçek yanıttan kısaltıldı (2026-11-21, "Saticiya Ait Paket Bilgilerini Listeleme"): kök dizi, paket başına items[].
    private const string OpenPackage = """
        {"id":"6b01ee32-9430-ca04-9e2d-e83d06060606","status":"Open","orderDate":"2026-11-21T22:16:59.437","barcode":"62750001308172",
         "packageNumber":"5000130817","cargoCompany":"hepsiJET","shippingDistrict":"Test","shippingTown":"Test","shippingCity":"İstanbul",
         "identityNo":"11111111111","totalPrice":{"currency":"TRY","amount":200},"customerName":"Test Alıcı",
         "items":[{"lineItemId":"6b01ee2b-9430-ca04-9e2d-e77306060606","hbSku":"HBV00001136ES","merchantSku":"8682344069282","quantity":1,
           "price":{"currency":"TRY","amount":200},"totalPrice":{"currency":"TRY","amount":200},"commission":{"currency":"TRY","amount":1},
           "orderNumber":"9196103341","orderDate":"2026-11-21T22:16:59.437"}]}
        """;

    [Fact]
    public async Task Paketlenmis_ama_kargoya_verilmemis_siparis_hazirlaniyor_olarak_gelir()
    {
        var (adapter, http) = Create();
        http.Respond = uri => uri.AbsolutePath.EndsWith($"/packages/merchantid/{MerchantId}", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, "[" + OpenPackage + "," + OpenPackage
                .Replace("\"Open\"", "\"Shipped\"", StringComparison.Ordinal).Replace("9196103341", "1111", StringComparison.Ordinal) + "]")
            : (HttpStatusCode.OK, """{"items":[]}""");

        var orders = await adapter.FetchOrdersAsync(DateTime.UtcNow.AddMinutes(-5), Ct);

        var packaged = orders.Should().ContainSingle(o => o.ChannelOrderNo == "9196103341").Which;
        packaged.Status.Should().Be(OrderStatus.Hazirlaniyor);
        packaged.Lines.Should().ContainSingle().Which.Should().Be(
            new NormalizedOrderLine("HBV00001136ES", null, "8682344069282", 1, 200m, 1m, ChannelLineId: "6b01ee2b-9430-ca04-9e2d-e77306060606"));
        packaged.TotalAmount.Should().Be(200m);
        packaged.OrderDateUtc.Should().Be(new DateTime(2026, 11, 21, 19, 16, 59, 437, DateTimeKind.Utc),
            "SIT: 19:16:59 UTC'de oluşturulan sipariş orderDate 22:16:59 döndü → saat dilimsiz tarih Türkiye saatidir");
        packaged.OrderDateUtc.Kind.Should().Be(DateTimeKind.Utc);
        packaged.ChannelPackageId.Should().Be("5000130817", "'kargoda' bildirimi paket numarası ister");
        packaged.Cargo.Should().Be(new CargoInfo("hepsiJET", "62750001308172", null));
        packaged.Customer.Should().Be(new CustomerSummary("Test Alıcı", "İstanbul", "Test"), "kimlik no, telefon, adres alınmaz");
        orders.Should().NotContain(o => o.ChannelOrderNo == "1111", "yalnızca SIT'te görülen 'Open' paket durumu tanınır; kargodakiler kendi listesinden gelir");

        var request = http.Requests.Single(r => r.Uri.AbsolutePath.EndsWith($"/packages/merchantid/{MerchantId}", StringComparison.Ordinal));
        request.Uri.Host.Should().Be("oms-external-sit.hepsiburada.com");
        request.Uri.Query.Should().Be("?offset=0&limit=10", "doküman: en fazla ve varsayılan 10 paket; tarih filtresi verilmez (bekleyen tüm paketler)");
    }

    [Fact]
    public async Task Paket_listesi_onar_onar_sayfalanir()
    {
        var (adapter, http) = Create();
        http.Respond = uri =>
        {
            if (!uri.AbsolutePath.EndsWith($"/packages/merchantid/{MerchantId}", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, """{"items":[]}""");
            }
            var count = uri.Query.Contains("offset=0", StringComparison.Ordinal) ? 10 : 3;
            var offset = uri.Query.Contains("offset=0", StringComparison.Ordinal) ? 0 : 10;
            var packages = Enumerable.Range(offset, count).Select(i => OpenPackage
                .Replace("9196103341", $"P{i}", StringComparison.Ordinal)
                .Replace("6b01ee2b-9430-ca04-9e2d-e77306060606", $"L{i}", StringComparison.Ordinal));
            return (HttpStatusCode.OK, "[" + string.Join(',', packages) + "]");
        };

        var orders = await adapter.FetchOrdersAsync(DateTime.UtcNow.AddMinutes(-5), Ct);

        orders.Count(o => o.Status == OrderStatus.Hazirlaniyor).Should().Be(13);
        http.Requests.Where(r => r.Uri.AbsolutePath.EndsWith($"/packages/merchantid/{MerchantId}", StringComparison.Ordinal))
            .Select(r => r.Uri.Query).Should().Equal("?offset=0&limit=10", "?offset=10&limit=10");
    }

    [Fact]
    public async Task Kismen_paketlenmis_siparis_tek_sipariste_birlesir_ve_onaylandi_kalir()
    {
        var (adapter, http) = Create();
        http.Respond = uri => uri.AbsolutePath switch
        {
            var p when p.EndsWith($"/orders/merchantid/{MerchantId}", StringComparison.Ordinal) => (HttpStatusCode.OK,
                """{"items":[{"id":"L-2","sku":"HBV2","orderNumber":"9196103341","orderDate":"2026-11-21T22:16:59","quantity":2,"unitPrice":{"amount":50},"totalPrice":{"amount":100}}]}"""),
            var p when p.EndsWith($"/packages/merchantid/{MerchantId}", StringComparison.Ordinal) => (HttpStatusCode.OK, "[" + OpenPackage + "]"),
            _ => (HttpStatusCode.OK, """{"items":[]}""")
        };

        var orders = await adapter.FetchOrdersAsync(DateTime.UtcNow.AddMinutes(-5), Ct);

        var order = orders.Should().ContainSingle(o => o.ChannelOrderNo == "9196103341",
            "aynı sipariş iki ayrı kayıtla gelirse ilk gelen eksik satırla kaydedilirdi").Which;
        order.Status.Should().Be(OrderStatus.Onaylandi, "paketlenmemiş kalem varken sipariş hazırlanıyor sayılmaz");
        order.Lines.Select(l => l.ChannelLineId).Should().BeEquivalentTo(["L-2", "6b01ee2b-9430-ca04-9e2d-e77306060606"]);
        order.TotalAmount.Should().Be(300m);
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
    public void Yetkisiz_hatasi_hangi_alana_ne_yazilacagini_soyler()
    {
        // SIT'te çalışan giriş: kullanıcı adı = Mağaza ID, şifre = servis anahtarı, User-Agent = entegratör kullanıcı adı (ADR-054)
        var message = HepsiburadaAdapter.Translate(HttpStatusCode.Unauthorized, "").Message;

        message.Should().Contain("API bilgileri hatalı")
            .And.Contain("Kullanıcı adı: Mağaza ID")
            .And.Contain("Şifre: servis anahtarı")
            .And.Contain("User-Agent: entegratör kullanıcı adı");
    }

    [Fact]
    public async Task Dogrulanmamis_ve_kapsam_disi_islemler_desteklenmiyor()
    {
        var (adapter, http) = Create();
        await FluentActions.Invoking(() => adapter.FetchReturnsAsync(DateTime.UtcNow, Ct)).Should().ThrowAsync<NotSupportedException>();
        http.Requests.Should().BeEmpty();
        // Fiyat: uygulandı (ADR-028); KDV dokümanda yok, kullanıcı onayıyla KDV dahil (ADR-065) → yetenek açılabilir (PriceContractTests)
        adapter.DescribePriceUpdate().CanEnable.Should().BeTrue();
    }

    // ------------------------------------------------------------------ katalog (ADR-022)

    [Fact]
    public async Task Katalog_listing_sorgusundan_cekilir_hbsku_kanal_kodu_merchantsku_stok_kodu()
    {
        var (adapter, http) = Create();
        http.Respond = _ => (HttpStatusCode.OK, """
            {"listings":[{"HepsiburadaSku":"HBV0000106NM0","MerchantSku":"SC-1","ProductName":"Chain Havlu","Price":349.9,"AvailableStock":14,"IsSalable":true},
                         {"HepsiburadaSku":"HBV0000106NM1","MerchantSku":null,"AvailableStock":0,"IsSalable":false}]}
            """);

        var page = await adapter.FetchCatalogAsync("200", Ct);

        http.Requests.Single(r => r.Uri.Host.StartsWith("listing", StringComparison.Ordinal)).Uri.ToString()
            .Should().Be($"https://listing-external-sit.hepsiburada.com/listings/merchantid/{MerchantId}?offset=200&limit=100");
        page.NextCursor.Should().BeNull("100'den az kayıt → son sayfa");
        page.Items.Should().Equal(
            new ChannelCatalogEntry("HBV0000106NM0", null, "SC-1", null, null, null, "Chain Havlu", null, null, "Satışta", false, "hHBV0000106NM0", SalePrice: 349.9m),
            new ChannelCatalogEntry("HBV0000106NM1", null, null, null, null, null, null, null, null, "Satışta değil", false, "hHBV0000106NM1"));
    }

    // ------------------------------------------------------------------ katalog: Mağaza Bazlı Ürün Bilgisi Listeleme (ADR-056)

    /// <summary>
    /// SIT'ten alınan gerçek yanıtın kısaltılmışı (2026-11-26, GET mpop-sit…/product/api/products/all-products-of-merchant/{id}).
    /// Kök: {success, code, totalElements, totalPages, number, last, data[]}; renk/beden variantTypeAttributes içinde.
    /// Aynı hbSku birden fazla satırda gelebiliyor (farklı merchantSku).
    /// </summary>
    private const string ProductInfoPage = """
        {"success":true,"code":0,"version":1,"message":null,"totalElements":3,"totalPages":1,"number":0,"numberOfElements":3,"first":true,"last":true,
         "data":[
          {"merchantSku":"8698642021921","barcode":"8698642021921","hbSku":"HBV1","variantGroupId":"ROYALKIMONO",
           "productName":"Soft cotton created by gökhan Royal Kimono","brand":"Soft Cotton","images":["https://productimages.hepsiburada.net/s/777/1500/1.jpg"],
           "categoryId":60005084,"categoryName":"Bornoz","tax":"18.0","price":"","description":"","status":"MATCHED",
           "baseAttributes":[{"name":"UrunAdi","value":"Soft cotton created by gökhan Royal Kimono","mandatory":true}],
           "variantTypeAttributes":[{"name":"renk_variant_property","value":"Beyaz","mandatory":true},{"name":"beden_variant_property","value":"S","mandatory":true}],
           "productAttributes":[{"name":"tipi","value":"Bornoz","mandatory":true}],"validationResults":[],"rejectReasons":[]},
          {"merchantSku":"HBV-ESKI","barcode":"1111111111111","hbSku":"HBV2","variantGroupId":"G2","productName":"Yanlış satır","brand":"X",
           "images":[],"categoryName":"Havlu","status":"MATCHED","variantTypeAttributes":[],"productAttributes":[]},
          {"merchantSku":"SC-HAVLU-2","barcode":"8680000000002","hbSku":"HBV2","variantGroupId":"G2","productName":"Chain Havlu 50x90","brand":"Soft Cotton",
           "images":["https://productimages.hepsiburada.net/s/1/500/2.jpg"],"categoryName":"Havlu","status":"MATCHED","variantTypeAttributes":[],"productAttributes":[]}
         ]}
        """;

    private const string TwoListings = """
        {"listings":[{"HepsiburadaSku":"HBV1","MerchantSku":"8698642021921","Price":899.9,"IsSalable":true},
                     {"HepsiburadaSku":"HBV2","MerchantSku":"SC-HAVLU-2","IsSalable":false},
                     {"HepsiburadaSku":"HBV3","MerchantSku":"YOK-1","IsSalable":true}]}
        """;

    [Fact]
    public async Task Katalog_urun_bilgisi_servisinden_barkod_ad_marka_renk_beden_ve_gorsel_eklenir()
    {
        var (adapter, http) = Create();
        http.Respond = u => (HttpStatusCode.OK, u.Host.StartsWith("mpop", StringComparison.Ordinal) ? ProductInfoPage : TwoListings);

        var page = await adapter.FetchCatalogAsync(null, Ct);

        http.Requests.Select(r => r.Uri.ToString()).Should().Contain(
            $"https://mpop-sit.hepsiburada.com/product/api/products/all-products-of-merchant/{MerchantId}?page=0&size=100");
        http.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
        page.Items[0].Should().Be(new ChannelCatalogEntry("HBV1", "8698642021921", "8698642021921", null, "Beyaz", "S",
            "Soft cotton created by gökhan Royal Kimono", "Soft Cotton", "Bornoz", "Satışta", false, "hHBV1",
            VariantAttributes: "Renk: Beyaz; Beden: S", SalePrice: 899.9m, ImageUrl: "https://productimages.hepsiburada.net/s/777/1500/1.jpg"));
        // Aynı hbSku iki satırda: listing'in merchantSku'suyla uyan satır seçilir
        page.Items[1].Barcode.Should().Be("8680000000002");
        page.Items[1].Name.Should().Be("Chain Havlu 50x90");
        // Ürün bilgisinde olmayan ilan eskisi gibi yalnızca listing alanlarıyla gelir
        page.Items[2].Should().Be(new ChannelCatalogEntry("HBV3", null, "YOK-1", null, null, null, null, null, null, "Satışta", false, "hHBV3"));
    }

    [Fact]
    public async Task Urun_bilgisi_tum_sayfalari_bir_kez_cekilir_sonraki_katalog_sayfalarinda_tekrar_cekilmez()
    {
        var (adapter, http) = Create();
        http.Respond = u =>
        {
            if (!u.Host.StartsWith("mpop", StringComparison.Ordinal))
            {
                var many = string.Join(',', Enumerable.Range(0, 100).Select(i => $$"""{"HepsiburadaSku":"HBV{{i}}","MerchantSku":"M{{i}}"}"""));
                return (HttpStatusCode.OK, $$"""{"listings":[{{many}}]}""");
            }
            // 1. sayfa dolu (100 satır), 2. sayfa son
            var rows = string.Join(',', Enumerable.Range(0, 100).Select(i => $$"""{"hbSku":"HBV{{i}}","barcode":"B{{i}}"}"""));
            return u.Query.Contains("page=1", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, """{"success":true,"number":1,"last":true,"data":[]}""")
                : (HttpStatusCode.OK, $$"""{"success":true,"number":0,"last":false,"data":[{{rows}}]}""");
        };

        var first = await adapter.FetchCatalogAsync(null, Ct);
        await adapter.FetchCatalogAsync(first.NextCursor, Ct);

        http.Requests.Where(r => r.Uri.Host.StartsWith("mpop", StringComparison.Ordinal)).Select(r => r.Uri.Query)
            .Should().Equal("?page=0&size=100", "?page=1&size=100");
    }

    [Fact]
    public async Task Urun_bilgisi_servisi_hata_verirse_katalog_listing_alanlariyla_devam_eder()
    {
        var (adapter, http) = Create();
        http.Respond = u => u.Host.StartsWith("mpop", StringComparison.Ordinal)
            ? (HttpStatusCode.Forbidden, """{"message":"yetkisiz"}""")
            : (HttpStatusCode.OK, TwoListings);

        var page = await adapter.FetchCatalogAsync(null, Ct);

        page.Items.Should().HaveCount(3);
        page.Items[0].Barcode.Should().BeNull();
        page.Items[0].StockCode.Should().Be("8698642021921");
    }

    [Fact]
    public void Urun_bilgisi_adresi_canlida_sit_eki_olmadan()
    {
        HepsiburadaCredentials.Parse($$"""{"merchantId":"{{MerchantId}}","userName":"{{MerchantId}}","password":"p","environment":"Prod"}""").ProductBaseUrl
            .Should().Be("https://mpop.hepsiburada.com/product");
        HepsiburadaCredentials.Parse(Creds).ProductBaseUrl.Should().Be("https://mpop-sit.hepsiburada.com/product");
    }

    [Theory]
    // Canlıda (2026-11-26) barkodsuz ürünlerde HB "<satıcı adı><GUID parçası>" üretiyor: 187 değerin hepsi bu biçimde, 37 karakter.
    [InlineData("SoftCottonCreatedbyGökhan090cad47-469", true)]
    [InlineData("SoftCottonCreatedbyGökhan090cad47-4690", true)]
    [InlineData("8698642021921", false)]
    [InlineData("LordHavlu0005", false)]
    [InlineData("SC-GLOR-BRN-1P-M-PUDRA", false)]
    public void Hepsiburadanin_urettigi_barkod_taninir(string barcode, bool generated) =>
        HepsiburadaAdapter.IsGeneratedBarcode(barcode).Should().Be(generated);

    [Fact]
    public async Task Hepsiburadanin_urettigi_barkod_yerine_stok_kodu_barkod_olur()
    {
        var (adapter, http) = Create();
        http.Respond = u => (HttpStatusCode.OK, u.Host.StartsWith("mpop", StringComparison.Ordinal)
            ? """{"last":true,"data":[{"merchantSku":"LOFT1","barcode":"SoftCottonCreatedbyGökhan67484b91-3ce","hbSku":"HBV9","productName":"LOFT Paspas"},{"merchantSku":"X2","barcode":"SoftCottonCreatedbyGökhan11111111-222","hbSku":"HBV8","productName":"Kodsuz"}]}"""
            : """{"listings":[{"HepsiburadaSku":"HBV9","MerchantSku":"LOFT1"},{"HepsiburadaSku":"HBV8","MerchantSku":null}]}""");

        var page = await adapter.FetchCatalogAsync(null, Ct);

        page.Items[0].Barcode.Should().Be("LOFT1");
        page.Items[0].StockCode.Should().Be("LOFT1");
        // Listing'de stok kodu yoksa ürün bilgisindeki stok kodu kullanılır
        page.Items[1].Barcode.Should().Be("X2");
    }
}
