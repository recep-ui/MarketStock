using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PazaryeriHub.Adapters.Trendyol;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Domain.Orders;

namespace PazaryeriHub.Tests.Unit.Channels;

/// <summary>
/// Aşama 6 kabul (sandbox yok): Trendyol adaptörü contract testleri. Örnek yanıtlar resmi dokümandaki şemaya ve örneklere
/// göre hazırlanmıştır (developers.trendyol.com/v2.0/reference/...); gerçek hesapla alınmış kayıt DEĞİLDİR.
/// Test ortamı (stage) erişimi ve gerçek kayıtlı yanıtlar geldiğinde bu örnekler gerçek yanıtlarla değiştirilmelidir.
/// </summary>
public sealed class TrendyolAdapterContractTests
{
    private const string Creds = """{"sellerId":12345,"apiKey":"test-key","apiSecret":"test-secret","environment":"Prod"}""";

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Auth, string? UserAgent, string? Body)> Requests { get; } = [];
        public Queue<(HttpStatusCode Status, string Body)> Responses { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(),
                string.Join(' ', request.Headers.GetValues("User-Agent")),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            var (status, body) = Responses.Count > 0 ? Responses.Dequeue() : (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeCredentials(string? json) : IChannelCredentialStore
    {
        public Task<string?> GetJsonAsync(string channelCode, CancellationToken ct) => Task.FromResult(json);
        public Task SaveJsonAsync(string channelCode, string json, CancellationToken ct) => Task.CompletedTask;
        public Task ClearAsync(string channelCode, CancellationToken ct) => Task.CompletedTask;
    }

    private static (TrendyolAdapter Adapter, FakeHandler Http) Create(string? creds = Creds)
    {
        var handler = new FakeHandler();
        return (new TrendyolAdapter(new HttpClient(handler), new FakeCredentials(creds), TimeProvider.System, NullLogger<TrendyolAdapter>.Instance), handler);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ kimlik

    [Fact]
    public async Task Basic_auth_ve_zorunlu_user_agent_gonderilir()
    {
        var (adapter, http) = Create();
        http.Responses.Enqueue((HttpStatusCode.OK, """{"batchRequestId":"fa75dfd5-6ce6-4730-a09e-97563500000-1529854840"}"""));

        await adapter.PushStockAsync([new StockUpdate(null, "8680000000", 3)], Ct);

        var request = http.Requests.Single();
        request.Auth.Should().Be("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("test-key:test-secret")));
        request.UserAgent.Should().Be("12345 - SelfIntegration", "dokümana göre '{sellerId} - SelfIntegration'; yoksa 403");
    }

    [Fact]
    public void Kimlik_bilgisi_dogrulanir_ve_gizli_degerler_ToStringde_gorunmez()
    {
        var creds = TrendyolCredentials.Parse(Creds);
        creds.ToString().Should().NotContain("test-key").And.NotContain("test-secret");
        creds.BaseUrl.Should().Be("https://apigw.trendyol.com/integration");
        TrendyolCredentials.Parse(Creds.Replace("Prod", "Stage")).BaseUrl.Should().Be("https://stageapigw.trendyol.com/integration");

        var act = () => TrendyolCredentials.Parse("""{"sellerId":0,"apiKey":"","apiSecret":"x","environment":"Canli"}""");
        act.Should().Throw<RequestValidationException>().Which.Errors.Keys.Should().BeEquivalentTo(["sellerId", "apiKey", "environment"]);
    }

    [Fact]
    public async Task Kimlik_bilgisi_yoksa_sade_hata()
    {
        var (adapter, _) = Create(creds: null);

        var act = () => adapter.PushStockAsync([new StockUpdate(null, "1", 1)], Ct);

        (await act.Should().ThrowAsync<ChannelApiException>()).Which.Message.Should().Contain("girilmemiş");
    }

    // ------------------------------------------------------------------ stok

    [Fact]
    public async Task Stok_gonderimi_yalnizca_barkod_ve_adet_tasir()
    {
        var (adapter, http) = Create();
        http.Responses.Enqueue((HttpStatusCode.OK, """{"batchRequestId":"b-1"}"""));

        var result = await adapter.PushStockAsync(
        [
            new StockUpdate("KOD-1", "8680000000", 100),   // barkod öncelikli
            new StockUpdate("8680000001", null, 25_000),   // ürün başına üst sınır 20.000
            new StockUpdate("8680000002", null, -4)        // negatif → 0
        ], Ct);

        result.Should().Be(new BatchResult("b-1", true));
        var request = http.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.ToString().Should().Be("https://apigw.trendyol.com/integration/inventory/sellers/12345/products/price-and-inventory");
        using var body = JsonDocument.Parse(request.Body!);
        body.RootElement.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("barcode").GetString(), i.GetProperty("quantity").GetInt32()))
            .Should().Equal(("8680000000", 100), ("8680000001", 20_000), ("8680000002", 0));
        request.Body.Should().NotContain("salePrice").And.NotContain("listPrice", "fiyat gönderilmez (ADR-015)");
    }

    [Fact]
    public async Task Binden_fazla_kalem_reddedilir()
    {
        var (adapter, _) = Create();
        var updates = Enumerable.Range(0, 1001).Select(i => new StockUpdate(null, i.ToString(), 1)).ToList();

        await FluentActions.Invoking(() => adapter.PushStockAsync(updates, Ct)).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Batch_sonucu_durumlara_cevrilir()
    {
        var (adapter, http) = Create();
        http.Responses.Enqueue((HttpStatusCode.OK, """{"batchRequestId":"b-1","status":"IN_PROGRESS","items":[],"itemCount":2,"failedItemCount":0}"""));
        http.Responses.Enqueue((HttpStatusCode.OK, """
            {"batchRequestId":"b-1","status":"COMPLETED","itemCount":2,"failedItemCount":1,"sourceType":"API","batchRequestType":"ProductInventoryUpdate",
             "items":[{"requestItem":{"barcode":"8680000000","quantity":5},"status":"SUCCESS","failureReasons":[]},
                      {"requestItem":{"barcode":"8680000001","quantity":7},"status":"FAILED","failureReasons":["Ürün bulunamadı"]}]}
            """));
        http.Responses.Enqueue((HttpStatusCode.OK, """{"batchRequestId":"b-1","status":"COMPLETED","itemCount":1,"failedItemCount":0,"items":[{"requestItem":{"barcode":"8680000000"},"status":"SUCCESS","failureReasons":[]}]}"""));

        (await adapter.CheckBatchAsync("b-1", Ct)).State.Should().Be(BatchState.Pending);
        var partial = await adapter.CheckBatchAsync("b-1", Ct);
        (await adapter.CheckBatchAsync("b-1", Ct)).State.Should().Be(BatchState.Completed);

        http.Requests[0].Uri.ToString().Should().Be("https://apigw.trendyol.com/integration/product/sellers/12345/products/batch-requests/b-1");
        partial.State.Should().Be(BatchState.PartiallyFailed);
        partial.Errors.Should().ContainSingle().Which.Should().Be(new BatchItemError("8680000001", "8680000001", "Trendyol: Ürün bulunamadı"));
    }

    // ------------------------------------------------------------------ siparişler

    private const string PackagesPage1 = """
        {"page":0,"size":200,"totalPages":2,"totalElements":3,"content":[
          {"id":11650604,"orderNumber":"80869231","status":"Created","totalPrice":259.98,
           "customerFirstName":"Ali","customerLastName":"Veli","customerEmail":"x@example.com",
           "shipmentAddress":{"firstName":"Ali","lastName":"Veli","city":"İstanbul","district":"Kadıköy","fullAddress":"gizli adres"},
           "cargoProviderName":"Trendyol Express","cargoTrackingNumber":7330029148127,"cargoTrackingLink":"https://example/track",
           "packageHistory":[{"createdDate":1700000000000,"status":"Created"}],
           "lines":[{"id":1,"barcode":"8680000000","merchantSku":"SC-1","quantity":2,"price":129.99,"amount":259.98}]},
          {"id":11650605,"orderNumber":"80869232","status":"UnPacked","totalPrice":10,"packageHistory":[],"lines":[{"id":2,"barcode":"8680000001","quantity":1,"price":10}]}
        ]}
        """;

    private const string PackagesPage2 = """
        {"page":1,"size":200,"totalPages":2,"totalElements":3,"content":[
          {"id":11650606,"orderNumber":"80869233","status":"BilinmeyenDurum","lines":[]}
        ]}
        """;

    [Fact]
    public async Task Siparisler_sayfalanir_ve_normalize_edilir()
    {
        var (adapter, http) = Create();
        http.Responses.Enqueue((HttpStatusCode.OK, PackagesPage1));
        http.Responses.Enqueue((HttpStatusCode.OK, PackagesPage2));
        var since = new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);

        var orders = await adapter.FetchOrdersAsync(since, Ct);

        http.Requests.Should().HaveCount(2);
        var query = http.Requests[0].Uri.Query;
        query.Should().Contain($"startDate={new DateTimeOffset(since).ToUnixTimeMilliseconds()}").And.Contain("page=0").And.Contain("size=200")
             .And.Contain("orderByField=PackageLastModifiedDate");
        http.Requests[1].Uri.Query.Should().Contain("page=1");
        http.Requests[0].Uri.AbsolutePath.Should().Be("/integration/order/sellers/12345/orders");

        orders.Should().HaveCount(2, "bilinmeyen durumdaki paket atlanır");
        var first = orders[0];
        first.ChannelOrderNo.Should().Be("80869231/11650604", "kanal sipariş no = sipariş no / paket id");
        first.Status.Should().Be(OrderStatus.Yeni);
        first.TotalAmount.Should().Be(259.98m);
        first.OrderDateUtc.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime);
        first.Lines.Should().ContainSingle().Which.Should().Be(new NormalizedOrderLine("8680000000", "8680000000", "SC-1", 2, 129.99m));
        first.Customer.Should().Be(new CustomerSummary("Ali Veli", "İstanbul", "Kadıköy"), "KVKK: yalnızca ad, il, ilçe; e-posta/adres alınmaz");
        first.Cargo.Should().Be(new CargoInfo("Trendyol Express", "7330029148127", "https://example/track"));
        orders[1].Status.Should().Be(OrderStatus.Iptal, "UnPacked paket bölündü; eski paket iptal sayılır");
    }

    [Theory]
    [InlineData("Created", OrderStatus.Yeni)]
    [InlineData("Awaiting", OrderStatus.Yeni)]
    [InlineData("Picking", OrderStatus.Hazirlaniyor)]
    [InlineData("Invoiced", OrderStatus.Hazirlaniyor)]
    [InlineData("Shipped", OrderStatus.Kargoda)]
    [InlineData("AtCollectionPoint", OrderStatus.Kargoda)]
    [InlineData("Delivered", OrderStatus.TeslimEdildi)]
    [InlineData("Cancelled", OrderStatus.Iptal)]
    [InlineData("UnSupplied", OrderStatus.Iptal)]
    [InlineData("Returned", OrderStatus.Iade)]
    [InlineData("UnDelivered", OrderStatus.Iade)]
    public void Paket_durumu_eslenir(string trendyol, OrderStatus expected) =>
        TrendyolAdapter.MapStatus(trendyol).Should().Be(expected);

    // ------------------------------------------------------------------ iadeler ve kanal stoğu

    [Fact]
    public async Task Iadeler_normalize_edilir()
    {
        var (adapter, http) = Create();
        http.Responses.Enqueue((HttpStatusCode.OK, """
            {"totalElements":1,"totalPages":1,"page":0,"size":200,"content":[
              {"id":"f9da2317-876b-4b86-b8f7-0535c3b65731","claimId":"f9da2317-876b-4b86-b8f7-0535c3b65731","orderNumber":"80869231",
               "orderShipmentPackageId":11650604,"claimDate":1700100000000,
               "items":[{"orderLine":{"id":1,"barcode":"8680000000","merchantSku":"SC-1"},
                         "claimItems":[{"id":"a","customerClaimItemReason":{"name":"Beden olmadı"},"claimItemStatus":{"name":"Created"}},
                                       {"id":"b","customerClaimItemReason":{"name":"Beden olmadı"},"claimItemStatus":{"name":"Created"}}]}]}
            ]}
            """));

        var returns = await adapter.FetchReturnsAsync(DateTime.UtcNow.AddDays(-1), Ct);

        http.Requests.Single().Uri.AbsolutePath.Should().Be("/integration/order/sellers/12345/claims");
        var r = returns.Should().ContainSingle().Subject;
        r.ChannelOrderNo.Should().Be("80869231/11650604", "siparişle aynı anahtar");
        r.Lines.Should().ContainSingle().Which.Should().Be(new NormalizedReturnLine("8680000000", "8680000000", 2));
        r.Reason.Should().Be("Beden olmadı");
    }

    [Fact]
    public async Task Kanal_stogu_50lik_barkod_parcalariyla_okunur()
    {
        var (adapter, http) = Create();
        const string page = """{"totalElements":1,"totalPages":1,"page":0,"size":100,"content":[{"contentId":1,"variants":[{"variantId":9,"barcode":"8680000000","quantity":12,"salePrice":1,"listPrice":1}]}]}""";
        http.Responses.Enqueue((HttpStatusCode.OK, page));
        http.Responses.Enqueue((HttpStatusCode.OK, """{"content":[]}"""));

        var stock = await adapter.FetchStockAsync(Enumerable.Range(0, 60).Select(i => $"B{i}").ToList(), Ct);

        http.Requests.Should().HaveCount(2, "en fazla 50 barkod / istek");
        http.Requests[0].Uri.AbsolutePath.Should().Be("/integration/product/sellers/12345/products/approved/inventory-and-price");
        stock.Should().ContainSingle().Which.Should().Be(new ChannelStock("8680000000", "8680000000", 12));
    }

    // ------------------------------------------------------------------ hata çevirisi

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"exception":"ClientApiAuthenticationException"}""", "API bilgileri hatalı", false)]
    [InlineData(HttpStatusCode.TooManyRequests, """{"message":"too.many.requests"}""", "istek sınırı", true)]
    [InlineData(HttpStatusCode.InternalServerError, "", "geçici bir hata", true)]
    [InlineData(HttpStatusCode.BadRequest, """{"errors":[{"message":"15 dakika boyunca aynı isteği tekrarlı olarak atamazsınız!"}]}""", "15 dakika", false)]
    [InlineData(HttpStatusCode.BadRequest, """{"errors":[{"message":"Barkod geçersiz"}]}""", "Trendyol isteği reddetti: Barkod geçersiz", false)]
    public void Ham_hata_sade_mesaja_cevrilir(HttpStatusCode status, string body, string expected, bool transient)
    {
        var ex = TrendyolAdapter.Translate(status, body, TrendyolCredentials.Production);

        ex.Message.Should().Contain(expected);
        ex.Transient.Should().Be(transient);
        ex.StatusCode.Should().Be((int)status);
    }

    [Fact]
    public void Stage_503_ip_beyaz_listesi_mesaji() =>
        TrendyolAdapter.Translate(HttpStatusCode.ServiceUnavailable, "", TrendyolCredentials.Stage).Message.Should().Contain("beyaz liste");

    [Fact]
    public async Task Kapsam_disi_islemler_desteklenmiyor()
    {
        var (adapter, http) = Create();

        await FluentActions.Invoking(() => adapter.PushPriceAsync([], Ct)).Should().ThrowAsync<NotSupportedException>();
        http.Requests.Should().BeEmpty();
    }
}
