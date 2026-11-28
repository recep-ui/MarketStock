using PazaryeriHub.Application.Common;

namespace PazaryeriHub.Application.Channels;

/// <summary>
/// Kanala yazma yetenekleri (ADR-026). Varsayılan KAPALI; STOK varsayılanı DENEME ("Onaylı", ADR-048): stok değişiklikleri
/// Yönetici onayı olmadan kanala gitmez.
/// </summary>
public static class ChannelCapabilities
{
    public const string Stock = "STOK";
    public const string OrderStatus = "SIPARIS_DURUM";
    public const string ReturnDecision = "IADE_KARAR";
    public const string Price = "FIYAT";
    public const string QuestionAnswer = "SORU_CEVAP";

    public static IReadOnlyList<string> All { get; } = [Stock, OrderStatus, ReturnDecision, Price, QuestionAnswer];

    public static string Default(string capability) => capability == Stock ? CapabilityStates.Trial : CapabilityStates.Closed;
}

public static class CapabilityStates
{
    /// <summary>Kanala hiçbir şey gitmez.</summary>
    public const string Closed = "KAPALI";

    /// <summary>Her kayıt Yönetici onayıyla tek tek gider (ilk gerçek denemeler için).</summary>
    public const string Trial = "DENEME";

    public const string Open = "ACIK";

    public static IReadOnlyList<string> All { get; } = [Closed, Trial, Open];
}

/// <summary>"Kanallar" bölümü: global yazma kilidi. Deneme modu bunu true yapar; true iken gerçek kanala HİÇBİR yazma gitmez.</summary>
public sealed class ChannelWriteOptions
{
    public const string SectionName = "Kanallar";

    public bool YazmaKapali { get; set; }
}

/// <summary>Kanala yazma engellendi (global kilit, pasif kanal veya kapalı yetenek). Kayıt hata sayılmaz; bekler.</summary>
public sealed class ChannelWriteBlockedException(string channelCode, string capability, string reason)
    : Exception($"{channelCode} kanalına yazma engellendi ({capability}): {reason}")
{
    public string ChannelCode { get; } = channelCode;
    public string Capability { get; } = capability;
    public string Reason { get; } = reason;
}

/// <summary>Gerçek kanala yazma çağrısından hemen önce son kontrol (GuardedChannelAdapter).</summary>
public interface IChannelWriteGate
{
    /// <summary>Global kilit kapalı, kanal aktif ve yetenek ACIK/DENEME değilse <see cref="ChannelWriteBlockedException"/> fırlatır.</summary>
    Task EnsureAllowedAsync(string channelCode, string capability, CancellationToken ct);

    Task<string> GetStateAsync(int channelId, string capability, CancellationToken ct);
}

/// <param name="AllowedStates">Panelde seçilebilecek durumlar (ör. otomatik stok gönderimi kapalıyken STOK için ACIK yok, ADR-067).</param>
public sealed record ChannelCapabilityDto(string Capability, string State, string? ChangedBy, DateTime ChangedAtUtc, IReadOnlyList<string> AllowedStates);

public sealed record ChannelCapabilityUpdate(string State);

public interface IChannelCapabilityService
{
    Task<IReadOnlyList<ChannelCapabilityDto>> GetAsync(int channelId, CancellationToken ct);
    Task<ChannelCapabilityDto> SetAsync(int channelId, string capability, string state, string? user, CancellationToken ct);
}

// ------------------------------------------------------------------ Kanal komut kuyruğu

public static class ChannelCommandTypes
{
    public const string OrderStatus = "SIPARIS_DURUM";
    public const string ReturnDecision = "IADE_KARAR";
    public const string QuestionAnswer = "SORU_CEVAP";

    /// <summary>Komut tipinin bağlı olduğu yetenek.</summary>
    public static string CapabilityOf(string type) => type switch
    {
        OrderStatus => ChannelCapabilities.OrderStatus,
        ReturnDecision => ChannelCapabilities.ReturnDecision,
        QuestionAnswer => ChannelCapabilities.QuestionAnswer,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };
}

public static class ChannelCommandStates
{
    public const string AwaitingApproval = "ONAY_BEKLIYOR";
    public const string Pending = "BEKLIYOR";
    public const string Sent = "GONDERILDI";
    public const string Failed = "HATA";
    public const string Cancelled = "IPTAL";
}

/// <summary>Yeni komut. Durum, yetenek DENEME ise ONAY_BEKLIYOR, ACIK ise BEKLIYOR olur; KAPALI ise oluşturulmaz.</summary>
public sealed record NewChannelCommand(int ChannelId, string Type, string TargetKey, string PayloadJson, string IdempotencyKey, string? CreatedBy);

/// <param name="Outcome">OLUSTURULDU | ZATEN_VAR</param>
public sealed record ChannelCommandCreateResult(long Id, string Outcome, string State);

public sealed record ChannelCommandDto(
    long Id, int ChannelId, string ChannelCode, string Type, string TargetKey, string PayloadJson, string State, int Attempts,
    DateTime? NextAttemptUtc, string? Error, string? ChannelOperationNo, string? CreatedBy, DateTime CreatedAtUtc,
    string? ApprovedBy, DateTime? ApprovedAtUtc, DateTime? SentAtUtc)
{
    /// <summary>Hatanın türü ve ne yapılacağı (ADR-033).</summary>
    public ChannelErrorInfo? ErrorInfo => ChannelErrorExplainer.Explain(Error);
}

public sealed record ChannelCommandQuery(int? ChannelId, string? Type, string? State, string? TargetKey, int? Page, int? PageSize);

public interface IChannelCommandService
{
    Task<PagedResult<ChannelCommandDto>> SearchAsync(ChannelCommandQuery query, CancellationToken ct);

    /// <summary>ONAY_BEKLIYOR → BEKLIYOR (Yönetici; DENEME yeteneğinde tek kaydın gönderilmesine izin verir).</summary>
    Task<ChannelCommandDto> ApproveAsync(long id, string? user, CancellationToken ct);

    /// <summary>HATA → BEKLIYOR (deneme sayacı sıfırlanır).</summary>
    Task<ChannelCommandDto> RetryAsync(long id, string? user, CancellationToken ct);

    /// <summary>ONAY_BEKLIYOR | BEKLIYOR | HATA → IPTAL.</summary>
    Task<ChannelCommandDto> CancelAsync(long id, string? user, CancellationToken ct);
}

/// <summary>Komut tipine özgü gönderim (KanalKomutGonder job'ı tipine göre çağırır).</summary>
public interface IChannelCommandHandler
{
    string Type { get; }

    /// <returns>Kanalın işlem numarası (varsa).</returns>
    Task<string?> ExecuteAsync(IChannelAdapter adapter, string payloadJson, CancellationToken ct);
}
