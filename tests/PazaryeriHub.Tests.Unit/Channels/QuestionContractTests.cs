using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PazaryeriHub.Adapters.Beymen;
using PazaryeriHub.Adapters.Hepsiburada;
using PazaryeriHub.Adapters.Trendyol;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Questions;

namespace PazaryeriHub.Tests.Unit.Channels;

/// <summary>
/// Soru-Cevap contract testleri (ADR-029). Örnek yanıtlar resmi dokümandaki örneklerdir (gerçek hesap kaydı DEĞİL).
/// KVKK: müşteri adı/kimliği yanıtta olsa da normalize modele taşınmaz.
/// </summary>
public sealed class QuestionContractTests
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

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (TrendyolAdapter, FakeHandler) Trendyol()
    {
        var h = new FakeHandler();
        return (new TrendyolAdapter(new HttpClient(h), new FakeCredentials("""{"sellerId":12345,"apiKey":"k","apiSecret":"s","environment":"Prod"}"""),
            new FixedClock(Now), NullLogger<TrendyolAdapter>.Instance), h);
    }

    private static (BeymenAdapter, FakeHandler) Beymen()
    {
        var h = new FakeHandler();
        return (new BeymenAdapter(new HttpClient(h), new FakeCredentials("""{"apiKey":"k","apiPassword":"p","environment":"Stage"}"""),
            new FixedClock(Now), NullLogger<BeymenAdapter>.Instance), h);
    }

    // ------------------------------------------------------------------ Trendyol

    private const string TrendyolPage = """
        {"content":[
          {"answer":null,"creationDate":1763460000000,"customerId":888888888,"id":101,"productName":"Basic Tişört","productMainId":"SC-TSHIRT",
           "public":true,"showUserName":true,"status":"WAITING_FOR_ANSWER","text":"Kumaşı pamuk mu?","userName":"Ayşe Y.","webUrl":"x"},
          {"answer":{"creationDate":0,"hasPrivateInfo":false,"id":5,"text":"Evet."},"creationDate":1763400000000,"customerId":7,"id":102,
           "productName":"Basic Tişört","status":"ANSWERED","text":"Kalıbı dar mı?","userName":"Mehmet K."},
          {"creationDate":1763300000000,"id":103,"status":"UNANSWERED","text":"Kargo ne zaman?"}
        ],"page":0,"size":50,"totalElements":3,"totalPages":1}
        """;

    [Fact]
    public async Task Trendyol_sorulari_iki_haftalik_parcalarla_ceker_musteri_bilgisini_tasimaz()
    {
        var (adapter, http) = Trendyol();
        http.Respond = _ => (HttpStatusCode.OK, TrendyolPage);

        var result = await adapter.FetchQuestionsAsync(Now.UtcDateTime.AddDays(-20), Ct);

        http.Requests.Should().HaveCount(2, "20 gün → 14 + 6 günlük iki pencere (en fazla 2 hafta)");
        http.Requests[0].Uri.AbsolutePath.Should().Be("/integration/qna/sellers/12345/questions/filter");
        http.Requests[0].Uri.Query.Should().Contain("size=50").And.Contain("page=0").And.Contain("startDate=").And.Contain("endDate=");

        result.Should().HaveCount(3, "aynı soru iki pencerede gelse de tekilleşir");
        var waiting = result.Single(q => q.QuestionNo == "101");
        waiting.State.Should().Be(QuestionChannelState.Waiting);
        waiting.ProductCode.Should().Be("SC-TSHIRT");
        waiting.CreatedUtc.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1763460000000).UtcDateTime);
        result.Single(q => q.QuestionNo == "102").Should().Match<NormalizedQuestion>(q => q.State == QuestionChannelState.Answered && q.AnswerText == "Evet.");
        result.Single(q => q.QuestionNo == "103").State.Should().Be(QuestionChannelState.Closed);
        JsonSerializer.Serialize(result).Should().NotContain("Ayşe").And.NotContain("Mehmet").And.NotContain("888888888");
    }

    [Fact]
    public async Task Trendyol_cevap_text_govdesiyle_gider_uzunluk_kurali_uygulanir()
    {
        var (adapter, http) = Trendyol();
        http.Respond = _ => (HttpStatusCode.OK, """{"answerId":77}""");

        await adapter.AnswerQuestionAsync(new QuestionAnswerRequest("101", "  Evet, ürün %100 pamuktur.  "), Ct);

        var r = http.Requests.Single();
        r.Method.Should().Be(HttpMethod.Post);
        r.Uri.ToString().Should().Be("https://apigw.trendyol.com/integration/qna/sellers/12345/questions/101/answers");
        JsonDocument.Parse(r.Body!).RootElement.GetProperty("text").GetString().Should().Be("Evet, ürün %100 pamuktur.");

        await FluentActions.Invoking(() => adapter.AnswerQuestionAsync(new QuestionAnswerRequest("101", "Evet"), Ct)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() => adapter.AnswerQuestionAsync(new QuestionAnswerRequest("101", new string('a', 2001)), Ct)).Should().ThrowAsync<ArgumentException>();
        http.Requests.Should().HaveCount(1);
        adapter.DescribeQuestionAnswer().Should().Match<QuestionAnswerRule>(r2 => r2.Supported && r2.MinLength == 10 && r2.MaxLength == 2000);
    }

    // ------------------------------------------------------------------ Beymen

    [Fact]
    public async Task Beymen_sorulari_filter_ucundan_ceker_durum_kodlari_eslenir()
    {
        var (adapter, http) = Beymen();
        http.Respond = _ => (HttpStatusCode.OK, """
            {"items":[
              {"id":551,"productName":"Basic Tişört","modelCode":"SC-TSHIRT","color":"Beyaz","size":"M","brandName":"Softcotton","question":"Kalıbı nasıl?",
               "answer":null,"imageUrl":"x","status":0,"maxPermittedResponseTime":"2026-11-19T13:12:07.547","actionDate":null,"createdDate":"2026-11-18T13:12:07.56"},
              {"id":552,"question":"Stok gelecek mi?","answer":"Evet","status":4,"createdDate":"2026-11-17T10:00:00"},
              {"id":553,"question":"?","status":1,"createdDate":"2026-11-17T10:00:00"}
            ],"page":0,"itemsPerPage":100,"pageCount":1,"total":3}
            """);

        var result = await adapter.FetchQuestionsAsync(Now.UtcDateTime.AddDays(-1), Ct);

        http.Requests.Single().Uri.AbsolutePath.Should().Be("/questions/filter");
        var q = result.Single(x => x.QuestionNo == "551");
        q.State.Should().Be(QuestionChannelState.Waiting);
        q.ChannelStatus.Should().Be("PendingResponse");
        q.AnswerDeadlineUtc.Should().Be(new DateTime(2026, 11, 19, 10, 12, 7, 547, DateTimeKind.Utc), "Türkiye saati (UTC+3) varsayıldı");
        result.Single(x => x.QuestionNo == "552").State.Should().Be(QuestionChannelState.Answered);
        result.Single(x => x.QuestionNo == "553").State.Should().Be(QuestionChannelState.Closed);
    }

    [Fact]
    public async Task Beymen_cevap_answer_govdesiyle_gider()
    {
        var (adapter, http) = Beymen();
        await adapter.AnswerQuestionAsync(new QuestionAnswerRequest("551", "Kalıbı normaldir, kendi bedeninizi seçebilirsiniz."), Ct);
        var r = http.Requests.Single();
        r.Uri.ToString().Should().Be("https://stage-partner-api.beymen.com/questions/551/answer");
        JsonDocument.Parse(r.Body!).RootElement.GetProperty("answer").GetString().Should().StartWith("Kalıbı normaldir");
    }

    // ------------------------------------------------------------------ Hepsiburada

    [Fact]
    public async Task Hepsiburada_soru_semasi_dogrulanmadigi_icin_desteklenmiyor()
    {
        var h = new FakeHandler();
        var adapter = new HepsiburadaAdapter(new HttpClient(h),
            new FakeCredentials("""{"merchantId":"b2910839-83b9-4d45-adb6-86bad457edcb","userName":"u","password":"p","environment":"Test"}"""),
            TimeProvider.System, NullLogger<HepsiburadaAdapter>.Instance);
        await FluentActions.Invoking(() => adapter.FetchQuestionsAsync(DateTime.UtcNow, Ct)).Should().ThrowAsync<NotSupportedException>();
        await FluentActions.Invoking(() => adapter.AnswerQuestionAsync(new QuestionAnswerRequest("1", "0123456789"), Ct)).Should().ThrowAsync<NotSupportedException>();
        adapter.DescribeQuestionAnswer().Supported.Should().BeFalse();
        h.Requests.Should().BeEmpty();
    }
}
