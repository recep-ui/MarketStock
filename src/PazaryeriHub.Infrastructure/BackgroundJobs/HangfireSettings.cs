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
    /// Bu sunucunun işleyeceği kuyruklar. Boşsa Hangfire varsayılanı ("default").
    /// Testlerde arka plan sunucusunun gerçek job'ları almaması için farklı bir kuyruk verilir.
    /// </summary>
    public string[]? Queues { get; set; }
}
