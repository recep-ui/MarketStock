using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Tests.Integration.Excel;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Channels;

/// <summary>ADR-033: kanal durum kartları uç noktası. Gizli bilgi dönmez; hata türü ve genel durum hesaplanır.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class ChannelStatusEndpointTests(TestDatabase db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Durum_ucu_mod_ortam_ve_hata_turunu_doner_gizli_bilgi_dondurmez()
    {
        db.SkipIfUnavailable();
        var store = db.Services.GetRequiredService<IChannelCredentialStore>();
        var original = await store.GetJsonAsync("TRENDYOL", Ct);
        const string secret = "durum-testi-gizli-deger-7f3a";

        int softcottonId;
        (DateTime? SonBasarili, DateTime? SonDeneme, string? SonHata, int ArdisikHata)? originalJob;
        await using (var c = db.Open())
        {
            softcottonId = await c.ExecuteScalarAsync<int>("SELECT Id FROM dbo.Channel WHERE Kod = 'SOFTCOTTON'");
            originalJob = await c.QuerySingleOrDefaultAsync<(DateTime?, DateTime?, string?, int)?>(
                "SELECT SonBasarili, SonDeneme, SonHata, ArdisikHata FROM dbo.ChannelJobState WHERE ChannelId = @softcottonId AND IsTipi = 'SIPARIS_CEK'",
                new { softcottonId });
            await c.ExecuteAsync("""
                DELETE FROM dbo.ChannelJobState WHERE ChannelId = @softcottonId AND IsTipi = 'SIPARIS_CEK';
                INSERT dbo.ChannelJobState (ChannelId, IsTipi, SonBasarili, SonDeneme, SonHata, ArdisikHata)
                VALUES (@softcottonId, 'SIPARIS_CEK', NULL, SYSUTCDATETIME(), N'Kanala ulaşılamadı. | Sipariş X: geçersiz satır', 3);
                """, new { softcottonId });
        }
        try
        {
            await store.SaveJsonAsync("TRENDYOL",
                JsonSerializer.Serialize(new { sellerId = 1, apiKey = secret, apiSecret = secret, environment = "Stage" }), Ct);

            var response = await db.Api.GetAsync("/api/channels/status", Ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var text = await response.Content.ReadAsStringAsync(Ct);
            text.Should().NotContain(secret, "API bilgisinin hiçbir alanı yanıta girmemeli");

            var list = JsonSerializer.Deserialize<List<ChannelStatusDto>>(text, ExcelImportApiTests.Json)!;
            list.Select(s => s.Code).Should().BeEquivalentTo(["TRENDYOL", "HEPSIBURADA", "BEYMEN", "SOFTCOTTON"]);
            list.Should().OnlyContain(s => s.Mode == "MOCK", "test sunucusunda tüm kanallar mock");
            list.Should().OnlyContain(s => s.Capabilities.Count == ChannelCapabilities.All.Count);

            var trendyol = list.Single(s => s.Code == "TRENDYOL");
            trendyol.HasCredentials.Should().BeTrue();
            trendyol.Environment.Should().Be("Stage");
            trendyol.RealAdapterAvailable.Should().BeTrue();

            var softcotton = list.Single(s => s.Code == "SOFTCOTTON");
            softcotton.RealAdapterAvailable.Should().BeFalse();
            var job = softcotton.Jobs.Single(j => j.JobType == "SIPARIS_CEK");
            job.ErrorInfo!.Kind.Should().Be(ChannelErrorKinds.Unreachable);
            job.ErrorInfo.Problems.Should().HaveCount(2);

            // Okuma tüm roller (Destek dahil)
            using var destek = await db.ApiHost.CreateClientAsAsync(Roles.Support);
            (await destek.GetAsync("/api/channels/status", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            if (original is null) await store.ClearAsync("TRENDYOL", Ct);
            else await store.SaveJsonAsync("TRENDYOL", original, Ct);

            await using var c = db.Open();
            await c.ExecuteAsync("DELETE FROM dbo.ChannelJobState WHERE ChannelId = @softcottonId AND IsTipi = 'SIPARIS_CEK'", new { softcottonId });
            if (originalJob is { } o)
            {
                await c.ExecuteAsync("""
                    INSERT dbo.ChannelJobState (ChannelId, IsTipi, SonBasarili, SonDeneme, SonHata, ArdisikHata)
                    VALUES (@softcottonId, 'SIPARIS_CEK', @SonBasarili, @SonDeneme, @SonHata, @ArdisikHata)
                    """, new { softcottonId, o.SonBasarili, o.SonDeneme, o.SonHata, o.ArdisikHata });
            }
        }
    }
}
