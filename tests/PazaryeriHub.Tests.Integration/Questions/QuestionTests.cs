using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using PazaryeriHub.Adapters.Mock;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Application.Questions;
using PazaryeriHub.Infrastructure.Channels;
using PazaryeriHub.Infrastructure.Questions;
using PazaryeriHub.Tests.Integration.Excel;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Questions;

/// <summary>
/// M5 Soru-Cevap (ADR-029) kabul kriterleri: soru tekil kaydedilir; cevap bir kez gider; Destek gönderebilir, Muhasebe gönderemez (403);
/// onay kutusu zorunlu; her cevap kullanıcı adı ve zamanla denetim izine yazılır; müşteri kişisel verisi saklanmaz.
/// SOFTCOTTON (mock) kanalı kullanılır.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class QuestionTests(TestDatabase db)
{
    private const string Channel = ChannelCodes.Softcotton;
    private static readonly JsonSerializerOptions Json = ExcelImportApiTests.Json;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MockChannelStore Mock => db.Services.GetRequiredService<MockChannelStore>();

    private async Task<int> PrepareAsync(string capability = "KAPALI")
    {
        await using var c = db.Open();
        var channelId = await c.ExecuteScalarAsync<int>("SELECT Id FROM dbo.Channel WHERE Kod = @Channel", new { Channel });
        await c.ExecuteAsync("""
            UPDATE dbo.Channel SET Aktif = 1 WHERE Id = @channelId;
            UPDATE dbo.ChannelCapability SET Durum = @capability WHERE ChannelId = @channelId AND Yetenek = 'SORU_CEVAP';
            UPDATE dbo.ChannelCommand SET Durum = 'IPTAL' WHERE ChannelId = @channelId AND Tip = 'SORU_CEVAP' AND Durum IN ('BEKLIYOR', 'ONAY_BEKLIYOR', 'HATA');
            DELETE FROM dbo.ChannelJobState WHERE ChannelId = @channelId AND IsTipi = 'SORU_CEK';
            """, new { channelId, capability });
        Mock.UnsupportedQuestions.TryRemove(Channel, out _);
        return channelId;
    }

    private async Task RestoreAsync()
    {
        await using var c = db.Open();
        await c.ExecuteAsync("""
            UPDATE dbo.Channel SET Aktif = 0 WHERE Kod = @Channel;
            UPDATE cap SET Durum = 'KAPALI' FROM dbo.ChannelCapability cap JOIN dbo.Channel c ON c.Id = cap.ChannelId WHERE c.Kod = @Channel AND cap.Yetenek = 'SORU_CEVAP';
            """, new { Channel });
        Mock.UnsupportedQuestions.TryRemove(Channel, out _);
    }

    private static NormalizedQuestion NewQuestion(string no) =>
        new(no, "Bu ürünün kumaşı yüzde yüz pamuk mu?", "Test Tişört", "MODEL-" + no, DateTime.UtcNow.AddMinutes(-5), "WAITING_FOR_ANSWER",
            QuestionChannelState.Waiting, DateTime.UtcNow.AddDays(1));

    private async Task<QuestionFetchJob.RunResult> FetchAsync()
    {
        using var scope = db.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<QuestionFetchJob>().RunAsync(Channel, false, Ct);
    }

    private async Task SendCommandsAsync()
    {
        using var scope = db.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ChannelCommandJob>().RunAsync(Channel, Ct);
    }

    private async Task<long> QuestionIdAsync(int channelId, string no)
    {
        await using var c = db.Open();
        return await c.ExecuteScalarAsync<long>("SELECT Id FROM dbo.CustomerQuestion WHERE ChannelId = @channelId AND KanalSoruNo = @no", new { channelId, no });
    }

    [Fact]
    public async Task Ayni_soru_5_kez_gelse_tek_kayit_olusur_durum_guncellenir_musteri_verisi_saklanmaz()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareAsync();
        try
        {
            var no = "Q-" + Guid.NewGuid().ToString("N")[..10];
            Mock.UpsertQuestion(Channel, NewQuestion(no));
            for (var i = 0; i < 5; i++)
            {
                (await FetchAsync()).Skipped.Should().BeFalse();
            }

            await using (var c = db.Open())
            {
                (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.CustomerQuestion WHERE ChannelId = @channelId AND KanalSoruNo = @no", new { channelId, no }))
                    .Should().Be(1);
                var columns = (await c.QueryAsync<string>("SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('dbo.CustomerQuestion')")).ToList();
                columns.Should().NotContain(n => n.Contains("Musteri", StringComparison.OrdinalIgnoreCase) || n.Contains("Customer", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("Kullanici", StringComparison.OrdinalIgnoreCase) || n.Contains("Eposta", StringComparison.OrdinalIgnoreCase), "KVKK: müşteri bilgisi saklanmaz");
            }

            var id = await QuestionIdAsync(channelId, no);
            var detail = await db.Api.GetFromJsonAsync<QuestionDetail>($"/api/questions/{id}", Json, Ct);
            detail!.Summary.State.Should().Be(QuestionStates.Waiting);
            detail.Summary.ProductName.Should().Be("Test Tişört");

            // Kanalda cevaplandı → bir sonraki çekimde güncellenir
            Mock.UpsertQuestion(Channel, NewQuestion(no) with { State = QuestionChannelState.Answered, ChannelStatus = "ANSWERED", AnswerText = "Evet, %100 pamuk." });
            await FetchAsync();
            detail = await db.Api.GetFromJsonAsync<QuestionDetail>($"/api/questions/{id}", Json, Ct);
            detail!.Summary.State.Should().Be(QuestionStates.Answered);
            detail.Summary.ChannelAnswer.Should().Be("Evet, %100 pamuk.");
            detail.CanAnswer.Should().BeFalse();
        }
        finally
        {
            await RestoreAsync();
        }
    }

    [Fact]
    public async Task Destek_cevap_gonderebilir_Muhasebe_gonderemez_onay_kutusu_zorunlu_cevap_bir_kez_gider_ve_denetlenir()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareAsync();
        try
        {
            var no = "Q-" + Guid.NewGuid().ToString("N")[..10];
            Mock.UpsertQuestion(Channel, NewQuestion(no));
            await FetchAsync();
            var id = await QuestionIdAsync(channelId, no);
            const string answer = "Merhaba, ürünümüz %100 pamuktur. İyi alışverişler dileriz.";

            using var destek = await db.ApiHost.CreateClientAsAsync(Roles.Support);
            using var muhasebe = await db.ApiHost.CreateClientAsAsync(Roles.Accounting);

            // Yetenek kapalı → kayıt oluşmaz
            var closed = await destek.PostAsJsonAsync($"/api/questions/{id}/answer", new QuestionAnswerInput(answer, true), Json, Ct);
            closed.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await closed.Content.ReadAsStringAsync(Ct)).Should().Contain("kapalı");

            await using (var c = db.Open())
            {
                await c.ExecuteAsync("UPDATE dbo.ChannelCapability SET Durum = 'ACIK' WHERE ChannelId = @channelId AND Yetenek = 'SORU_CEVAP'", new { channelId });
            }

            (await muhasebe.PostAsJsonAsync($"/api/questions/{id}/answer", new QuestionAnswerInput(answer, true), Json, Ct))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await destek.PostAsJsonAsync($"/api/questions/{id}/answer", new QuestionAnswerInput(answer, false), Json, Ct))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest, "onay kutusu işaretlenmeden cevap gönderilmez");
            (await destek.PostAsJsonAsync($"/api/questions/{id}/answer", new QuestionAnswerInput("Evet", true), Json, Ct))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest, "kanalın en az 10 karakter kuralı");

            var ok = await destek.PostAsJsonAsync($"/api/questions/{id}/answer", new QuestionAnswerInput(answer, true), Json, Ct);
            ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync(Ct));
            (await ok.Content.ReadFromJsonAsync<QuestionAnswerResult>(Json, Ct))!.Outcome.Should().Be("KUYRUGA_ALINDI");

            (await destek.PostAsJsonAsync($"/api/questions/{id}/answer", new QuestionAnswerInput(answer + " Ek bilgi.", true), Json, Ct))
                .StatusCode.Should().Be(HttpStatusCode.Conflict, "soru başına tek etkin cevap");

            await SendCommandsAsync();
            await SendCommandsAsync();
            Mock.AnswersOf(Channel).Where(a => a.QuestionNo == no).Should().ContainSingle().Which.Text.Should().Be(answer);

            await FetchAsync();
            var detail = await db.Api.GetFromJsonAsync<QuestionDetail>($"/api/questions/{id}", Json, Ct);
            detail!.Summary.State.Should().Be(QuestionStates.Answered);
            detail.Answers.Should().ContainSingle();
            detail.Answers[0].User.Should().Be(ApiFactory.UsersByRole[Roles.Support]);
            detail.Answers[0].CommandState.Should().Be(ChannelCommandStates.Sent);
            detail.Answers[0].Text.Should().Be(answer);
        }
        finally
        {
            await RestoreAsync();
        }
    }

    [Fact]
    public async Task DENEME_yeteneginde_cevap_Yonetici_onayi_bekler()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareAsync("DENEME");
        try
        {
            var no = "Q-" + Guid.NewGuid().ToString("N")[..10];
            Mock.UpsertQuestion(Channel, NewQuestion(no));
            await FetchAsync();
            var id = await QuestionIdAsync(channelId, no);

            var response = await db.Api.PostAsJsonAsync($"/api/questions/{id}/answer", new QuestionAnswerInput("Deneme cevabıdır, teşekkürler.", true), Json, Ct);
            (await response.Content.ReadFromJsonAsync<QuestionAnswerResult>(Json, Ct))!.Outcome.Should().Be("ONAY_BEKLIYOR");
            await SendCommandsAsync();
            Mock.AnswersOf(Channel).Should().NotContain(a => a.QuestionNo == no, "onaylanmadan kanala gitmez");
        }
        finally
        {
            await RestoreAsync();
        }
    }

    [Fact]
    public async Task Soru_API_si_dogrulanmamis_kanal_atlanir_hata_sayilmaz()
    {
        db.SkipIfUnavailable();
        await PrepareAsync("ACIK");
        try
        {
            Mock.UnsupportedQuestions[Channel] = true;
            var result = await FetchAsync();
            result.Skipped.Should().BeTrue();
            result.Message.Should().Contain("doğrulanmadı");

            var list = await db.Api.GetFromJsonAsync<PagedResult<QuestionListItem>>("/api/questions?state=CEVAP_BEKLIYOR&pageSize=5", Json, Ct);
            list.Should().NotBeNull();
        }
        finally
        {
            await RestoreAsync();
        }
    }
}
