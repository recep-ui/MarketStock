namespace PazaryeriHub.Application.Channels;

/// <summary>
/// Hata türleri (ADR-033). Kanal hataları zaten adaptörlerde sadeleştirilmiş Türkçe metin olarak saklanır; bu sınıflandırma
/// metni değiştirmez, panelde "ne oldu + ne yapmalı" olarak gösterilmesini sağlar.
/// </summary>
public static class ChannelErrorKinds
{
    public const string Credentials = "KIMLIK";
    public const string AccessDenied = "ERISIM_REDDI";
    public const string RateLimit = "HIZ_LIMITI";
    public const string Unreachable = "KANAL_ERISILEMIYOR";
    public const string WriteLocked = "YAZMA_KILITLI";
    public const string NotSupported = "DESTEKLENMIYOR";
    public const string Duplicate = "TEKRAR_ENGELI";
    public const string InvalidData = "GECERSIZ_VERI";
    public const string Unknown = "BILINMEYEN";
}

/// <summary>Kullanıcıya gösterilecek hata açıklaması.</summary>
/// <param name="Kind">Hata türü (<see cref="ChannelErrorKinds"/>).</param>
/// <param name="Title">Kısa başlık: ne oldu.</param>
/// <param name="Advice">Ne yapmalı.</param>
/// <param name="Transient">Geçici: sistem kendisi yeniden dener, kullanıcının bir şey yapması gerekmez.</param>
/// <param name="Problems">Kayıttaki ayrı sorunlar (çekim işleri " | " ile birleştirir); en fazla 3.</param>
/// <param name="MoreCount">Gösterilmeyen sorun sayısı.</param>
public sealed record ChannelErrorInfo(string Kind, string Title, string Advice, bool Transient, IReadOnlyList<string> Problems, int MoreCount);

/// <summary>Saf fonksiyon: kayıtlı hata metnini türüne ayırır (birim testli). Bilinmeyen metin için güvenli varsayılan döner.</summary>
public static class ChannelErrorExplainer
{
    private const int MaxProblems = 3;

    public static ChannelErrorInfo? Explain(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;

        var parts = error.Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        // Çekim işleri 10'dan fazlasını "(+N)" ile özetler: son parçadaki eki sayıya ekle
        var extra = 0;
        if (parts.Count > 0)
        {
            var last = parts[^1];
            var idx = last.LastIndexOf(" (+", StringComparison.Ordinal);
            if (idx > 0 && last.EndsWith(')') && int.TryParse(last[(idx + 3)..^1], out var n))
            {
                extra = n;
                parts[^1] = last[..idx];
            }
        }

        var (kind, title, advice, transient) = Classify(error);
        return new ChannelErrorInfo(kind, title, advice, transient, parts.Take(MaxProblems).ToList(), Math.Max(0, parts.Count - MaxProblems) + extra);
    }

    private static (string Kind, string Title, string Advice, bool Transient) Classify(string text)
    {
        bool Has(string s) => text.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (Has("API bilgileri hatalı") || Has("API bilgileri girilmemiş") || Has("API bilgileri okunamadı") || Has("HTTP 401"))
            return (ChannelErrorKinds.Credentials, "API bilgisi eksik veya kabul edilmedi",
                "Ayarlar → Kanallar → API bilgisi'nden bilgileri kontrol edip yeniden girin.", false);
        if (Has("yazma engellendi") || Has("yazma kilidi"))
            return (ChannelErrorKinds.WriteLocked, "Kanala yazma kapalı",
                "Deneme modunda veya yetenek kapalıyken kanala yazılmaz. Gerekirse Ayarlar → Kanala yazma yetenekleri'ni kontrol edin.", false);
        if (Has("istek sınırı") || Has("HTTP 429"))
            return (ChannelErrorKinds.RateLimit, "Kanalın istek sınırı aşıldı",
                "Bir şey yapmanız gerekmez; sistem biraz sonra kendisi yeniden dener.", true);
        if (Has("15 dakika"))
            return (ChannelErrorKinds.Duplicate, "Aynı istek kısa sürede tekrar gönderilemez",
                "Bir şey yapmanız gerekmez; değer değişince yeniden gönderilir.", true);
        if (Has("HTTP 403") || Has("reddetti (erişim") || Has("beyaz liste"))
            return (ChannelErrorKinds.AccessDenied, "Kanal erişimi reddetti",
                "Pazaryeri panelinde API yetkilerini, IP izinlerini ve User-Agent bilgisini kontrol edin.", false);
        if (Has("zamanında yanıt vermedi") || Has("ulaşılamadı") || Has("erişilemedi") || Has("geçici") || Has("HTTP 5"))
            return (ChannelErrorKinds.Unreachable, "Kanala şu an ulaşılamıyor",
                "Kanal tarafında geçici bir sorun var; sistem yeniden dener. Uzun sürerse \"Yeniden dene\"yi kullanın.", true);
        if (Has("desteklenmiyor"))
            return (ChannelErrorKinds.NotSupported, "Bu işlem kanalda desteklenmiyor",
                "Bu işlemi pazaryerinin kendi panelinden yapın.", false);
        if (Has("reddetti") || Has("geçersiz") || Has("bulunamadı") || Has("HTTP 400") || Has("HTTP 404") || Has("HTTP 409") || Has("HTTP 422"))
            return (ChannelErrorKinds.InvalidData, "Kanal kaydı kabul etmedi",
                "Ürün eşleştirmesini, barkodu ve değerleri kontrol edin; düzelttikten sonra \"Yeniden dene\"yi kullanın.", false);
        return (ChannelErrorKinds.Unknown, "İşlem tamamlanamadı",
            "Ayrıntıyı inceleyin; sorun sürerse \"Yeniden dene\"yi kullanın.", false);
    }
}

/// <summary>Kanal durum özeti (ADR-033).</summary>
public static class ChannelHealthStates
{
    public const string Working = "CALISIYOR";
    public const string Warning = "DIKKAT";
    public const string Error = "HATA";
    public const string Mock = "SAHTE";
    public const string Off = "KAPALI";
}

/// <param name="JobType">SIPARIS_CEK | IADE_CEK | SORU_CEK | KATALOG_CEK | MUTABAKAT | REFERANS_CEK</param>
public sealed record ChannelJobStatus(string JobType, DateTime? LastSuccessUtc, DateTime? LastAttemptUtc, int ConsecutiveFailures, string? LastError)
{
    public ChannelErrorInfo? ErrorInfo => ConsecutiveFailures > 0 ? ChannelErrorExplainer.Explain(LastError) : null;
}

/// <summary>Panel "Kanal durumu" kartı. Gizli bilgi içermez: yalnızca API bilgisinin var olup olmadığı ve seçilen ortam.</summary>
/// <param name="Mode">MOCK | GERCEK (sunucu yapılandırması "Kanallar:{KOD}:Mod").</param>
/// <param name="Environment">API bilgisinde seçilen ortam (Prod | Stage | Test); girilmemişse null.</param>
/// <param name="WriteLocked">Global yazma kilidi (deneme modu): gerçek kanala hiçbir yazma gitmez.</param>
public sealed record ChannelStatusDto(
    int Id, string Code, string Name, bool Active, bool HasCredentials, string Mode, bool RealAdapterAvailable, string? Environment,
    bool WriteLocked, IReadOnlyList<ChannelCapabilityDto> Capabilities, IReadOnlyList<ChannelJobStatus> Jobs, string Health, string HealthReason);

/// <summary>Saf fonksiyon: kanalın genel durumunu ve tek cümlelik nedenini hesaplar (birim testli).</summary>
public static class ChannelHealth
{
    public static (string Health, string Reason) Evaluate(bool active, string mode, bool realAdapterAvailable, bool hasCredentials, IReadOnlyList<ChannelJobStatus> jobs)
    {
        if (!active) return (ChannelHealthStates.Off, "Kanal pasif: çekim ve gönderim yapılmaz.");
        if (mode != "GERCEK")
        {
            return (ChannelHealthStates.Mock, realAdapterAvailable
                ? "Sahte (mock) modda: gerçek pazaryerine bağlanmıyor, veriler örnektir."
                : "Gerçek entegrasyon henüz yok; sahte (mock) veriyle çalışıyor.");
        }
        if (!hasCredentials) return (ChannelHealthStates.Error, "API bilgisi girilmemiş: pazaryerine bağlanılamaz.");

        var failing = jobs.Where(j => j.ConsecutiveFailures > 0).OrderByDescending(j => j.LastAttemptUtc).ToList();
        if (failing.Count > 0)
        {
            var info = failing[0].ErrorInfo;
            var reason = $"{JobLabel(failing[0].JobType)}: {info?.Title ?? "başarısız"}";
            return failing.All(j => j.ErrorInfo?.Transient == true) ? (ChannelHealthStates.Warning, reason) : (ChannelHealthStates.Error, reason);
        }
        if (jobs.All(j => j.LastSuccessUtc is null)) return (ChannelHealthStates.Warning, "Bağlantı kuruldu ancak henüz hiç veri çekilmedi.");
        return (ChannelHealthStates.Working, "Son çekimler başarılı.");
    }

    public static string JobLabel(string jobType) => jobType switch
    {
        "SIPARIS_CEK" => "Sipariş çekme",
        "IADE_CEK" => "İade çekme",
        "SORU_CEK" => "Soru çekme",
        "KATALOG_CEK" => "Ürün çekme",
        "MUTABAKAT" => "Stok mutabakatı",
        "REFERANS_CEK" => "Satıcı bilgileri",
        _ => jobType
    };
}
