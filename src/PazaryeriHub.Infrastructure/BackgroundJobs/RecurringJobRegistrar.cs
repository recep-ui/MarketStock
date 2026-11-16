using Hangfire;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Orders;

namespace PazaryeriHub.Infrastructure.BackgroundJobs;

/// <summary>
/// Tekrarlayan job'ları Hangfire'a kaydeder. Veritabanı henüz erişilebilir değilse uygulamayı düşürmez,
/// belirli aralıklarla yeniden dener.
/// </summary>
public sealed class RecurringJobRegistrar(
    IRecurringJobManager recurringJobs,
    IOptions<HangfireSettings> settings,
    IOptions<OrderOptions> orderOptions,
    IEnumerable<IChannelAdapter> adapters,
    ILogger<RecurringJobRegistrar> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Register();
                logger.LogInformation("Tekrarlayan job kayıtları tamamlandı.");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Tekrarlayan job'lar kaydedilemedi, {Saniye} sn sonra yeniden denenecek: {Hata}",
                    RetryDelay.TotalSeconds, ex.Message);
            }

            try
            {
                await Task.Delay(RetryDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Register()
    {
        var utc = new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc };

        var cron = settings.Value.HeartbeatCron;
        if (string.IsNullOrWhiteSpace(cron))
        {
            recurringJobs.RemoveIfExists(HeartbeatJob.RecurringJobId);
        }
        else
        {
            recurringJobs.AddOrUpdate<HeartbeatJob>(HeartbeatJob.RecurringJobId, job => job.RunAsync(), cron, utc);
        }

        // SiparisCek: kayıtlı her kanal adaptörü için bir tekrarlayan job. Pasif kanallarda job hemen çıkar.
        var fetchCron = orderOptions.Value.FetchCron;
        foreach (var code in adapters.Select(a => a.ChannelCode).Distinct(StringComparer.Ordinal))
        {
            var id = OrderFetchJob.RecurringJobId(code);
            if (string.IsNullOrWhiteSpace(fetchCron))
            {
                recurringJobs.RemoveIfExists(id);
            }
            else
            {
                recurringJobs.AddOrUpdate<OrderFetchJob>(id, job => job.RunAsync(code, CancellationToken.None), fetchCron, utc);
            }
        }
    }
}
