using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PazaryeriHub.Infrastructure.BackgroundJobs;

/// <summary>
/// Geçici ölçüm job'ı (Aşama 0). Ardışık çalışmalar arasındaki süreyi loglar; böylece Hangfire'ın
/// saniye çözünürlüklü tekrarlayan job'ları ne kadar isabetli çalıştırdığı gözlenir. Aşama 4'te kaldırılacak.
/// </summary>
public sealed class HeartbeatJob(ILogger<HeartbeatJob> logger)
{
    public const string RecurringJobId = "asama0-heartbeat";

    private static long _lastRunTimestamp;

    public Task RunAsync()
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Exchange(ref _lastRunTimestamp, now);
        var elapsed = previous == 0 ? (double?)null : Stopwatch.GetElapsedTime(previous, now).TotalSeconds;

        logger.LogInformation("Hangfire heartbeat çalıştı. Önceki çalışmadan bu yana geçen süre: {GecenSaniye} sn", elapsed);
        return Task.CompletedTask;
    }
}
