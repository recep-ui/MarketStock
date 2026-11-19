using System.Net;
using System.Text;
using System.Text.Json;
using PazaryeriHub.Adapters.Hepsiburada;

namespace PazaryeriHub.Tests.Unit.Channels;

/// <summary>
/// ADR-037: Hepsiburada test ortamı (SIT) doğrulama istemcisi. Uçlar ve alan adları developers.hepsiburada.com'dan (2026-11-19,
/// uygulama içi tarayıcıyla) okundu: "Katalog Ürün Entegrasyonu / Listeleme / Sipariş Entegrasyonu Test Süreci Adımları" ve ilgili
/// uç sayfaları. Yanıt şemaları dokümanda gösterilmediği için istemci yanıtı yorumlamadan döndürür; testler İSTEĞİ doğrular.
/// </summary>
public sealed class HepsiburadaSitClientContractTests
{
    private const string MerchantId = "b2910839-83b9-4d45-adb6-86bad457edcb";

    private static readonly HepsiburadaCredentials TestCreds =
        new(MerchantId, "hb-kullanici", "hb-servis-anahtari", HepsiburadaCredentials.Test, "PazaryeriHub-SIT");

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Auth, string? UserAgent, string? Body)> Requests { get; } = [];
        public (HttpStatusCode Status, string Body) Response { get; set; } = (HttpStatusCode.OK, "{}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(),
                string.Join(' ', request.Headers.GetValues("User-Agent")),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(Response.Status) { Content = new StringContent(Response.Body, Encoding.UTF8, "application/json") };
        }
    }

    private static (HepsiburadaSitClient Client, FakeHandler Http) Create()
    {
        var handler = new FakeHandler();
        return (new HepsiburadaSitClient(new HttpClient(handler), TestCreds), handler);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Body((HttpMethod, Uri, string?, string?, string? Body) request) =>
        JsonDocument.Parse(request.Body!).RootElement;

    [Fact]
    public void Canli_ortam_bilgisiyle_olusturulamaz()
    {
        var prod = TestCreds with { Environment = HepsiburadaCredentials.Production };

        var act = () => new HepsiburadaSitClient(new HttpClient(new FakeHandler()), prod);

        act.Should().Throw<InvalidOperationException>().WithMessage("*yalnızca test (SIT)*");
    }

    [Fact]
    public async Task Her_istek_SIT_adresine_basic_auth_ve_user_agent_ile_gider()
    {
        var (client, http) = Create();

        await client.ListingsAsync(Ct);

        var r = http.Requests.Single();
        r.Method.Should().Be(HttpMethod.Get);
        r.Uri.ToString().Should().Be($"https://listing-external-sit.hepsiburada.com/listings/merchantid/{MerchantId}?offset=0&limit=100");
        r.Auth.Should().Be("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("hb-kullanici:hb-servis-anahtari")));
        r.UserAgent.Should().Be("PazaryeriHub-SIT");
    }

    [Fact]
    public async Task Hizli_urun_yukleme_dizi_govdesi_ve_durum_sorgulama()
    {
        var (client, http) = Create();

        await client.FastListingAsync([new SitFastListingItem("PH-SIT-1", "Test ürünü", "7541828790114", Stock: 5, Price: 100m)], Ct);
        await client.ProductStatusAsync("trk-123", Ct);

        http.Requests[0].Method.Should().Be(HttpMethod.Post);
        http.Requests[0].Uri.ToString().Should().Be("https://mpop-sit.hepsiburada.com/product/api/products/fastlisting");
        var item = Body(http.Requests[0]).EnumerateArray().Single();
        item.GetProperty("merchant").GetString().Should().Be(MerchantId);
        item.GetProperty("merchantSku").GetString().Should().Be("PH-SIT-1");
        item.GetProperty("productName").GetString().Should().Be("Test ürünü");
        item.GetProperty("barcode").GetString().Should().Be("7541828790114");
        item.GetProperty("stock").GetString().Should().Be("5", "dokümanda stock ve price string");
        item.GetProperty("price").GetString().Should().Be("100");

        http.Requests[1].Method.Should().Be(HttpMethod.Get);
        http.Requests[1].Uri.ToString().Should().Be("https://mpop-sit.hepsiburada.com/product/api/products/status/trk-123?version=1&page=0&size=1000");
    }

    [Fact]
    public async Task Envanter_yukleme_ve_sorgulama()
    {
        var (client, http) = Create();

        await client.InventoryUploadAsync([new SitInventoryItem("HBV00000AAA1", "MSKU-1", Price: 149.9m, AvailableStock: 7)], Ct);
        await client.InventoryUploadStatusAsync("inv-9", Ct);

        http.Requests[0].Uri.ToString().Should().Be($"https://listing-external-sit.hepsiburada.com/listings/merchantid/{MerchantId}/inventory-uploads");
        var item = Body(http.Requests[0]).EnumerateArray().Single();
        item.GetProperty("hepsiburadaSku").GetString().Should().Be("HBV00000AAA1");
        item.GetProperty("merchantSku").GetString().Should().Be("MSKU-1");
        item.GetProperty("price").GetDecimal().Should().Be(149.9m);
        item.GetProperty("availableStock").GetInt32().Should().Be(7);
        http.Requests[1].Uri.ToString().Should().Be($"https://listing-external-sit.hepsiburada.com/listings/merchantid/{MerchantId}/inventory-uploads/id/inv-9");
    }

    [Fact]
    public async Task Aktif_pasif_ve_toplu_kilit_kaldirma()
    {
        var (client, http) = Create();

        await client.ActivateAsync("HBV00000AAA1", Ct);
        await client.DeactivateAsync("HBV00000AAA1", Ct);
        await client.BulkUnlockAsync(["HBV00000AAA1", "HBV00000AAA2"], Ct);

        http.Requests.Select(r => r.Method).Should().AllBeEquivalentTo(HttpMethod.Post);
        http.Requests[0].Uri.ToString().Should().Be($"https://listing-external-sit.hepsiburada.com/listings/merchantid/{MerchantId}/sku/HBV00000AAA1/activate");
        http.Requests[0].Body.Should().Be("{}");
        http.Requests[1].Uri.ToString().Should().Be($"https://listing-external-sit.hepsiburada.com/listings/merchantid/{MerchantId}/sku/HBV00000AAA1/deactivate");
        http.Requests[2].Uri.ToString().Should().Be($"https://listing-external-sit.hepsiburada.com/listings/merchantid/{MerchantId}/bulk-unlock");
        Body(http.Requests[2]).GetProperty("hbSkuList").EnumerateArray().Select(e => e.GetString()).Should().Equal("HBV00000AAA1", "HBV00000AAA2");
    }

    [Fact]
    public async Task Test_siparisi_stub_adresine_gider_ve_musteri_bilgisi_acikca_test_degeridir()
    {
        var (client, http) = Create();

        await client.CreateTestOrderAsync(new SitTestOrderLine("HBV00000AAA1", "MSKU-1", Quantity: 1, UnitPrice: 149.9m), Ct);

        var r = http.Requests.Single();
        r.Method.Should().Be(HttpMethod.Post);
        r.Uri.ToString().Should().Be($"https://oms-stub-external-sit.hepsiburada.com/orders/merchantId/{MerchantId}");
        var body = Body(r);
        var line = body.GetProperty("LineItems").EnumerateArray().Single();
        line.GetProperty("MerchantId").GetString().Should().Be(MerchantId, "path'teki merchantId ile aynı olmalı");
        line.GetProperty("Sku").GetString().Should().Be("HBV00000AAA1");
        line.GetProperty("MerchantSku").GetString().Should().Be("MSKU-1");
        line.GetProperty("Quantity").GetInt32().Should().Be(1);
        line.GetProperty("Price").GetProperty("Amount").GetDecimal().Should().Be(149.9m);
        line.GetProperty("Price").GetProperty("Currency").GetString().Should().Be("TRY");
        line.GetProperty("TotalPrice").GetProperty("Amount").GetDecimal().Should().Be(149.9m);
        body.GetProperty("Customer").GetProperty("Name").GetString().Should().Contain("Test");
        r.Body.Should().NotContain("@").And.NotContain("PhoneNumber", "gerçek kişi verisi gönderilmez");
    }

    [Fact]
    public async Task Test_siparisinde_siparis_no_ve_teslimat_secenegi_yalnizca_verilirse_gonderilir()
    {
        var (client, http) = Create();

        await client.CreateTestOrderAsync(new SitTestOrderLine("HBV1", null, 1, 10m), Ct);
        await client.CreateTestOrderAsync(new SitTestOrderLine("HBV1", null, 1, 10m, DeliveryOptionId: 1), Ct, orderNumber: "9123456789");

        var plain = Body(http.Requests[0]);
        plain.TryGetProperty("OrderNumber", out _).Should().BeFalse();
        plain.GetProperty("LineItems")[0].TryGetProperty("DeliveryOptionId", out _).Should().BeFalse();
        var withValues = Body(http.Requests[1]);
        withValues.GetProperty("OrderNumber").GetString().Should().Be("9123456789");
        withValues.GetProperty("LineItems")[0].GetProperty("DeliveryOptionId").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Siparis_listeleme_ayni_pakete_konulabilecek_kalemler_ve_paketler()
    {
        var (client, http) = Create();

        await client.PaidOrdersAsync(Ct);
        await client.PackageableWithAsync("line-1", Ct);
        await client.PackagesAsync(Ct);

        http.Requests.Select(r => r.Method).Should().AllBeEquivalentTo(HttpMethod.Get);
        http.Requests[0].Uri.ToString().Should().Be($"https://oms-external-sit.hepsiburada.com/orders/merchantid/{MerchantId}?offset=0&limit=100");
        http.Requests[1].Uri.ToString().Should().Be($"https://oms-external-sit.hepsiburada.com/lineitems/merchantid/{MerchantId}/packageablewith/lineitemid/line-1");
        http.Requests[2].Uri.ToString().Should().Be($"https://oms-external-sit.hepsiburada.com/packages/merchantid/{MerchantId}?offset=0&limit=50");
    }

    [Fact]
    public async Task Hata_yaniti_istisna_atmaz_durum_ve_ham_govde_rapora_doner()
    {
        var (client, http) = Create();
        http.Response = (HttpStatusCode.BadRequest, """{"message":"Geçersiz istek"}""");

        var call = await client.ProductStatusAsync("x", Ct);

        call.Status.Should().Be(400);
        call.Success.Should().BeFalse();
        call.ResponseBody.Should().Contain("Geçersiz istek");
        call.Url.Should().NotContain("hb-servis-anahtari");
    }

    [Theory]
    [InlineData("""{"trackingId":"abc"}""", "trackingId", "abc")]
    [InlineData("""{"data":{"TrackingId":"def"}}""", "trackingId", "def")]
    [InlineData("""[{"id":42}]""", "id", "42")]
    [InlineData("""{"success":true}""", "trackingId", null)]
    [InlineData("bozuk", "trackingId", null)]
    public void Yanittan_alan_buyuk_kucuk_harf_duyarsiz_ve_ic_ice_bulunur(string body, string name, string? expected) =>
        SitCall.FindValue(body, name).Should().Be(expected);
}
