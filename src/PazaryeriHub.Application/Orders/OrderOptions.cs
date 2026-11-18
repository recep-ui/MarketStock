namespace PazaryeriHub.Application.Orders;

/// <summary>Sipariş ayarları ("Siparis" bölümü).</summary>
public sealed class OrderOptions
{
    public const string SectionName = "Siparis";

    /// <summary>SiparisCek tekrarlayan job'ının cron ifadesi (UTC). Boşsa job kaydedilmez.</summary>
    public string? FetchCron { get; set; } = "*/2 * * * *";

    /// <summary>IadeCek (kanal başına; ADR-027). Boşsa job kaydedilmez.</summary>
    public string? ReturnFetchCron { get; set; } = "*/15 * * * *";

    /// <summary>Örtüşmeli pencere: son başarılı çekmeden bu kadar dakika geriden başlanır (geç düşen siparişler kaçmasın).</summary>
    public int OverlapMinutes { get; set; } = 10;

    /// <summary>Kanal için hiç başarılı çekme yoksa kaç saat geriye bakılır.</summary>
    public int InitialLookbackHours { get; set; } = 24;

    /// <summary>
    /// Sipariş rezervasyonlarının yapılacağı depo. Çoklu depo stratejisi henüz netleşmedi (açık soru);
    /// şimdilik tüm sipariş rezervasyonları bu depodan yapılır.
    /// </summary>
    public string DefaultWarehouseName { get; set; } = "Ana Depo";
}

/// <summary>Kanal kodları (Channel.Kod CHECK kısıtıyla aynı).</summary>
public static class ChannelCodes
{
    public const string Trendyol = "TRENDYOL";
    public const string Hepsiburada = "HEPSIBURADA";
    public const string Beymen = "BEYMEN";
    public const string Softcotton = "SOFTCOTTON";

    public static readonly string[] All = [Trendyol, Hepsiburada, Beymen, Softcotton];
}
