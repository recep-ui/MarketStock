using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PazaryeriHub.Adapters.Beymen;
using PazaryeriHub.Adapters.Hepsiburada;
using PazaryeriHub.Adapters.Trendyol;
using PazaryeriHub.Application.Channels;

namespace PazaryeriHub.Tests.Unit.Channels;

/// <summary>
/// Fiyat gönderimi contract testleri (ADR-028). İstek gövdeleri resmi dokümandaki örneklere göre doğrulanır; yanıtlar dokümandaki
/// örneklerdir, gerçek hesapla alınmış kayıt DEĞİLDİR. KDV kuralı: yalnızca Beymen dokümanı "VAT included" diyor.
/// </summary>
public sealed class PriceContractTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Body)> Requests { get; } = [];
        public Func<Uri, (HttpStatusCode, string)> Respond { get; set; } = _ => (HttpStatusCode.OK, "{}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            var (status, body) = Respond(request.RequestUri!);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeCredentials(string json) : IChannelCredentialStore
    {
        public Task<string?> GetJsonAsync(string channelCode, CancellationToken ct) => Task.FromResult<string?>(json);
        public Task SaveJsonAsync(string channelCode, string json, CancellationToken ct) => Task.CompletedTask;
        public Task ClearAsync(string channelCode, CancellationToken ct) => Task.CompletedTask;
    }

    private const string HbMerchant = "b2910839-83b9-4d45-adb6-86bad457edcb";

    private static (TrendyolAdapter, FakeHandler) Trendyol()
    {
        var h = new FakeHandler();
        return (new TrendyolAdapter(new HttpClient(h), new FakeCredentials("""{"sellerId":12345,"apiKey":"k","apiSecret":"s","environment":"Prod"}"""),
            TimeProvider.System, NullLogger<TrendyolAdapter>.Instance), h);
    }

    private static (BeymenAdapter, FakeHandler) Beymen()
    {
        var h = new FakeHandler();
        return (new BeymenAdapter(new HttpClient(h), new FakeCredentials("""{"apiKey":"k","apiPassword":"p","environment":"Stage"}"""),
            TimeProvider.System, NullLogger<BeymenAdapter>.Instance), h);
    }

    private static (HepsiburadaAdapter, FakeHandler) Hepsiburada()
    {
        var h = new FakeHandler();
        return (new HepsiburadaAdapter(new HttpClient(h), new FakeCredentials($$"""{"merchantId":"{{HbMerchant}}","userName":"u","password":"p","environment":"Test"}"""),
            TimeProvider.System, NullLogger<HepsiburadaAdapter>.Instance), h);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ KDV kuralları (yetenek açılabilir mi)

    [Fact]
    public void Uc_kanal_KDV_dahil_Trendyol_ve_Hepsiburada_kullanici_onayiyla()
    {
        var (ty, _) = Trendyol();
        var (by, _) = Beymen();
        var (hb, _) = Hepsiburada();

        // ADR-041: Trendyol KDV dahil — kullanıcı onayı (satıcı panelinde TSF = müşterinin gördüğü fiyat); resmi dokümanda ifade yok
        ty.DescribePriceUpdate().Should().Match<PriceUpdateRule>(r => r.Supported && r.VatIncluded == true && r.CanEnable && r.SendsListPrice);
        ty.DescribePriceUpdate().Note.Should().Contain("Kullanıcı onayı");
        // ADR-065: Hepsiburada KDV dahil — kullanıcı onayı (sitede müşterinin gördüğü fiyat = API'deki fiyat); resmi dokümanda ifade yok
        hb.DescribePriceUpdate().Should().Match<PriceUpdateRule>(r => r.Supported && r.VatIncluded == true && r.CanEnable && !r.SendsListPrice);
        hb.DescribePriceUpdate().Note.Should().Contain("Kullanıcı onayı");
        by.DescribePriceUpdate().Should().Match<PriceUpdateRule>(r => r.Supported && r.VatIncluded == true && r.CanEnable && r.SamePriceForProductVariants);
        new[] { ty.DescribePriceUpdate(), hb.DescribePriceUpdate(), by.DescribePriceUpdate() }.Should().OnlyContain(r => r.Source != null);
    }

    // ------------------------------------------------------------------ Trendyol

    [Fact]
    public async Task Trendyol_yalnizca_fiyat_gonderir_quantity_yok()
    {
        var (adapter, http) = Trendyol();
        http.Respond = _ => (HttpStatusCode.OK, """{"batchRequestId":"fa75dfd5-6ce6-4730-a09e-97563500000-1529854840"}""");

        var result = await adapter.PushPriceAsync([new PriceUpdate(null, "8680000000", 113.85m, 112.85m)], Ct);

        result.Should().Be(new BatchResult("fa75dfd5-6ce6-4730-a09e-97563500000-1529854840", true));
        var r = http.Requests.Single();
        r.Method.Should().Be(HttpMethod.Post);
        r.Uri.ToString().Should().Be("https://apigw.trendyol.com/integration/inventory/sellers/12345/products/price-and-inventory");
        using var body = JsonDocument.Parse(r.Body!);
        var item = body.RootElement.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("barcode").GetString().Should().Be("8680000000");
        item.GetProperty("salePrice").GetDecimal().Should().Be(112.85m);
        item.GetProperty("listPrice").GetDecimal().Should().Be(113.85m);
        item.TryGetProperty("quantity", out _).Should().BeFalse("yalnızca fiyat: stok alanı gönderilmez (stok ayrı hatta)");
    }

    [Fact]
    public async Task Trendyol_liste_satistan_dusukse_istek_cikmaz()
    {
        var (adapter, http) = Trendyol();
        await FluentActions.Invoking(() => adapter.PushPriceAsync([new PriceUpdate(null, "1", 90m, 100m)], Ct)).Should().ThrowAsync<ArgumentException>();
        http.Requests.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ Beymen

    [Fact]
    public async Task Beymen_liste_salesPrice_satis_platformPrice_olarak_gider_quantity_yok()
    {
        var (adapter, http) = Beymen();
        http.Respond = _ => (HttpStatusCode.OK, """{"processId":"a218a046-08dc-476d-a1d6-5d463ea2e418","message":"Your bulk price and stock update request has been received."}""");

        var result = await adapter.PushPriceAsync([new PriceUpdate("e9e5261a-5be1-430d-9ee4-34e0ee7ddd92", null, 1999.99m, 1899.99m)], Ct);

        result.BatchId.Should().Be("a218a046-08dc-476d-a1d6-5d463ea2e418");
        var r = http.Requests.Single();
        r.Uri.ToString().Should().Be("https://stage-partner-api.beymen.com/batch/products/price-stock");
        using var body = JsonDocument.Parse(r.Body!);
        var item = body.RootElement.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("productId").GetString().Should().Be("e9e5261a-5be1-430d-9ee4-34e0ee7ddd92");
        item.GetProperty("salesPrice").GetDecimal().Should().Be(1999.99m);
        item.GetProperty("platformPrice").GetDecimal().Should().Be(1899.99m);
        item.TryGetProperty("quantity", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Beymen_ayni_productId_iki_kez_gonderilmez()
    {
        var (adapter, http) = Beymen();
        await FluentActions.Invoking(() => adapter.PushPriceAsync(
            [new PriceUpdate("p-1", null, 10m, 10m), new PriceUpdate("p-1", null, 12m, 12m)], Ct)).Should().ThrowAsync<ArgumentException>();
        http.Requests.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ Hepsiburada

    [Fact]
    public async Task Hepsiburada_price_uploads_ve_ayri_sonuc_ucu()
    {
        var (adapter, http) = Hepsiburada();
        http.Respond = uri => uri.AbsolutePath.EndsWith("/price-uploads", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """{"id":"715bae7f-2b90-4e88-9067-uhu7656892123"}""")
            : (HttpStatusCode.OK, """
                {"id":"715bae7f-2b90-4e88-9067-uhu7656892123","status":"Done","createdAt":"2023-10-02T11:09:16.334Z","total":2,"errors":null,
                 "priceValidations":[{"elementNo":1,"hepsiburadaSku":"HBCV00002LJ9YU","merchantSku":"X","type":"MaxLock","minPrice":899.8,"maxPrice":13767.0,
                   "description":"Yüksek fiyat sebebiyle kilitlendi."}]}
                """);

        var result = await adapter.PushPriceAsync([new PriceUpdate("HBCV00002LJ9YU", null, 20000m, 15000m), new PriceUpdate("HBV0000OK", null, 100m, 100m)], Ct);

        result.BatchId.Should().Be("price:715bae7f-2b90-4e88-9067-uhu7656892123", "sonuç ucu stoktan ayrı → önekle işaretlenir");
        var post = http.Requests.Single();
        post.Uri.ToString().Should().Be($"https://listing-external-sit.hepsiburada.com/listings/merchantid/{HbMerchant}/price-uploads");
        using (var body = JsonDocument.Parse(post.Body!))
        {
            body.RootElement.EnumerateArray().Select(i => (i.GetProperty("hepsiburadaSku").GetString(), i.GetProperty("price").GetDecimal()))
                .Should().Equal(("HBCV00002LJ9YU", 15000m), ("HBV0000OK", 100m));
            post.Body.Should().NotContain("listPrice", "Hepsiburada yalnızca tek fiyat alır");
        }

        var status = await adapter.CheckBatchAsync(result.BatchId!, Ct);
        http.Requests[1].Uri.AbsolutePath.Should().Be($"/listings/merchantid/{HbMerchant}/price-uploads/id/715bae7f-2b90-4e88-9067-uhu7656892123");
        status.State.Should().Be(BatchState.PartiallyFailed, "fiyat kilidi (MaxLock) kalem hatasıdır");
        status.Errors.Should().ContainSingle(e => e.ChannelProductCode == "HBCV00002LJ9YU" && e.Message.Contains("MaxLock"));
    }

    [Fact]
    public async Task Hepsiburada_stok_batchi_eski_uctan_sorgulanir()
    {
        var (adapter, http) = Hepsiburada();
        http.Respond = _ => (HttpStatusCode.OK, """{"id":"s-1","status":"Done","total":1,"errors":null}""");
        (await adapter.CheckBatchAsync("s-1", Ct)).State.Should().Be(BatchState.Completed);
        http.Requests.Single().Uri.AbsolutePath.Should().EndWith("/stock-uploads/id/s-1");
    }

    [Fact]
    public async Task Hepsiburada_sifir_fiyat_gonderilmez_listingi_kapatir()
    {
        var (adapter, http) = Hepsiburada();
        await FluentActions.Invoking(() => adapter.PushPriceAsync([new PriceUpdate("HB1", null, 0m, 0m)], Ct)).Should().ThrowAsync<ArgumentException>();
        http.Requests.Should().BeEmpty();
    }
}
