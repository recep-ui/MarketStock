using PazaryeriHub.Domain.Orders;

namespace PazaryeriHub.Application.Panel;

/// <summary>Genel arama terimi kuralları (ADR-034).</summary>
public static class SearchTerm
{
    public const int MinLength = 2;
    public const int MaxLength = 100;

    /// <summary>Boşlukları atar; <see cref="MinLength"/>'ten kısa terim için null (aranmaz); uzun terimi kısaltır.</summary>
    public static string? Normalize(string? q)
    {
        var term = q?.Trim();
        if (string.IsNullOrEmpty(term) || term.Length < MinLength)
        {
            return null;
        }
        return term.Length > MaxLength ? term[..MaxLength] : term;
    }

    /// <summary>T-SQL LIKE için kaçış (ESCAPE '\').</summary>
    public static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
}

/// <param name="MatchedBy">Neyle eşleşti: SKU, BARKOD, URUN_ADI, KANAL_KODU.</param>
public sealed record SkuSearchHit(int SkuId, string SkuCode, string? Barcode, string ProductName, string? Variant, int Available, string MatchedBy);

public sealed record OrderSearchHit(long OrderId, string ChannelCode, string ChannelOrderNo, OrderStatus Status, DateTime OrderDateUtc, decimal Total);

/// <summary>Genel arama sonucu. Müşteri bilgisi içermez (tüm rollere açık).</summary>
public sealed record SearchResult(string? Term, IReadOnlyList<SkuSearchHit> Skus, IReadOnlyList<OrderSearchHit> Orders);

public interface ISearchService
{
    /// <summary>SKU (kod öneki, barkod, ürün adı, kanal barkodu/ürün kodu) ve sipariş (kanal sipariş no öneki) arar; her biri en fazla <paramref name="limit"/>.</summary>
    Task<SearchResult> SearchAsync(string? q, int limit, CancellationToken ct);
}
