using System.Net;
using System.Net.Http.Json;
using Dapper;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using PazaryeriHub.Adapters.Mock;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Infrastructure.Channels;
using PazaryeriHub.Tests.Integration.Excel;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Channels;

/// <summary>Satıcı Bilgileri (ADR-025): ReferansCek job'ı (mock kanal), tekillik, pasifleştirme, desteklenmeyen tip, hata ve API yetkileri.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class ReferenceFetchJobTests(TestDatabase db)
{
    private const string Channel = "SOFTCOTTON"; // gerçek adaptörü olmayan mock kanal

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MockChannelStore Mock => db.Services.GetRequiredService<MockChannelStore>();

    [Fact]
    public async Task Referanslar_tekil_yazilir_kaybolan_pasif_olur_bos_liste_ve_desteklenmeyen_tip_kayitlara_dokunmaz()
    {
        db.SkipIfUnavailable();
        var p = "RF" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var channelId = await ChannelIdAsync();
        try
        {
            Mock.SetReferences(Channel, ChannelReferenceType.CargoProvider, [new($"{p}-A", "Kargo A"), new($"{p}-B", "Kargo B"), new($"{p}-B", "tekrar"), new($"{p}-C", "Kargo C")]);
            Mock.SetReferences(Channel, ChannelReferenceType.ClaimRejectReason, [new($"{p}-R1", "Gerekçe 1", """{"x":1}""")]);

            var first = await RunAsync();
            first.Should().ContainEquivalentOf(new { Type = "KARGO_FIRMASI", Outcome = "GUNCELLENDI", Count = 3, Source = "API" });
            first.Should().ContainEquivalentOf(new { Type = "IADE_RED_GEREKCE", Outcome = "GUNCELLENDI", Count = 1 });
            (await RowsAsync(channelId, p)).Should().BeEquivalentTo(new[]
            {
                ($"{p}-A", "Kargo A", true), ($"{p}-B", "Kargo B", true), ($"{p}-C", "Kargo C", true), ($"{p}-R1", "Gerekçe 1", true)
            }, "aynı kod ikinci kez gelirse ilki alınır");

            // İkinci çekim yeni kayıt açmaz; kaybolan (C) pasif olur, adı değişen (A) güncellenir
            Mock.SetReferences(Channel, ChannelReferenceType.CargoProvider, [new($"{p}-A", "Kargo A Yeni"), new($"{p}-B", "Kargo B")]);
            var second = await RunAsync();
            second.Single(r => r.Type == "KARGO_FIRMASI").Deactivated.Should().BeGreaterThanOrEqualTo(1);
            (await RowsAsync(channelId, p)).Should().BeEquivalentTo(new[]
            {
                ($"{p}-A", "Kargo A Yeni", true), ($"{p}-B", "Kargo B", true), ($"{p}-C", "Kargo C", false), ($"{p}-R1", "Gerekçe 1", true)
            });

            // Boş liste → mevcut kayıtlar korunur; desteklenmeyen tip → dokunulmaz, hata sayılmaz
            Mock.SetReferences(Channel, ChannelReferenceType.CargoProvider, []);
            Mock.UnsupportedReferences[(Channel, ChannelReferenceType.ClaimRejectReason)] = true;
            var third = await RunAsync();
            third.Single(r => r.Type == "KARGO_FIRMASI").Message.Should().Contain("boş liste");
            third.Single(r => r.Type == "IADE_RED_GEREKCE").Outcome.Should().Be("DESTEKLENMIYOR");
            (await RowsAsync(channelId, p)).Count(r => r.Aktif).Should().Be(3);

            var view = await db.Api.GetFromJsonAsync<ChannelReferenceView>($"/api/channels/{channelId}/references?type=KARGO_FIRMASI", ExcelImportApiTests.Json, Ct);
            view!.Items.Should().OnlyContain(i => i.Type == "KARGO_FIRMASI" && i.Active);
            view.Items.Select(i => i.Code).Should().Contain([$"{p}-A", $"{p}-B"]).And.NotContain($"{p}-C");
            view.Status.LastError.Should().BeNull();
            view.Status.Results.Should().HaveCount(2);

            var withInactive = await db.Api.GetFromJsonAsync<ChannelReferenceView>(
                $"/api/channels/{channelId}/references?type=KARGO_FIRMASI&includeInactive=true", ExcelImportApiTests.Json, Ct);
            withInactive!.Items.Should().Contain(i => i.Code == $"{p}-C" && !i.Active);
        }
        finally
        {
            Mock.SetReferences(Channel, ChannelReferenceType.CargoProvider, []);
            Mock.SetReferences(Channel, ChannelReferenceType.ClaimRejectReason, []);
            Mock.UnsupportedReferences.Clear();
        }
    }

    [Fact]
    public async Task Kanal_hatasi_durum_kaydina_yazilir_ve_yenileme_istegi_yetki_ve_tekillik_kurallarina_uyar()
    {
        db.SkipIfUnavailable();
        var channelId = await ChannelIdAsync();

        Mock.FailNextReference[Channel] = new ChannelApiException(Channel, "Kanal geçici olarak yanıt vermiyor.", 503, transient: true);
        var results = await RunAsync();
        results.Should().Contain(r => r.Outcome == "HATA" && r.Message == "Kanal geçici olarak yanıt vermiyor.");
        var view = await db.Api.GetFromJsonAsync<ChannelReferenceView>($"/api/channels/{channelId}/references", ExcelImportApiTests.Json, Ct);
        view!.Status.LastError.Should().Contain("Kanal geçici olarak yanıt vermiyor.");

        (await db.Api.GetAsync($"/api/channels/{channelId}/references?type=BILINMEYEN", Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await db.Api.GetAsync("/api/channels/999999/references", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var depo = await db.ApiHost.CreateClientAsAsync(Roles.Warehouse);
        (await depo.GetAsync($"/api/channels/{channelId}/references", Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "okuma tüm rollere açık");
        (await depo.PostAsync($"/api/channels/{channelId}/references/refresh", null, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var first = await db.Api.PostAsync($"/api/channels/{channelId}/references/refresh", null, Ct);
        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = (await first.Content.ReadFromJsonAsync<Dictionary<string, string>>(Ct))!["jobId"];
        try
        {
            (await db.Api.PostAsync($"/api/channels/{channelId}/references/refresh", null, Ct)).StatusCode
                .Should().Be(HttpStatusCode.Conflict, "aynı kanal için iş zaten kuyrukta");
        }
        finally
        {
            db.Services.GetRequiredService<IBackgroundJobClient>().Delete(jobId);
            await RunAsync(); // durumu CALISIYOR'dan çıkar
        }
    }

    private async Task<IReadOnlyList<ChannelReferenceTypeResult>> RunAsync()
    {
        using var scope = db.Services.CreateScope();
        var job = ActivatorUtilities.CreateInstance<ReferenceFetchJob>(scope.ServiceProvider);
        return await job.RunAsync(Channel, scheduled: false, Ct);
    }

    private async Task<int> ChannelIdAsync()
    {
        await using var c = db.Open();
        return await c.ExecuteScalarAsync<int>("SELECT Id FROM dbo.Channel WHERE Kod = @Channel", new { Channel });
    }

    private async Task<List<(string KanalKimlik, string Ad, bool Aktif)>> RowsAsync(int channelId, string prefix)
    {
        await using var c = db.Open();
        return (await c.QueryAsync<(string, string, bool)>(
            "SELECT KanalKimlik, Ad, Aktif FROM dbo.ChannelReference WHERE ChannelId = @channelId AND KanalKimlik LIKE @p",
            new { channelId, p = prefix + "%" })).ToList();
    }
}
