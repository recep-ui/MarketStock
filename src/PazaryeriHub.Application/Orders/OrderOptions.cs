namespace PazaryeriHub.Application.Orders;

/// <summary>Sipariş ayarları ("Siparis" bölümü).</summary>
public sealed class OrderOptions
{
    public const string SectionName = "Siparis";

    /// <summary>
    /// SiparisCek tekrarlayan job'ının varsayılan cron ifadesi (UTC). Boşsa HİÇBİR kanalda job kaydedilmez (testler, deneme modu):
    /// kanal başına ayar (<see cref="ChannelFetchCron"/>) bunu ezmez.
    /// </summary>
    public string? FetchCron { get; set; } = "*/2 * * * *";

    /// <summary>
    /// Kanal başına SiparisCek aralığı (ADR-030; webhook yerine kısa polling). Hız limitleri resmi dokümandan:
    /// Trendyol "Get Shipment Packages" en düşük kademede 30 istek/dk (çalışma başına ~1 istek); Hepsiburada sipariş servisleri
    /// 1000 istek/sn (çalışma başına ~5 istek). Beymen'de hız limiti dokümanda yok → varsayılan (2 dk) kalır.
    /// </summary>
    public Dictionary<string, string?> ChannelFetchCron { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TRENDYOL"] = "* * * * *",
        ["HEPSIBURADA"] = "* * * * *"
    };

    /// <summary>Kanalın SiparisCek cron'u: genel ayar boşsa null (kapalı); kanal ayarı varsa o, yoksa genel ayar.</summary>
    public string? FetchCronFor(string channelCode) =>
        string.IsNullOrWhiteSpace(FetchCron) ? null
        : ChannelFetchCron.TryGetValue(channelCode, out var cron) && !string.IsNullOrWhiteSpace(cron) ? cron.Trim()
        : FetchCron;

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
