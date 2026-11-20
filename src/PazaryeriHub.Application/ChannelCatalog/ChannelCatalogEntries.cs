using PazaryeriHub.Application.Catalog;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Excel;

namespace PazaryeriHub.Application.ChannelCatalog;

/// <summary>
/// Kanal API'sinden gelen katalog öğelerini eşleştiricinin girdisine çevirir (Excel okuyucusuyla aynı kurallar):
/// değerler kırpılır; kod alanı sınırı aşan veya barkod/kanal kodu olmayan öğe hatalıdır; tekrarlanan barkod/kanal kodu
/// hatalıdır (tüm tekrarlar). "Satır" = API'den geliş sırası (1'den başlar).
/// </summary>
public static class ChannelCatalogEntries
{
    public static (IReadOnlyList<ChannelCatalogItem> Items, IReadOnlyList<ImportRowError> Errors) ToItems(IReadOnlyList<ChannelCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var items = new List<ChannelCatalogItem>(entries.Count);
        var errors = new List<ImportRowError>();

        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var row = i + 1;
            var before = errors.Count;

            string? Code(string? value, int max, string column)
            {
                var v = Clean(value);
                if (v is not null && v.Length > max)
                {
                    errors.Add(new(row, column, $"{column} en fazla {max} karakter olabilir ({v.Length})."));
                    return null;
                }
                return v;
            }

            var productCode = Code(e.ChannelProductCode, CatalogLimits.ChannelProductCode, "Kanal Ürün Kodu");
            var barcode = Code(e.Barcode, CatalogLimits.ChannelBarcode, "Barkod");
            var stockCode = Code(e.StockCode, CatalogLimits.SkuCode, "Stok Kodu");
            var modelCode = Code(e.ModelCode, CatalogLimits.ModelCode, "Model Kodu");
            var color = Code(e.Color, CatalogLimits.Color, "Renk");
            var size = Code(e.Size, CatalogLimits.Size, "Beden");
            if (errors.Count == before && barcode is null && productCode is null)
            {
                errors.Add(new(row, "Barkod", "Kanal öğesinde barkod ve kanal ürün kodu yok."));
            }
            if (errors.Count > before)
            {
                continue;
            }

            items.Add(new ChannelCatalogItem(row, productCode, barcode, stockCode, modelCode, color, size,
                Truncate(Clean(e.Name), CatalogLimits.ProductName), Truncate(Clean(e.Brand), CatalogLimits.Brand),
                Truncate(Clean(e.Category), CatalogLimits.Category), Truncate(Clean(e.ChannelStatus), 100), e.Passive,
                Truncate(Clean(e.VariantKey), 400), Truncate(Clean(e.VariantAttributes), 400),
                ListingChannelData.HttpsOrNull(Clean(e.ProductUrl)),
                e.SalePrice is > 0 ? e.SalePrice : null, e.ListPrice is > 0 ? e.ListPrice : null,
                ListingChannelData.HttpsOrNull(Clean(e.ImageUrl))));
        }

        var invalid = new HashSet<int>();
        Duplicates(items, x => x.Barcode, "Barkod", errors, invalid);
        Duplicates(items, x => x.ChannelProductCode, "Kanal Ürün Kodu", errors, invalid);
        items.RemoveAll(x => invalid.Contains(x.Row));

        return (items, errors.OrderBy(x => x.Row).ThenBy(x => x.Column, StringComparer.Ordinal).ToList());
    }

    private static void Duplicates(List<ChannelCatalogItem> items, Func<ChannelCatalogItem, string?> key, string column, List<ImportRowError> errors, HashSet<int> invalid)
    {
        foreach (var group in items.Where(x => key(x) is not null).GroupBy(x => key(x)!, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            var rows = string.Join(", ", group.Select(x => x.Row).Take(10));
            foreach (var item in group)
            {
                errors.Add(new(item.Row, column, $"'{group.Key}' kanal kataloğunda birden fazla kez var (sıra: {rows})."));
                invalid.Add(item.Row);
            }
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

/// <summary>"Kanaldan Ürün Çek" job'ının son durumu (ChannelJobState, IsTipi = KATALOG_CEK).</summary>
/// <param name="State">YOK | CALISIYOR | TAMAMLANDI | HATA</param>
public sealed record CatalogFetchStatus(
    int ChannelId,
    string State,
    DateTime? LastAttemptUtc,
    DateTime? LastSuccessUtc,
    string? LastError,
    CatalogImportReport? Report);

public static class CatalogFetchStates
{
    public const string None = "YOK";
    public const string Running = "CALISIYOR";
    public const string Completed = "TAMAMLANDI";
    public const string Failed = "HATA";
}
