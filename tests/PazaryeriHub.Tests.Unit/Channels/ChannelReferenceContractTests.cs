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
/// Satıcı Bilgileri (ADR-025) contract testleri.
/// ÖRNEK YANITLAR GERÇEK YANIT DEĞİLDİR: resmi dokümandaki örneklerden alınmıştır
/// (developers.trendyol.com/v2.0/docs/cargo-providers-service ve /claim-issue-reasons-getclaimsissuereasons, 2026-11-18 okundu).
/// Beymen listeleri dokümandaki sabit tablolardır (partner-docs.beymen.com/en, 2026-11-18).
/// </summary>
public sealed class ChannelReferenceContractTests
{
    private const string TrendyolCreds = """{"sellerId":12345,"apiKey":"test-key","apiSecret":"test-secret","environment":"Prod"}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri)> Requests { get; } = [];
        public Queue<(HttpStatusCode Status, string Body)> Responses { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!));
            var (status, body) = Responses.Count > 0 ? Responses.Dequeue() : (HttpStatusCode.OK, "[]");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class FakeCredentials(string? json) : IChannelCredentialStore
    {
        public Task<string?> GetJsonAsync(string channelCode, CancellationToken ct) => Task.FromResult(json);
        public Task SaveJsonAsync(string channelCode, string json, CancellationToken ct) => Task.CompletedTask;
        public Task ClearAsync(string channelCode, CancellationToken ct) => Task.CompletedTask;
    }

    // ------------------------------------------------------------------ Trendyol

    [Fact]
    public async Task Trendyol_kargo_firmalari_lookup_servisinden_okunur()
    {
        var handler = new FakeHandler();
        handler.Responses.Enqueue((HttpStatusCode.OK, """
            [ { "code": "KOLAYGELSINMP", "name": "Kolay Gelsin" }, { "code": "YKMP", "name": "Yurtiçi Kargo" }, { "code": "", "name": "boş kod" } ]
            """));
        var adapter = new TrendyolAdapter(new HttpClient(handler), new FakeCredentials(TrendyolCreds), TimeProvider.System, NullLogger<TrendyolAdapter>.Instance);

        var list = await adapter.FetchReferenceAsync(ChannelReferenceType.CargoProvider, Ct);

        handler.Requests.Single().Should().Be((HttpMethod.Get, new Uri("https://apigw.trendyol.com/integration/product/lookup/cargo-providers")));
        list.Source.Should().Be(ReferenceSource.Api);
        list.Items.Should().BeEquivalentTo(new[] { new ChannelReferenceItem("KOLAYGELSINMP", "Kolay Gelsin"), new ChannelReferenceItem("YKMP", "Yurtiçi Kargo") },
            "kodsuz kayıt alınmaz");
    }

    [Fact]
    public async Task Trendyol_iade_red_gerekceleri_id_ile_okunur_ve_1651_notu_tasinir()
    {
        var handler = new FakeHandler();
        handler.Responses.Enqueue((HttpStatusCode.OK, """
            [ { "id": 51, "name": "Ürün olağan dışı kullanılmış ve/veya müşteri kaynaklı hasar görmüş" },
              { "id": 1651, "name": "İade paketi tarafıma teslim edilmedi" } ]
            """));
        var adapter = new TrendyolAdapter(new HttpClient(handler), new FakeCredentials(TrendyolCreds), TimeProvider.System, NullLogger<TrendyolAdapter>.Instance);

        var list = await adapter.FetchReferenceAsync(ChannelReferenceType.ClaimRejectReason, Ct);

        handler.Requests.Single().Uri.Should().Be(new Uri("https://apigw.trendyol.com/integration/order/claim-issue-reasons"));
        list.Items.Select(i => i.Code).Should().Equal("51", "1651");
        list.Items[0].DataJson.Should().BeNull();
        JsonDocument.Parse(list.Items[1].DataJson!).RootElement.GetProperty("not").GetString().Should().Contain("24 saat");
    }

    [Fact]
    public async Task Trendyol_kimlik_bilgisi_yoksa_sade_hata_ve_istek_yok()
    {
        var handler = new FakeHandler();
        var adapter = new TrendyolAdapter(new HttpClient(handler), new FakeCredentials(null), TimeProvider.System, NullLogger<TrendyolAdapter>.Instance);

        var act = () => adapter.FetchReferenceAsync(ChannelReferenceType.CargoProvider, Ct);

        (await act.Should().ThrowAsync<ChannelApiException>()).Which.Message.Should().Contain("API bilgileri girilmemiş");
        handler.Requests.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ Beymen (doküman listesi; HTTP yok)

    [Fact]
    public async Task Beymen_listeleri_dokumandaki_sabit_tablolardan_gelir_ve_kanala_istek_atilmaz()
    {
        var handler = new FakeHandler();
        var adapter = new BeymenAdapter(new HttpClient(handler), new FakeCredentials(null), TimeProvider.System, NullLogger<BeymenAdapter>.Instance);

        var cargo = await adapter.FetchReferenceAsync(ChannelReferenceType.CargoProvider, Ct);
        var reasons = await adapter.FetchReferenceAsync(ChannelReferenceType.ClaimRejectReason, Ct);

        handler.Requests.Should().BeEmpty();
        cargo.Source.Should().Be(ReferenceSource.Documentation);
        cargo.Note.Should().Contain("partner-docs.beymen.com");
        cargo.Items.Should().HaveCount(32);
        cargo.Items.Select(i => i.Code).Should().OnlyHaveUniqueItems().And.Contain(["Mng", "YRTICI", "Aras", "HepsiJet", "Kargom Sende"]);

        reasons.Items.Should().HaveCount(12);
        reasons.Items.Select(i => JsonDocument.Parse(i.DataJson!).RootElement.GetProperty("deger").GetInt32()).Should().Equal(Enumerable.Range(0, 12),
            "SellerReasonType enum değerleri 0..11");
        var empty = reasons.Items.Single(i => i.Code == "PackageIsEmpty");
        JsonDocument.Parse(empty.DataJson!).RootElement.GetProperty("gorselZorunlu").GetBoolean().Should().BeFalse("dokümana göre görsel yalnızca PackageIsEmpty'de zorunlu değil");
        JsonDocument.Parse(reasons.Items.Single(i => i.Code == "ProductIsFake").DataJson!).RootElement.GetProperty("gorselZorunlu").GetBoolean().Should().BeTrue();
    }

    // ------------------------------------------------------------------ Hepsiburada (doğrulanmadı → desteklenmiyor)

    [Theory]
    [InlineData(ChannelReferenceType.CargoProvider)]
    [InlineData(ChannelReferenceType.ClaimRejectReason)]
    public async Task Hepsiburada_dogrulanamayan_referanslar_desteklenmiyor_ve_istek_atilmaz(ChannelReferenceType type)
    {
        var handler = new FakeHandler();
        var adapter = new HepsiburadaAdapter(new HttpClient(handler), new FakeCredentials(null), TimeProvider.System, NullLogger<HepsiburadaAdapter>.Instance);

        var act = () => adapter.FetchReferenceAsync(type, Ct);

        await act.Should().ThrowAsync<NotSupportedException>();
        handler.Requests.Should().BeEmpty();
    }
}
