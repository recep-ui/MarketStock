using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PazaryeriHub.Adapters.Beymen;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Domain.Orders;

namespace PazaryeriHub.Tests.Unit.Channels;

/// <summary>
/// Beymen adaptörü contract testleri. Örnek yanıtlar https://partner-docs.beymen.com/en adresindeki şema ve örneklere göre
/// hazırlanmıştır; gerçek hesapla alınmış kayıt DEĞİLDİR (stage erişimi gelince gerçek yanıtlarla değiştirilmelidir).
/// </summary>
public sealed class BeymenAdapterContractTests
{
    private const string Creds = """{"apiKey":"by-key","apiPassword":"by-password","environment":"Stage"}""";

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Auth, string? Body)> Requests { get; } = [];
        public Func<Uri, (HttpStatusCode, string)> Respond { get; set; } = _ => (HttpStatusCode.OK, """{"items":[]}""");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(),
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

    private static (BeymenAdapter Adapter, FakeHandler Http) Create()
    {
        var handler = new FakeHandler();
        return (new BeymenAdapter(new HttpClient(handler), new FakeCredentials(Creds), TimeProvider.System, NullLogger<BeymenAdapter>.Instance), handler);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Kimlik_dogrulanir_ve_ortam_adresi_secilir()
    {
        BeymenCredentials.Parse(Creds).BaseUrl.Should().Be("https://stage-partner-api.beymen.com");
        BeymenCredentials.Parse(Creds.Replace("Stage", "Prod")).BaseUrl.Should().Be("https://partner-api.beymen.com");
        BeymenCredentials.Parse(Creds).ToString().Should().NotContain("by-password");
        FluentActions.Invoking(() => BeymenCredentials.Parse("""{"apiKey":"","apiPassword":"x","environment":"Live"}"""))
            .Should().Throw<RequestValidationException>().Which.Errors.Keys.Should().BeEquivalentTo(["apiKey", "environment"]);
    }

    [Fact]
    public async Task Toplu_stok_productId_ile_gonderilir()
    {
        var (adapter, http) = Create();
        http.Respond = _ => (HttpStatusCode.OK, """{"processId":"a218a046-08dc-476d-a1d6-5d463ea2e418","message":"Your bulk price and stock update request has been received."}""");

        var result = await adapter.PushStockAsync(
            [new StockUpdate("3fa85f64-5717-4562-b3fc-2c963f66afa6", "BRK-1", 12), new StockUpdate("4fa85f64-5717-4562-b3fc-2c963f66afa7", null, 2_000_000)], Ct);

        result.Should().Be(new BatchResult("a218a046-08dc-476d-a1d6-5d463ea2e418", true));
        var r = http.Requests.Single();
        r.Method.Should().Be(HttpMethod.Post);
        r.Uri.ToString().Should().Be("https://stage-partner-api.beymen.com/batch/products/price-stock");
        r.Auth.Should().Be("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("by-key:by-password")));
        using var body = JsonDocument.Parse(r.Body!);
        body.RootElement.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("productId").GetString(), i.GetProperty("quantity").GetInt32()))
            .Should().Equal(("3fa85f64-5717-4562-b3fc-2c963f66afa6", 12), ("4fa85f64-5717-4562-b3fc-2c963f66afa7", 1_000_000));
        r.Body.Should().NotContain("salesPrice").And.NotContain("platformPrice");
    }

    [Fact]
    public async Task Batch_sonucu_eslesme_tamamlanmadiysa_beklemede()
    {
        var (adapter, http) = Create();
        var call = 0;
        http.Respond = _ => call++ == 0
            ? (HttpStatusCode.BadRequest, """{"message":"The matching is incomplete."}""")
            : (HttpStatusCode.OK, """
                {"processId":"p-1","items":[
                  {"productId":"3fa85f64-5717-4562-b3fc-2c963f66afa6","success":true,"message":"The operation was successful."},
                  {"productId":"4fa85f64-5717-4562-b3fc-2c963f66afa7","success":false,"message":"Different price information was detected for size variants of the same product."}]}
                """);

        (await adapter.CheckBatchAsync("p-1", Ct)).State.Should().Be(BatchState.Pending, "HTTP 400 'The matching is incomplete.' = henüz işlenmedi");
        var done = await adapter.CheckBatchAsync("p-1", Ct);

        http.Requests[0].Uri.ToString().Should().Be("https://stage-partner-api.beymen.com/batch/products/price-stock/p-1");
        done.State.Should().Be(BatchState.PartiallyFailed);
        done.Errors.Should().ContainSingle().Which.ChannelProductCode.Should().Be("4fa85f64-5717-4562-b3fc-2c963f66afa7");
    }

    private const string OrdersPage = """
        {"items":[
          {"id":"1","orderNumber":"BY1001","orderDate":"2026-11-16T09:00:00Z","shipmentNumber":"S-1","shipmentProvider":"YRTICI",
           "shipmentTrackingNumber":"TRK-1","customerName":"Zeynep Kaya","totalAmount":1499.90,"shipmentStatus":1,
           "shipmentAddress":{"customerName":"Zeynep Kaya","city":"Ankara","district":"Çankaya","neighborhood":"Kızılay","phoneNumber":"0555"},
           "shipmentLines":[{"id":"l1","productId":"3fa85f64-5717-4562-b3fc-2c963f66afa6","quantity":1,"price":1499.90,"commission":224.99,
                             "product":{"barcode":"BRK-1","stockCode":"SC-1"}}]},
          {"id":"2","orderNumber":"BY1002","orderDate":"2026-11-15T09:00:00Z","shipmentNumber":"S-2","shipmentStatus":16,"isReturnedShipment":true,"shipmentLines":[]},
          {"id":"3","orderNumber":"BY1003","shipmentNumber":"S-3","shipmentStatus":32,"shipmentLines":[]},
          {"id":"4","orderNumber":"BY1004","shipmentStatus":999}
        ],"page":0,"itemsPerPage":100,"pageCount":1,"total":4}
        """;

    [Fact]
    public async Task Siparisler_normalize_edilir_ve_iki_sorgu_yapilir()
    {
        var (adapter, http) = Create();
        http.Respond = uri => uri.Query.Contains("shipmentStatus=Cancelled") ? (HttpStatusCode.OK, """{"items":[]}""") : (HttpStatusCode.OK, OrdersPage);

        var orders = await adapter.FetchOrdersAsync(DateTime.UtcNow.AddMinutes(-15), Ct);

        http.Requests.Should().HaveCount(2);
        http.Requests.Should().OnlyContain(r => r.Uri.AbsolutePath == "/orders" && r.Uri.Query.Contains("startDate=") && r.Uri.Query.Contains("endDate="));
        http.Requests[1].Uri.Query.Should().Contain("shipmentStatus=Cancelled");

        orders.Should().HaveCount(3, "bilinmeyen durum atlanır");
        var first = orders.Single(o => o.ChannelOrderNo == "BY1001/S-1");
        first.Status.Should().Be(OrderStatus.Yeni);
        first.TotalAmount.Should().Be(1499.90m);
        first.Lines.Single().Should().Be(new NormalizedOrderLine("3fa85f64-5717-4562-b3fc-2c963f66afa6", "BRK-1", "SC-1", 1, 1499.90m, 224.99m));
        first.Customer.Should().Be(new CustomerSummary("Zeynep Kaya", "Ankara", "Çankaya"), "KVKK: telefon/mahalle alınmaz");
        first.Cargo.Should().Be(new CargoInfo("YRTICI", "TRK-1", null));
        orders.Single(o => o.ChannelOrderNo == "BY1002/S-2").Status.Should().Be(OrderStatus.Iade, "teslim edilemeyen gönderi");
        orders.Single(o => o.ChannelOrderNo == "BY1003/S-3").Status.Should().Be(OrderStatus.Iptal, "Divided: paket bölündü");
    }

    [Theory]
    [InlineData(1, false, OrderStatus.Yeni)]
    [InlineData(2, false, OrderStatus.Hazirlaniyor)]
    [InlineData(4, false, OrderStatus.Kargoda)]
    [InlineData(8, false, OrderStatus.TeslimEdildi)]
    [InlineData(16, false, OrderStatus.Iptal)]
    [InlineData(16, true, OrderStatus.Iade)]
    [InlineData(32, false, OrderStatus.Iptal)]
    public void Gonderi_durumu_eslenir(int status, bool returned, OrderStatus expected) =>
        BeymenAdapter.MapStatus(status, returned).Should().Be(expected);

    [Fact]
    public async Task Durum_takibi_icin_en_az_7_gun_geriye_bakilir()
    {
        var (adapter, http) = Create();

        await adapter.FetchOrdersAsync(DateTime.UtcNow.AddMinutes(-15), Ct);

        var start = Uri.UnescapeDataString(System.Web.HttpUtility.ParseQueryString(http.Requests[0].Uri.Query)["startDate"]!);
        DateTime.Parse(start, null, System.Globalization.DateTimeStyles.AdjustToUniversal).Should()
            .BeCloseTo(DateTime.UtcNow.AddDays(-BeymenAdapter.StatusLookbackDays), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Iadeler_normalize_edilir()
    {
        var (adapter, http) = Create();
        http.Respond = _ => (HttpStatusCode.OK, """
            {"items":[{"id":"c-1","orderNumber":"BY1001","shipmentNumber":"S-1","claimDate":"2026-11-16T10:00:00Z",
              "shipmentLines":[{"productId":"3fa85f64-5717-4562-b3fc-2c963f66afa6","quantity":1,"status":0,"product":{"barcode":"BRK-1"},
                                "customerReason":{"code":"1","description":"Beden uymadı"}}]}],"page":0,"pageCount":1,"total":1}
            """);

        var returns = await adapter.FetchReturnsAsync(DateTime.UtcNow.AddDays(-1), Ct);

        http.Requests.Single().Uri.AbsolutePath.Should().Be("/claims");
        returns.Should().ContainSingle().Which.Should().Match<NormalizedReturn>(r =>
            r.ChannelOrderNo == "BY1001/S-1" && r.ReturnNo == "c-1" && r.Reason == "Beden uymadı" && r.Lines.Single().Quantity == 1);
    }

    [Fact]
    public async Task Kanal_stogu_istenen_productIdler_icin_suzulur()
    {
        var (adapter, http) = Create();
        http.Respond = _ => (HttpStatusCode.OK, """
            {"items":[{"id":"3fa85f64-5717-4562-b3fc-2c963f66afa6","barcode":"BRK-1","stock":{"count":9}},
                      {"id":"ffffffff-5717-4562-b3fc-2c963f66afa6","barcode":"BRK-X","stock":{"count":1}}],"total":2,"size":1000,"page":0,"pageCount":1}
            """);

        var stock = await adapter.FetchStockAsync(["3fa85f64-5717-4562-b3fc-2c963f66afa6"], Ct);

        http.Requests.Single().Uri.AbsolutePath.Should().Be("/products/price-stock/search");
        stock.Should().ContainSingle().Which.Should().Be(new ChannelStock("3fa85f64-5717-4562-b3fc-2c963f66afa6", "BRK-1", 9));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"status":401,"title":"MicroservicesCommon.Exceptions.UnauthorizedException"}""", "API bilgileri hatalı", false)]
    [InlineData(HttpStatusCode.BadRequest, """{"message":"items cannot be empty"}""", "Beymen isteği reddetti: items cannot be empty", false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "", "geçici bir hata", true)]
    public void Ham_hata_sade_mesaja_cevrilir(HttpStatusCode status, string body, string expected, bool transient)
    {
        var ex = BeymenAdapter.Translate(status, body);
        ex.Message.Should().Contain(expected);
        ex.Transient.Should().Be(transient);
    }

    // ------------------------------------------------------------------ katalog (ADR-022)

    /// <summary>ADR-031: "Product Listing" (GET /products) örnek yanıtı (dokümandan; gerçek hesap kaydı değil).</summary>
    [Fact]
    public async Task Katalog_urun_listesinden_cekilir_model_renk_beden_fiyat_ve_varyant_anahtari()
    {
        var (adapter, http) = Create();
        http.Respond = _ => (HttpStatusCode.OK, """
            {"items":[
              {"id":"d4cc05d9-52fb-49b1-8074-e2e6af8e5440","externalId":"x","merchantId":"m","name":"Chain Havlu","barcode":"Chain75150001","modelCode":"Chain75150",
               "stockCode":"","brand":{"id":"717","name":"Softcotton"},"category":{"id":"98267","name":"Havlu"},"merchantColorCode":"",
               "size":{"id":"13483","name":"75x150"},"platformColor":{"id":"2481","name":"Krem"},
               "salesPrice":{"value":499.9,"currency":"TRY"},"platformSalesPrice":{"value":349.9,"currency":"TRY"},
               "stock":{"count":19},"status":{"status":"OnSale","localizedDescription":"Satışta"},
               "images":[{"id":4352,"thumbnailPath":"https://img-beymen-marketplace.mncdn.com/mnresize/57/93/a/e5.jpg","originalPath":"https://img-beymen-marketplace.mncdn.com/a/e5.jpg","isImageRevised":true}]}],
             "page":0,"itemsPerPage":100,"pageCount":1,"total":1}
            """);

        var page = await adapter.FetchCatalogAsync(null, Ct);

        http.Requests.Single().Uri.PathAndQuery.Should().Be("/products?size=100&page=0");
        page.NextCursor.Should().BeNull();
        page.Items.Should().Equal(new ChannelCatalogEntry("d4cc05d9-52fb-49b1-8074-e2e6af8e5440", "Chain75150001", null, "Chain75150", "Krem", "75x150",
            "Chain Havlu", "Softcotton", "Havlu", "Satışta", false, "pd4cc05d9-52fb-49b1-8074-e2e6af8e5440", "Renk: Krem; Beden: 75x150", null, 349.9m, 499.9m,
            ImageUrl: "https://img-beymen-marketplace.mncdn.com/mnresize/57/93/a/e5.jpg"));
    }

    [Fact]
    public async Task Katalog_sonraki_sayfa_imleci()
    {
        var (adapter, http) = Create();
        var items = string.Join(",", Enumerable.Range(0, 100).Select(i => $$"""{"id":"id-{{i}}","barcode":"B{{i}}"}"""));
        http.Respond = _ => (HttpStatusCode.OK, $$"""{"items":[{{items}}],"page":0,"pageCount":3}""");

        (await adapter.FetchCatalogAsync(null, Ct)).NextCursor.Should().Be("p:1");
        (await adapter.FetchCatalogAsync("p:2", Ct)).NextCursor.Should().BeNull("son sayfa (pageCount 3)");
        http.Requests[1].Uri.PathAndQuery.Should().Be("/products?size=100&page=2");
    }
}
