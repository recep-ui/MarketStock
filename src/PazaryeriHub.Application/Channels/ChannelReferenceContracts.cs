namespace PazaryeriHub.Application.Channels;

/// <summary>
/// Satıcı Bilgileri: kanal referans verisi (ADR-025). Yalnızca diğer modüllerin gerçekten ihtiyaç duyduğu tipler:
/// kargo firmaları (Teslimat bildirimi, kendi kargo ile gönderim) ve iade red gerekçeleri (İade kararı).
/// </summary>
public enum ChannelReferenceType
{
    CargoProvider,
    ClaimRejectReason
}

/// <summary>Referans listesinin kaynağı: kanalın servisi veya kanal dokümanındaki sabit liste.</summary>
public enum ReferenceSource
{
    Api,
    Documentation
}

/// <param name="Code">Kanalın kimliği (kanala bu değer gönderilir).</param>
/// <param name="Name">Görünen ad.</param>
/// <param name="DataJson">Kanala özgü ek bilgi (JSON; ör. Beymen red gerekçesinin sayısal değeri ve görsel zorunluluğu).</param>
public sealed record ChannelReferenceItem(string Code, string Name, string? DataJson = null);

/// <param name="Note">Kaynak açıklaması (dokümandaki sabit listeler için doküman adresi ve okuma tarihi).</param>
public sealed record ChannelReferenceList(IReadOnlyList<ChannelReferenceItem> Items, ReferenceSource Source, string? Note = null);

public static class ChannelReferenceTypes
{
    public const string CargoProvider = "KARGO_FIRMASI";
    public const string ClaimRejectReason = "IADE_RED_GEREKCE";

    public static IReadOnlyList<ChannelReferenceType> All { get; } = [ChannelReferenceType.CargoProvider, ChannelReferenceType.ClaimRejectReason];

    public static string ToDb(ChannelReferenceType type) => type switch
    {
        ChannelReferenceType.CargoProvider => CargoProvider,
        ChannelReferenceType.ClaimRejectReason => ClaimRejectReason,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    public static bool TryParse(string? value, out ChannelReferenceType type)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case CargoProvider: type = ChannelReferenceType.CargoProvider; return true;
            case ClaimRejectReason: type = ChannelReferenceType.ClaimRejectReason; return true;
            default: type = default; return false;
        }
    }

    public static string SourceToDb(ReferenceSource source) => source == ReferenceSource.Api ? "API" : "DOKUMAN";
}

/// <summary>Panel/API için referans kaydı.</summary>
public sealed record ChannelReferenceDto(
    int Id, string Type, string Code, string Name, string? DataJson, string Source, DateTime FetchedAtUtc, bool Active);

/// <summary>Tip başına son çekme sonucu (ChannelJobState.SonSonuc).</summary>
/// <param name="Outcome">GUNCELLENDI | DESTEKLENMIYOR | HATA</param>
public sealed record ChannelReferenceTypeResult(string Type, string Outcome, int Count, int Deactivated, string? Source, string? Message);

public sealed record ChannelReferenceStatus(
    int ChannelId, DateTime? LastAttemptUtc, DateTime? LastSuccessUtc, string? LastError, IReadOnlyList<ChannelReferenceTypeResult> Results);

public sealed record ChannelReferenceView(IReadOnlyList<ChannelReferenceDto> Items, ChannelReferenceStatus Status);

public interface IChannelReferenceService
{
    /// <param name="type">Null → tüm tipler.</param>
    Task<ChannelReferenceView> GetAsync(int channelId, ChannelReferenceType? type, bool includeInactive, CancellationToken ct);
}
