namespace PazaryeriHub.Application.Stock;

/// <summary>Bir ilanın son mutabakat görüntüsü (kanal stoğunu deftere alma önizlemesi).</summary>
public sealed record IntakeChannelValue(
    int ListingId, string ChannelCode, string? ChannelBarcode, string? ChannelProductCode,
    int? ChannelStock, DateTime? FetchedAtUtc, int Expected, bool SyncPending);

/// <summary>SKU başına önizleme satırı. Hedef satılabilir kullanıcı tarafından düzenlenebilir; Fiziksel uygulamada yeniden hesaplanır.</summary>
/// <param name="SourceValue">Kaynak kanalın gösterdiği değer (çekim anında).</param>
/// <param name="ReservedSinceSnapshot">Çekimden sonra gelen net rezervasyon (sipariş − iptal).</param>
/// <param name="ProposedTarget">Önerilen hedef satılabilir (şimdi) = MAX(0, kanal değeri − çekimden sonraki net rezervasyon).</param>
public sealed record IntakePreviewRow(
    int SkuId, string SkuCode, string ProductName,
    int Physical, int Reserved, int SafetyStock, int Available,
    IReadOnlyList<IntakeChannelValue> Channels,
    int? SourceValue, int? MinValue, int ReservedSinceSnapshot, int? ProposedTarget, int? ProposedPhysical,
    IReadOnlyList<string> Flags, bool DefaultSelected);

/// <param name="PreviewedAtUtc">Önizleme anı (veritabanı saati); uygulamada bu andan sonraki rezervasyonlar hedeften düşülür.</param>
public sealed record IntakePreview(
    int SourceChannelId, string SourceChannelCode, bool Enabled, string? DisabledReason, int LowStockThreshold,
    DateTime PreviewedAtUtc, IReadOnlyList<IntakePreviewRow> Rows);

/// <param name="ExpectedPhysical">Önizlemedeki Fiziksel; uygulamada farklıysa satır reddedilir.</param>
public sealed record IntakeApplyRow(int SkuId, int TargetAvailable, int? ExpectedPhysical);

/// <param name="PreviewedAtUtc">Önizlemenin <see cref="IntakePreview.PreviewedAtUtc"/> değeri.</param>
public sealed record IntakeApplyRequest(int SourceChannelId, DateTime? PreviewedAtUtc, IReadOnlyList<IntakeApplyRow>? Rows);

public static class IntakeOutcomes
{
    public const string Applied = "UYGULANDI";
    public const string Unchanged = "FARK_YOK";
    public const string Rejected = "REDDEDILDI";
}

public sealed record IntakeRowResult(int SkuId, string? SkuCode, string Outcome, string? Message, int? Physical, int? Available, long? MovementId);

public sealed record IntakeApplyResult(
    string BatchId, int Applied, int Unchanged, int Rejected, int PushQueued, int PushSkipped, IReadOnlyList<IntakeRowResult> Rows);

/// <summary>Önizleme işaretleri (satırı varsayılan olarak seçilmez yapar).</summary>
public static class IntakeFlags
{
    public const string NoChannelValue = "KANALDA_DEGER_YOK";
    public const string ListingsDiffer = "ILANLAR_FARKLI";
    public const string ShareNot100 = "PAY_100_DEGIL";
    public const string ThresholdAmbiguous = "ESIK_BELIRSIZ";
    public const string AtCap = "UST_LIMITTE";
    public const string StaleSnapshot = "ESKI_CEKIM";

    /// <summary>SKU'nun kaynak kanaldaki ilanlarından bazıları kanal stok sorgusunda dönmedi (kara liste, arşiv vb.).</summary>
    public const string SomeListingsMissing = "ILAN_DEGERI_YOK";
    public const string LedgerChangedAfter = "DEFTER_SONRADAN_DEGISTI";
    public const string SyncPending = "SENKRON_BEKLIYOR";
    public const string NoDifference = "FARK_YOK";

    /// <summary>Bilgi: çekimden sonra sipariş/iptal var; hedeften düşüldü (seçimi engellemez).</summary>
    public const string ReservationsAfterSnapshot = "CEKIMDEN_SONRA_SIPARIS";

    /// <summary>Seçimi engellemeyen (yalnızca bilgi) işaretler.</summary>
    public static readonly IReadOnlySet<string> Informational = new HashSet<string>(StringComparer.Ordinal) { ReservationsAfterSnapshot };
}

public sealed record IntakeEvaluation(int? Value, int? MinValue, int? Target, IReadOnlyList<string> Flags, bool DefaultSelected);

/// <summary>
/// Kanal stoğunu deftere alma kuralları (saf fonksiyon, ADR-024). Kaynak kanaldaki ilan değerlerinden SKU'nun hedef
/// satılabilir değerini önerir ve tersine çevrilemeyen / güvenilmez durumları işaretler.
/// </summary>
public static class ChannelIntakeRules
{
    public static readonly TimeSpan MaxSnapshotAge = TimeSpan.FromHours(24);

    /// <param name="sourceValues">Kaynak kanaldaki AKTIF ilanların son kanal değerleri (null = kanalda görülmedi).</param>
    /// <param name="oldestSourceSnapshotUtc">Değeri olan kaynak ilanların en eski çekim anı (değeri olmayan ilan ayrı işaretlenir).</param>
    /// <param name="allValues">Tüm kanallardaki değerler (en düşük değer bilgisi için).</param>
    /// <param name="reservedSinceSnapshot">Çekimden sonraki net rezervasyon (sipariş − iptal); hedeften düşülür.</param>
    public static IntakeEvaluation Evaluate(
        IReadOnlyList<int?> sourceValues, IReadOnlyList<int?> allValues, int currentAvailable, int reservedSinceSnapshot,
        decimal sourceSharePercent, int? sourceCap, int lowStockThreshold,
        DateTime? oldestSourceSnapshotUtc, DateTime? lastMovementUtc, bool syncPending, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(sourceValues);
        ArgumentNullException.ThrowIfNull(allValues);
        var flags = new List<string>();
        var known = sourceValues.OfType<int>().ToList();
        int? min = allValues.Any(v => v is not null) ? allValues.OfType<int>().Min() : null;

        if (known.Count == 0)
        {
            return new IntakeEvaluation(null, min, null, [IntakeFlags.NoChannelValue], false);
        }

        // Aynı kanalda birden fazla ilan (çoklu ilan) farklı değer gösteriyorsa en düşüğü (oversell açısından güvenli).
        var value = known.Min();
        if (known.Count < sourceValues.Count)
        {
            flags.Add(IntakeFlags.SomeListingsMissing);
        }
        if (known.Distinct().Count() > 1)
        {
            flags.Add(IntakeFlags.ListingsDiffer);
        }
        if (sourceSharePercent != 100m)
        {
            flags.Add(IntakeFlags.ShareNot100); // kanal değeri FLOOR(satılabilir × pay) → kesin geri hesaplanamaz
        }
        if (value == 0 && lowStockThreshold > 0)
        {
            flags.Add(IntakeFlags.ThresholdAmbiguous); // gerçek satılabilir 0..eşik arası olabilir
        }
        if (sourceCap is { } cap && value >= cap)
        {
            flags.Add(IntakeFlags.AtCap); // gerçek değer üst limitten büyük olabilir
        }
        if (oldestSourceSnapshotUtc is not { } at || at < nowUtc - MaxSnapshotAge)
        {
            flags.Add(IntakeFlags.StaleSnapshot);
        }
        if (lastMovementUtc is { } moved && oldestSourceSnapshotUtc is { } snap && moved > snap)
        {
            flags.Add(IntakeFlags.LedgerChangedAfter);
        }
        if (syncPending)
        {
            flags.Add(IntakeFlags.SyncPending); // kanal değeri değişmek üzere
        }
        // Çekimden sonra gelen siparişler kanal değerinde yok: gerçek satılabilir = kanal değeri − sonraki net rezervasyon.
        // (Sipariş çekimden önce verilip sonra kaydedildiyse iki kez düşülür → daha düşük stok, oversell açısından güvenli.)
        var target = Math.Max(0, value - reservedSinceSnapshot);
        if (reservedSinceSnapshot != 0)
        {
            flags.Add(IntakeFlags.ReservationsAfterSnapshot);
        }
        if (target == currentAvailable)
        {
            flags.Add(IntakeFlags.NoDifference);
        }

        return new IntakeEvaluation(value, min, target, flags, flags.All(IntakeFlags.Informational.Contains));
    }

    /// <summary>Önerilen Fiziksel = hedef satılabilir (şimdi) + Rezerve + Güvenlik stoğu (uygulamada kilit altında yeniden hesaplanır).</summary>
    public static int? ProposedPhysical(int? target, int reserved, int safetyStock) =>
        target is { } t ? (int)Math.Min(int.MaxValue, (long)t + reserved + safetyStock) : null;
}

public interface IChannelIntakeService
{
    /// <summary>Önizleme: hiçbir şey yazmaz. Kaynak kanalda AKTIF ilanı olan SKU'lar.</summary>
    Task<IntakePreview> PreviewAsync(int sourceChannelId, bool onlyDifferences, CancellationToken ct);

    /// <summary>Onaylanan satırları uygular (her satır kendi transaction'ında); ardından SKU'ların tüm aktif ilanları zorla gönderilir.</summary>
    Task<IntakeApplyResult> ApplyAsync(IntakeApplyRequest request, string? user, CancellationToken ct);
}
