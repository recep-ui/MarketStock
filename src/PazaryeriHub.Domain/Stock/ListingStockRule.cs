namespace PazaryeriHub.Domain.Stock;

/// <summary>İlan başına stok kuralı türü (ADR-042). Veritabanında ListingStockRule.Tip: UST_LIMIT | SABIT.</summary>
public enum ListingStockRuleType
{
    /// <summary>Kanala giden stok en fazla X olur (pay ve kanal limitiyle hesaplanandan küçükse o kalır).</summary>
    UpperLimit,

    /// <summary>Kanala X gönderilir; defterdeki satılabilir veya kanal üst limiti daha azsa o gönderilir.</summary>
    Fixed,
}

/// <summary>
/// Bir pazaryeri ilanı için platform bazlı stok kuralı. Kural defteri değiştirmez, yalnızca o ilana gidecek değeri belirler;
/// sonuç defterdeki satılabilir stoğu asla aşmaz (oversell engeli korunur).
/// </summary>
public sealed record ListingStockRule
{
    public const string UpperLimitCode = "UST_LIMIT";
    public const string FixedCode = "SABIT";

    public ListingStockRule(ListingStockRuleType type, int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Type = type;
        Value = value;
    }

    public ListingStockRuleType Type { get; }
    public int Value { get; }

    public static ListingStockRuleType? ParseType(string? code) => code switch
    {
        UpperLimitCode => ListingStockRuleType.UpperLimit,
        FixedCode => ListingStockRuleType.Fixed,
        _ => null,
    };

    public static string ToCode(ListingStockRuleType type) => type switch
    {
        ListingStockRuleType.UpperLimit => UpperLimitCode,
        ListingStockRuleType.Fixed => FixedCode,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}
