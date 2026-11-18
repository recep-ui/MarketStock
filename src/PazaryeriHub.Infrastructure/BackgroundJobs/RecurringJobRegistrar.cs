using Hangfire;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Application.Sync;
using PazaryeriHub.Infrastructure.Sync;

namespace PazaryeriHub.Infrastructure.BackgroundJobs;

/// <summary>
/// Tekrarlayan job'ları Hangfire'a kaydeder. Veritabanı henüz erişilebilir değilse uygulamayı düşürmez,
/// belirli aralıklarla yeniden dener.
/// </summary>
public sealed class RecurringJobRegistrar(
    IRecurringJobManager recurringJobs,
    IOptions<OrderOptions> orderOptions,
    IOptions<SyncOptions> syncOptions,
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

    /// <summary>Aşama 0'daki geçici ölçüm job'ı (kaldırıldı); eski kurulumlarda kaydı temizlenir.</summary>
    private const string LegacyHeartbeatJobId = "asama0-heartbeat";

    private void Register()
    {
        var utc = new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc };
        recurringJobs.RemoveIfExists(LegacyHeartbeatJobId);

        var codes = adapters.Select(a => a.ChannelCode).Distinct(StringComparer.Ordinal).ToList();
        var sync = syncOptions.Value;

        // Kanal başına: SiparisCek, StokGonder, FiyatGonder, Mutabakat, ReferansCek, KanalKomutGonder. Pasif kanallarda job'lar hemen çıkar.
        foreach (var code in codes)
        {
            Set<OrderFetchJob>(OrderFetchJob.RecurringJobId(code), orderOptions.Value.FetchCron, job => job.RunAsync(code, CancellationToken.None), utc);
            Set<StockPushJob>(StockPushJob.RecurringJobId(code), sync.PushCron, job => job.RunAsync(code, CancellationToken.None), utc);
            Set<PricePushJob>(PricePushJob.RecurringJobId(code), sync.PricePushCron, job => job.RunAsync(code, CancellationToken.None), utc);
            Set<PazaryeriHub.Infrastructure.Questions.QuestionFetchJob>(PazaryeriHub.Infrastructure.Questions.QuestionFetchJob.RecurringJobId(code),
                sync.QuestionFetchCron, job => job.RunAsync(code, true, CancellationToken.None), utc);
            Set<ReconciliationJob>(ReconciliationJob.RecurringJobId(code), sync.ReconciliationCron, job => job.RunAsync(code, CancellationToken.None), utc);
            Set<PazaryeriHub.Infrastructure.Channels.ReferenceFetchJob>(PazaryeriHub.Infrastructure.Channels.ReferenceFetchJob.RecurringJobId(code),
                sync.ReferenceCron, job => job.RunAsync(code, true, CancellationToken.None), utc);
            Set<PazaryeriHub.Infrastructure.Orders.ReturnFetchJob>(PazaryeriHub.Infrastructure.Orders.ReturnFetchJob.RecurringJobId(code),
                orderOptions.Value.ReturnFetchCron, job => job.RunAsync(code, true, CancellationToken.None), utc);
            Set<PazaryeriHub.Infrastructure.Channels.ChannelCommandJob>(PazaryeriHub.Infrastructure.Channels.ChannelCommandJob.RecurringJobId(code),
                sync.CommandCron, job => job.RunAsync(code, CancellationToken.None), utc);
        }

        Set<OutboxProcessorJob>(OutboxProcessorJob.RecurringJobId, sync.OutboxCron, job => job.RunAsync(CancellationToken.None), utc);
        Set<BatchCheckJob>(BatchCheckJob.RecurringJobId, sync.BatchCheckCron, job => job.RunAsync(CancellationToken.None), utc);
    }

    /// <summary>Cron boşsa job kaldırılır (testler ve devre dışı bırakma için).</summary>
    private void Set<TJob>(string id, string? cron, System.Linq.Expressions.Expression<Func<TJob, Task>> call, RecurringJobOptions options)
    {
        if (string.IsNullOrWhiteSpace(cron))
        {
            recurringJobs.RemoveIfExists(id);
        }
        else
        {
            recurringJobs.AddOrUpdate(id, call, cron, options);
        }
    }
}
