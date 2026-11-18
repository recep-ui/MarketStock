using System.Diagnostics;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Infrastructure.BackgroundJobs;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Orders;

/// <summary>ADR-030: SiparisCek kanal başına aralıkla Hangfire'a kaydedilir (Trendyol/Hepsiburada 1 dk, diğerleri genel ayar).</summary>
[Collection(DatabaseCollection.Name)]
public sealed class OrderFetchScheduleTests(TestDatabase db)
{
    [Fact]
    public async Task SiparisCek_kanal_basina_cron_ile_kaydedilir()
    {
        db.SkipIfUnavailable();
        // Job'lar kaydedilir ama bu sunucu yalnızca boş kuyruğu dinler (ApiFactory); test sonunda kayıtlar silinir.
        var host = new ApiFactory(db.ConnectionString!, new Dictionary<string, string?> { ["Siparis:FetchCron"] = "*/2 * * * *" });
        try
        {
            _ = host.Services; // uygulamayı başlat (RecurringJobRegistrar)
            var storage = host.Services.GetRequiredService<JobStorage>();
            Dictionary<string, string> crons = [];
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(30))
            {
                using var connection = storage.GetConnection();
                crons = connection.GetRecurringJobs()
                    .Where(j => j.Id.StartsWith("siparis-cek-", StringComparison.Ordinal))
                    .ToDictionary(j => j.Id, j => j.Cron);
                if (crons.Count == ChannelCodes.All.Length)
                {
                    break;
                }
                await Task.Delay(250, TestContext.Current.CancellationToken);
            }

            crons[OrderFetchJob.RecurringJobId(ChannelCodes.Trendyol)].Should().Be("* * * * *");
            crons[OrderFetchJob.RecurringJobId(ChannelCodes.Hepsiburada)].Should().Be("* * * * *");
            crons[OrderFetchJob.RecurringJobId(ChannelCodes.Beymen)].Should().Be("*/2 * * * *");
        }
        finally
        {
            var jobs = host.Services.GetRequiredService<IRecurringJobManager>();
            foreach (var code in ChannelCodes.All)
            {
                jobs.RemoveIfExists(OrderFetchJob.RecurringJobId(code));
            }
            await host.DisposeAsync();
        }
    }
}
