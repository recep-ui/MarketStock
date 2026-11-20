namespace PazaryeriHub.Application.Stock;

/// <summary>
/// İlan başına stok kuralı girdisi (ADR-042). Type: "UST_LIMIT" | "SABIT" | null (kural kaldırılır). Value: 0 veya pozitif adet.
/// </summary>
public sealed record ListingStockRuleInput(string? Type, int? Value);

/// <summary>İlanın stok kuralı ve kurala göre kanala gidecek güncel hedef.</summary>
public sealed record ListingStockRuleDto(
    int ListingId, string? Type, int? Value, int Target, int? LastSent, string? ChangedBy, DateTime? ChangedAtUtc);

/// <summary>
/// Platform bazlı farklı stok. Kural defteri değiştirmez; yalnızca o ilana gidecek değeri belirler ve defteri asla aşmaz.
/// Kanala çağrı YAPMAZ: değişiklik outbox (EslestirmeDegisti) → STOK kuyruğu → StokGonder hattıyla, yazma kilidine tabi gider.
/// </summary>
public interface IListingStockRuleService
{
    Task<ListingStockRuleDto> GetAsync(int listingId, CancellationToken ct);
    Task<ListingStockRuleDto> SetAsync(int listingId, ListingStockRuleInput input, string? user, CancellationToken ct);
}
