using System.Net;
using System.Net.Http.Json;
using Dapper;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using PazaryeriHub.Api.Endpoints;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Infrastructure.BackgroundJobs;
using PazaryeriHub.Tests.Integration.Excel;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Orders;

/// <summary>Panelden "Kanaldan sipariş çek" (yalnızca okuma) ve kanal işlerinin son çekim durumu.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class ChannelFetchEndpointTests(TestDatabase db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Siparis_cekme_Yonetici_ile_kuyruga_alinir_Depo_alamaz_son_cekim_durumu_doner()
    {
        db.SkipIfUnavailable();

        var response = await db.Api.PostAsync("/api/orders/fetch/softcotton", null, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(Ct))!["jobId"];
        db.Services.GetRequiredService<IBackgroundJobClient>().Delete(jobId); // test sunucusu bu kuyruğu işlemez

        (await db.Api.PostAsync("/api/orders/fetch/bilinmeyen", null, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var depo = await db.ApiHost.CreateClientAsAsync(Roles.Warehouse))
        {
            (await depo.PostAsync("/api/orders/fetch/softcotton", null, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        // Job doğrudan çalıştırılınca son çekim kaydı oluşur ve uçtan okunur
        int channelId;
        await using (var c = db.Open())
        {
            channelId = await c.ExecuteScalarAsync<int>("SELECT Id FROM dbo.Channel WHERE Kod = 'SOFTCOTTON'");
            await c.ExecuteAsync("UPDATE dbo.Channel SET Aktif = 1 WHERE Id = @channelId", new { channelId });
        }
        try
        {
            using (var scope = db.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<OrderFetchJob>().RunAsync("SOFTCOTTON", Ct);
            }
            var states = await db.Api.GetFromJsonAsync<List<ChannelJobStateDto>>($"/api/channels/{channelId}/job-states", ExcelImportApiTests.Json, Ct);
            states!.Should().Contain(s => s.JobType == OrderFetchJob.JobType && s.LastAttemptUtc != null && s.LastError == null);
        }
        finally
        {
            await using var c = db.Open();
            await c.ExecuteAsync("UPDATE dbo.Channel SET Aktif = 0 WHERE Id = @channelId", new { channelId });
        }
    }
}
