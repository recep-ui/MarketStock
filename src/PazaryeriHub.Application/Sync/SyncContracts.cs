using PazaryeriHub.Application.Common;

namespace PazaryeriHub.Application.Sync;

/// <summary>Senkron hattı ayarları ("Senkron" bölümü).</summary>
public sealed class SyncOptions
{
    public const string SectionName = "Senkron";

    /// <summary>OutboxIsle (6 alanlı cron = saniye çözünürlüklü). Boşsa job kaydedilmez.</summary>
    public string? OutboxCron { get; set; } = "*/10 * * * * *";

    /// <summary>StokGonder (kanal başına).</summary>
    public string? PushCron { get; set; } = "*/10 * * * * *";

    /// <summary>BatchSorgula.</summary>
    public string? BatchCheckCron { get; set; } = "*/10 * * * * *";

    /// <summary>Mutabakat (kanal başına). Varsayılan 00:00 UTC = 03:00 Türkiye.</summary>
    public string? ReconciliationCron { get; set; } = "0 0 * * *";

    /// <summary>ReferansCek (kanal başına; Satıcı Bilgileri, ADR-025). Varsayılan haftalık, Pazartesi 01:00 UTC.</summary>
    public string? ReferenceCron { get; set; } = "0 1 * * 1";

    /// <summary>SoruCek (kanal başına; ADR-029). Kanallar 1–2 iş günü cevap süresi verdiği için 5 dakikada bir.</summary>
    public string? QuestionFetchCron { get; set; } = "*/5 * * * *";

    /// <summary>FiyatGonder (kanal başına; ADR-028). FIYAT yeteneği KAPALI kanalda hiçbir şey talep etmez.</summary>
    public string? PricePushCron { get; set; } = "*/10 * * * * *";

    /// <summary>KanalKomutGonder (kanal başına; sipariş durumu, iade kararı, soru cevabı; ADR-026).</summary>
    public string? CommandCron { get; set; } = "*/30 * * * * *";

    /// <summary>Bu kadar başarısız denemeden sonra kayıt HATA olur (ilke 5).</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Üstel geri çekilmenin taban süresi: n. başarısızlıktan sonra Base × 2^(n-1) beklenir.</summary>
    public int BaseBackoffSeconds { get; set; } = 30;

    public int MaxBackoffMinutes { get; set; } = 30;

    /// <summary>Tek toplu gönderimdeki en fazla kalem.</summary>
    public int PushChunkSize { get; set; } = 100;

    /// <summary>Bir OutboxIsle çalışmasında en fazla kaç parti (×100 olay) işlenir.</summary>
    public int OutboxMaxBatchesPerRun { get; set; } = 50;

    /// <summary>Kanal bu süre içinde sonuç vermezse toplu işlem başarısız sayılır.</summary>
    public int BatchTimeoutMinutes { get; set; } = 30;

    /// <summary>Talep edilip gönderilemeden kalan (süreç çöktü vb.) kayıtlar bu süreden sonra serbest bırakılır.</summary>
    public int ClaimTimeoutMinutes { get; set; } = 10;
}

/// <summary>Yeniden deneme politikası (saf).</summary>
public static class RetryPolicy
{
    /// <summary><paramref name="failedAttempts"/>. başarısızlıktan sonra beklenecek süre; null ise artık denenmez (HATA).</summary>
    public static TimeSpan? NextDelay(int failedAttempts, SyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (failedAttempts >= options.MaxAttempts)
        {
            return null;
        }
        var seconds = options.BaseBackoffSeconds * Math.Pow(2, Math.Max(0, failedAttempts - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, options.MaxBackoffMinutes * 60.0));
    }
}

public static class SyncStatus
{
    public const string Pending = "BEKLIYOR";
    public const string Sent = "GONDERILDI";
    public const string Error = "HATA";
}

public static class OutboxEvents
{
    /// <summary>Bakiye değişti (payload: SkuId, ...). Saklı yordamlar yazar.</summary>
    public const string StockChanged = "StokDegisti";

    /// <summary>Eşleştirme eklendi/değişti (payload: ListingId).</summary>
    public const string ListingChanged = "EslestirmeDegisti";

    /// <summary>Kanal payı/aktifliği/ayarı değişti; kanalın tüm eşleştirmeleri yeniden hesaplanır (payload: ChannelId).</summary>
    public const string ChannelRefresh = "KanalStokYenile";

    /// <summary>
    /// Genel düşük stok eşiği değişti (payload: {}). Tüm aktif ilanlar yeniden hesaplanır; yalnızca hedefi son gönderilenden
    /// farklı olanlar kuyruğa girer (aynı değer yeniden gönderilmez).
    /// </summary>
    public const string LowStockThresholdChanged = "StokEsigiDegisti";

    /// <summary>
    /// Fiyat değişti (ADR-028; payload: SkuId | ListingId | ChannelId). SKU fiyatı, ilan fiyatı, kanal kuralı, eşik, onay veya
    /// FIYAT yeteneğinin açılması yazar. OutboxIsle yalnızca FIYAT yeteneği KAPALI olmayan kanallar için değerlendirir.
    /// </summary>
    public const string PriceChanged = "FiyatDegisti";
}

/// <summary>AppSetting anahtarları (senkronu etkileyenler).</summary>
public static class StockSettingKeys
{
    /// <summary>Genel düşük stok eşiği (ADR-023): satılabilir &lt;= değer ise kanallara 0 gönderilir. Varsayılan 0.</summary>
    public const string LowStockThreshold = "DusukStokEsigi";
}

/// <param name="SentPrice">Tip FIYAT: gönderilen satış fiyatı.</param>
public sealed record SyncQueueItem(
    long Id, int ListingId, string SkuCode, string ChannelCode, string Type, string Status, int Attempts,
    string? Error, string? BatchId, int? SentValue, DateTime CreatedUtc, DateTime? LastAttemptUtc, DateTime? NextAttemptUtc,
    decimal? SentPrice = null)
{
    /// <summary>Hatanın türü ve ne yapılacağı (ADR-033).</summary>
    public PazaryeriHub.Application.Channels.ChannelErrorInfo? ErrorInfo => PazaryeriHub.Application.Channels.ChannelErrorExplainer.Explain(Error);
}

public sealed record SyncSummaryRow(string ChannelCode, int Pending, int InFlight, int Sent, int Error, DateTime? LastSentUtc);

/// <summary>Mutabakat satırı: kanalda görünen stok ile olması gereken stok.</summary>
public sealed record ReconciliationRow(
    int ListingId, string SkuCode, string ChannelCode, string? ChannelProductCode, string? ChannelBarcode,
    int Expected, int? LastSent, int? ChannelStock, DateTime? FetchedAtUtc)
{
    /// <summary>Kanal - beklenen (hiç çekilmediyse veya son çekimde kanalda bulunamadıysa null).</summary>
    public int? Deviation => ChannelStock - Expected;

    /// <summary>Son mutabakatta kanalda bulunamadı (çekim var, değer yok; ADR-040).</summary>
    public bool MissingInChannel => ChannelStock is null && FetchedAtUtc is not null;
}

public sealed record ReconciliationResult(string ChannelCode, bool Skipped, string? SkipReason, int Listings, int Matched, int Missing, int Deviations);

public sealed record PushSkipped(int ListingId, string Reason);

public sealed record PushListingsResult(int Queued, IReadOnlyList<PushSkipped> Skipped);

public sealed record PushListingsRequest(IReadOnlyList<int> ListingIds);

/// <summary>Aynı istek bu süre içinde tekrar gönderilemez (Trendyol ve Beymen dokümanı; reddedilme yanıtı doğrulanmadı).</summary>
public static class ChannelResendRules
{
    public static readonly TimeSpan SameRequestWindow = TimeSpan.FromMinutes(15);
    public static readonly IReadOnlySet<string> ChannelsWithWindow = new HashSet<string>(StringComparer.Ordinal) { "TRENDYOL", "BEYMEN" };
}

public interface ISyncService
{
    Task<PagedResult<SyncQueueItem>> SearchQueueAsync(int? channelId, string? status, int? page, int? pageSize, CancellationToken ct);
    Task<IReadOnlyList<SyncSummaryRow>> GetSummaryAsync(CancellationToken ct);

    /// <summary>HATA durumundaki kaydı yeniden kuyruğa alır (deneme sayacı sıfırlanır).</summary>
    Task<SyncQueueItem> RetryAsync(long id, CancellationToken ct);

    /// <summary>Kanalın tüm eşleştirmeleri için yeniden hesaplama ister (outbox'a KanalStokYenile yazar).</summary>
    Task RequestChannelRefreshAsync(int channelId, CancellationToken ct);

    /// <summary>Son mutabakat görüntüsü ve sapmalar.</summary>
    Task<IReadOnlyList<ReconciliationRow>> GetReconciliationAsync(int? channelId, bool onlyDeviations, CancellationToken ct);

    /// <summary>
    /// "Sapmayı kanala yaz" (ADR-024): seçilen ilanlar için defterdeki değer, hedef son gönderilenle aynı olsa bile mevcut
    /// senkron hattından (outbox EslestirmeDegisti → SyncQueue) yeniden gönderilir. Gönderilemeyen ilanlar gerekçesiyle atlanır.
    /// </summary>
    Task<PushListingsResult> PushListingsAsync(IReadOnlyCollection<int> listingIds, CancellationToken ct);
}
