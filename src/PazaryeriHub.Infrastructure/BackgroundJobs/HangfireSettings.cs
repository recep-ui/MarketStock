namespace PazaryeriHub.Infrastructure.BackgroundJobs;

/// <summary>Hangfire ayarları ("Hangfire" bölümü).</summary>
public sealed class HangfireSettings
{
    public const string SectionName = "Hangfire";

    /// <summary>Hangfire tablolarının şeması (iş tablolarından ayrı tutulur).</summary>
    public string SchemaName { get; set; } = "hangfire";

    /// <summary>Bu süreçte iş işleyen worker sayısı. 0 ise Hangfire varsayılanı kullanılır.</summary>
    public int WorkerCount { get; set; }

    /// <summary>Zamanlanmış / tekrarlayan işlerin yoklanma aralığı (saniye).</summary>
    public int SchedulePollingIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Aşama 0 ölçüm job'ının cron ifadesi (6 alanlı = saniye çözünürlüklü). Boş ise job kaldırılır.
    /// Amaç: 10 sn'lik OutboxIsle aralığının Hangfire ile karşılanıp karşılanamadığını ölçmek.
    /// </summary>
    public string? HeartbeatCron { get; set; }
}
