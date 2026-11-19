using Dapper;
using PazaryeriHub.Application.Panel;
using PazaryeriHub.Domain.Orders;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.Panel;

/// <summary>
/// Genel arama (ADR-034). SKU kodu ve kanal sipariş no BIN2 collation'lı olduğu için öneki hem olduğu gibi hem de
/// büyük harfe çevrilmiş olarak (UPPER, Türkçe olmayan kural: i → I) aranır; ürün adı Turkish_CI_AS ile içerir araması.
/// Barkod ve kanal barkodu/ürün kodu birebir eşleşir. Müşteri bilgisi dönmez.
/// </summary>
public sealed class SearchService(SqlConnectionFactory connectionFactory) : ISearchService
{
    public async Task<SearchResult> SearchAsync(string? q, int limit, CancellationToken ct)
    {
        var term = SearchTerm.Normalize(q);
        if (term is null)
        {
            return new SearchResult(null, [], []);
        }

        var escaped = SearchTerm.EscapeLike(term);
        var args = new
        {
            limit = Math.Clamp(limit, 1, 20),
            term,
            prefix = escaped + "%",
            upperPrefix = escaped.ToUpperInvariant() + "%",
            contains = "%" + escaped + "%"
        };

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        using var multi = await connection.QueryMultipleAsync(new CommandDefinition("""
            SELECT TOP (@limit) x.SkuId, x.SkuCode, x.Barcode, x.ProductName, x.Color, x.Size, x.Available, x.MatchedBy
              FROM (
                SELECT s.Id AS SkuId, s.SkuKodu AS SkuCode, s.Barkod AS Barcode, p.Ad AS ProductName, s.Renk AS Color, s.Beden AS Size,
                       ISNULL((SELECT SUM(b.Satilabilir) FROM dbo.StockBalance b WHERE b.SkuId = s.Id), 0) AS Available,
                       CASE WHEN s.Barkod = @term THEN 'BARKOD'
                            WHEN s.SkuKodu LIKE @prefix ESCAPE '\' OR UPPER(s.SkuKodu) LIKE @upperPrefix ESCAPE '\' THEN 'SKU'
                            WHEN EXISTS (SELECT 1 FROM dbo.ChannelListing l WHERE l.SkuId = s.Id AND (l.KanalBarkod = @term OR l.KanalUrunKodu = @term)) THEN 'KANAL_KODU'
                            ELSE 'URUN_ADI' END AS MatchedBy
                  FROM dbo.Sku s
                  JOIN dbo.Product p ON p.Id = s.ProductId
                 WHERE s.Barkod = @term
                    OR s.SkuKodu LIKE @prefix ESCAPE '\' OR UPPER(s.SkuKodu) LIKE @upperPrefix ESCAPE '\'
                    OR p.Ad LIKE @contains ESCAPE '\'
                    OR EXISTS (SELECT 1 FROM dbo.ChannelListing l WHERE l.SkuId = s.Id AND (l.KanalBarkod = @term OR l.KanalUrunKodu = @term))
              ) AS x
             ORDER BY CASE x.MatchedBy WHEN 'BARKOD' THEN 0 WHEN 'SKU' THEN 1 WHEN 'KANAL_KODU' THEN 2 ELSE 3 END, x.SkuCode;

            SELECT TOP (@limit) o.Id AS OrderId, c.Kod AS ChannelCode, o.KanalSiparisNo AS ChannelOrderNo, o.Durum AS Status,
                   o.SiparisTarihi AS OrderDateUtc, o.Toplam AS Total
              FROM dbo.[Order] o
              JOIN dbo.Channel c ON c.Id = o.ChannelId
             WHERE o.KanalSiparisNo LIKE @prefix ESCAPE '\' OR UPPER(o.KanalSiparisNo) LIKE @upperPrefix ESCAPE '\'
             ORDER BY o.SiparisTarihi DESC, o.Id DESC;
            """, args, cancellationToken: ct));

        var skus = (await multi.ReadAsync<SkuRow>()).Select(r => new SkuSearchHit(
            r.SkuId, r.SkuCode, r.Barcode, r.ProductName, Variant(r.Color, r.Size), r.Available, r.MatchedBy)).ToList();
        var orders = (await multi.ReadAsync<OrderRow>()).Select(r => new OrderSearchHit(
            r.OrderId, r.ChannelCode, r.ChannelOrderNo, Enum.Parse<OrderStatus>(r.Status),
            DateTime.SpecifyKind(r.OrderDateUtc, DateTimeKind.Utc), r.Total)).ToList();
        return new SearchResult(term, skus, orders);
    }

    /// <summary>"Renk / Beden"; ikisi de boşsa null.</summary>
    internal static string? Variant(string? color, string? size)
    {
        var parts = new[] { color, size }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()).ToList();
        return parts.Count == 0 ? null : string.Join(" / ", parts);
    }

    private sealed class SkuRow
    {
        public int SkuId { get; init; }
        public string SkuCode { get; init; } = "";
        public string? Barcode { get; init; }
        public string ProductName { get; init; } = "";
        public string? Color { get; init; }
        public string? Size { get; init; }
        public int Available { get; init; }
        public string MatchedBy { get; init; } = "";
    }

    private sealed class OrderRow
    {
        public long OrderId { get; init; }
        public string ChannelCode { get; init; } = "";
        public string ChannelOrderNo { get; init; } = "";
        public string Status { get; init; } = "";
        public DateTime OrderDateUtc { get; init; }
        public decimal Total { get; init; }
    }
}
