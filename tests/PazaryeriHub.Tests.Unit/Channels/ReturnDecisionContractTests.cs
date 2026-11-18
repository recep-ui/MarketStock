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
/// M3 (ADR-027): iade talebi okuma ve karar bildirimi contract testleri.
/// ÖRNEK YANITLAR / İSTEKLER GERÇEK YANIT DEĞİLDİR; resmi dokümandan alınmıştır (2026-11-18): Trendyol getClaims,
/// approveClaimLineItems, createClaimIssue; Beymen Return Listing, Confirmation of the Return, "I Have Received", Problem Reporting.
/// </summary>
public sealed class ReturnDecisionContractTests
{
    private const string TrendyolCreds = """{"sellerId":12345,"apiKey":"test-key","apiSecret":"test-secret","environment":"Prod"}""";
    private const string BeymenCreds = """{"apiKey":"by-key","apiPassword":"by-password","environment":"Stage"}""";
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

    private static readonly IReadOnlyList<ReturnDecisionLine> TyLines = [new("28717254", "99999999999", ["b71461e3-a", "b71461e3-b"], 2)];

    // ------------------------------------------------------------------ Trendyol

    [Fact]
    public void Trendyol_iade_talebi_claimItem_kimlikleri_ve_durumuyla_esleniri()
    {
        var json = """
            { "id": "f9da2317", "claimId": "f9da2317", "orderNumber": "65745805", "orderShipmentPackageId": 3853354, "claimDate": 1525844162827,
              "items": [ { "orderLine": { "id": 28717254, "barcode": "99999999999", "merchantSku": "2083667" },
                           "claimItems": [ { "id": "b71461e3-a", "customerClaimItemReason": { "name": "Beden uymama" }, "claimItemStatus": { "name": "WaitingInAction" } },
                                           { "id": "b71461e3-b", "claimItemStatus": { "name": "WaitingInAction" } } ] } ] }
            """;
        var claim = JsonSerializer.Deserialize<Claim>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var r = TrendyolAdapter.MapClaim(claim)!;

        r.ReturnNo.Should().Be("f9da2317");
        r.ChannelStatus.Should().Be("WaitingInAction");
        r.Reason.Should().Be("Beden uymama");
        var line = r.Lines.Single();
        line.Quantity.Should().Be(2, "her claimItem bir adet");
        line.LineKey.Should().Be("28717254");
        line.ChannelItemIds.Should().Equal("b71461e3-a", "b71461e3-b");
    }

    [Fact]
    public async Task Trendyol_onay_claimItem_listesiyle_red_yalnizca_eksiz_gerekceyle_gider()
    {
        var h = new FakeHandler();
        var adapter = new TrendyolAdapter(new HttpClient(h), new FakeCredentials(TrendyolCreds), TimeProvider.System, NullLogger<TrendyolAdapter>.Instance);

        await adapter.DecideReturnAsync(new ReturnDecisionRequest("f9da2317", ReturnDecisionKind.Approve, TyLines, null, null), Ct);
        h.Requests[0].Method.Should().Be(HttpMethod.Put);
        h.Requests[0].Uri.Should().Be(new Uri("https://apigw.trendyol.com/integration/order/sellers/12345/claims/f9da2317/items/approve"));
        JsonDocument.Parse(h.Requests[0].Body!).RootElement.GetRawText().Should().Be("""{"claimLineItemIdList":["b71461e3-a","b71461e3-b"],"params":{}}""");

        var withFile = () => adapter.DecideReturnAsync(new ReturnDecisionRequest("f9da2317", ReturnDecisionKind.Reject, TyLines, "51", "x"), Ct);
        await withFile.Should().ThrowAsync<ArgumentException>("51 dosya eki ister");

        await adapter.DecideReturnAsync(new ReturnDecisionRequest("f9da2317", ReturnDecisionKind.Reject, TyLines, "1651", "Paket ulaşmadı"), Ct);
        h.Requests.Should().HaveCount(2);
        h.Requests[1].Method.Should().Be(HttpMethod.Post);
        h.Requests[1].Uri.AbsolutePath.Should().Be("/integration/order/sellers/12345/claims/f9da2317/issue");
        var q = System.Web.HttpUtility.ParseQueryString(h.Requests[1].Uri.Query);
        q["claimIssueReasonId"].Should().Be("1651");
        q["claimItemIdList"].Should().Be("b71461e3-a,b71461e3-b");
        q["description"].Should().Be("Paket ulaşmadı");

        adapter.DescribeReturnDecision(ReturnDecisionKind.Reject).AllowedReasons.Should().Equal("1651", "451", "2101");
        adapter.DescribeReturnDecision(ReturnDecisionKind.Received).Supported.Should().BeFalse();
    }

    // ------------------------------------------------------------------ Beymen

    [Fact]
    public async Task Beymen_onay_teslim_alindi_ve_bos_paket_sorun_bildirimi_dokumandaki_uclara_gider()
    {
        var h = new FakeHandler();
        var adapter = new BeymenAdapter(new HttpClient(h), new FakeCredentials(BeymenCreds), TimeProvider.System, NullLogger<BeymenAdapter>.Instance);
        const string id = "0ef04ec6-c8fb-4572-8d08-9a184c234dfc";
        IReadOnlyList<ReturnDecisionLine> lines = [new("8cf7864b", "8cf7864b", ["8cf7864b"], 1)];

        await adapter.DecideReturnAsync(new ReturnDecisionRequest(id, ReturnDecisionKind.Approve, lines, null, null), Ct);
        await adapter.DecideReturnAsync(new ReturnDecisionRequest(id, ReturnDecisionKind.Received, lines, null, null), Ct);
        var images = () => adapter.DecideReturnAsync(new ReturnDecisionRequest(id, ReturnDecisionKind.Reject, lines, "ProductIsFake", null), Ct);
        await images.Should().ThrowAsync<ArgumentException>("görsel gerektiren tip desteklenmez");
        await adapter.DecideReturnAsync(new ReturnDecisionRequest(id, ReturnDecisionKind.Reject, lines, "PackageIsEmpty", "Boş geldi"), Ct);

        h.Requests.Select(r => (r.Method.Method, r.Uri.AbsolutePath)).Should().Equal(
            ("PATCH", $"/claims/{id}/approve"), ("PATCH", $"/claims/{id}/receive"), ("PATCH", $"/claims/{id}/dispute"));
        JsonDocument.Parse(h.Requests[0].Body!).RootElement.GetRawText().Should().Be("""[{"productId":"8cf7864b","quantity":1}]""");
        h.Requests[1].Body.Should().BeNull();
        var dispute = JsonDocument.Parse(h.Requests[2].Body!).RootElement.GetProperty("items")[0];
        dispute.GetProperty("reasonType").GetInt32().Should().Be(9, "PackageIsEmpty = 9");
        dispute.GetProperty("images").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Beymen_iade_talebi_productId_satir_anahtariyla_esleniri()
    {
        using var doc = JsonDocument.Parse("""
            { "id": "0ef04ec6", "orderNumber": "BEY-1", "shipmentNumber": "MP1", "claimDate": "2022-09-15T14:16:34.887",
              "shipmentLines": [ { "quantity": 1, "productId": "8cf7864b", "status": 1, "product": { "barcode": "436324254" },
                                   "customerReason": { "code": "1", "description": "Cayma Hakkımı Kullanmak İstiyorum" } } ] }
            """);

        var r = BeymenAdapter.MapClaim(doc.RootElement)!;

        r.ReturnNo.Should().Be("0ef04ec6");
        r.ChannelOrderNo.Should().Be("BEY-1/MP1");
        r.ChannelStatus.Should().Be("ActionNeeded");
        r.Lines.Single().Should().Match<NormalizedReturnLine>(l => l.LineKey == "8cf7864b" && l.Barcode == "436324254" && l.ChannelItemIds!.Single() == "8cf7864b");
    }

    // ------------------------------------------------------------------ Hepsiburada

    [Fact]
    public async Task Hepsiburada_iade_okuma_ve_karar_dogrulanmadigi_icin_istek_atmaz()
    {
        var h = new FakeHandler();
        var adapter = new HepsiburadaAdapter(new HttpClient(h), new FakeCredentials(null), TimeProvider.System, NullLogger<HepsiburadaAdapter>.Instance);

        var fetch = () => adapter.FetchReturnsAsync(DateTime.UtcNow.AddDays(-1), Ct);
        var decide = () => adapter.DecideReturnAsync(new ReturnDecisionRequest("1", ReturnDecisionKind.Approve, [], null, null), Ct);

        await fetch.Should().ThrowAsync<NotSupportedException>();
        await decide.Should().ThrowAsync<NotSupportedException>();
        adapter.DescribeReturnDecision(ReturnDecisionKind.Approve).Supported.Should().BeFalse();
        h.Requests.Should().BeEmpty();
    }
}
