using System.Globalization;
using System.Text;

namespace PazaryeriHub.Application.ChannelCatalog;

/// <summary>Kanal dosyasından veya kanal API'sinden gelen tek ürün satırı (normalize edilmiş).</summary>
/// <param name="Row">Excel satır numarası; API kaynağında sıra numarası.</param>
/// <param name="ChannelProductCode">Kanalın kendi ürün kimliği (ör. Beymen productId). Trendyol dosyasında yok.</param>
/// <param name="Passive">Kanal durumu "satışa kapalı / arşiv" ise true → eşleştirme PASIF yazılır.</param>
public sealed record ChannelCatalogItem(
    int Row,
    string? ChannelProductCode,
    string? Barcode,
    string? StockCode,
    string? ModelCode,
    string? Color,
    string? Size,
    string? Name,
    string? Brand,
    string? Category,
    string? ChannelStatus,
    bool Passive)
{
    /// <summary>Eşleşmeyenler tablosundaki tekil anahtar: barkod, yoksa kanal ürün kodu.</summary>
    public string Key => Barcode ?? ChannelProductCode ?? "";
}

/// <summary>Eşleştirmede aday SKU (ürün bilgisiyle).</summary>
public sealed record CatalogSkuRef(int SkuId, string SkuCode, string? Barcode, string? ModelCode, string? Color, string? Size, string ProductName, string? Brand);

/// <summary>Bu kanalda zaten var olan eşleştirme.</summary>
public sealed record ExistingListingRef(int SkuId, string? ChannelProductCode, string? ChannelBarcode);

/// <summary>Eşleştirme adımı (öncelik sırasıyla).</summary>
public enum CatalogMatchStep
{
    /// <summary>Kanal barkodu / ürün kodu bu kanalda zaten bir SKU'ya bağlı.</summary>
    ExistingListing = 0,

    /// <summary>Kanal barkodu = Sku.Barkod.</summary>
    Barcode = 1,

    /// <summary>Satıcı stok kodu = SkuKodu (yalnızca iki tarafta da tekilse).</summary>
    StockCode = 2,

    /// <summary>Model kodu + renk + beden (yalnızca tek aday varsa).</summary>
    ModelColorSize = 3
}

public static class UnmatchedReasons
{
    public const string NoMatch = "ESLESME_YOK";
    public const string Ambiguous = "BELIRSIZ";
    public const string Conflict = "CAKISMA";
}

/// <summary>Tek satırın eşleştirme sonucu. <see cref="Sku"/> null ise eşleşmemiştir (<see cref="Reason"/> dolu).</summary>
public sealed record CatalogMatch(
    ChannelCatalogItem Item,
    CatalogSkuRef? Sku,
    CatalogMatchStep? Step,
    string? Reason,
    string? Detail,
    CatalogSkuRef? Suggestion,
    decimal? SuggestionScore)
{
    public bool IsMatched => Sku is not null;
}

/// <summary>
/// Kanal ürünlerini SKU'larla eşleştiren SAF fonksiyon (ADR-020). Sıra:
/// (0) bu kanaldaki mevcut eşleştirme → (1) barkod = Sku.Barkod → (2) satıcı stok kodu = SkuKodu (iki tarafta tekilse)
/// → (3) model kodu + renk + beden (tek aday varsa). Belirsizlikte bağlama yapılmaz.
/// Ürün adı benzerliği yalnızca eşleşmeyenler için ÖNERİ üretir; asla otomatik bağlamaz.
/// Aynı SKU'ya birden fazla kanal satırı düşerse hiçbiri bağlanmaz (UNIQUE(SkuId, ChannelId) korunur).
/// </summary>
public static class ChannelCatalogMatcher
{
    /// <summary>Öneri için en düşük ad benzerliği (trigram Dice, 0–1).</summary>
    public const decimal SuggestionThreshold = 0.60m;

    public static IReadOnlyList<CatalogMatch> Match(
        IReadOnlyList<ChannelCatalogItem> items,
        IReadOnlyList<CatalogSkuRef> skus,
        IReadOnlyList<ExistingListingRef> existingListings)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(skus);
        ArgumentNullException.ThrowIfNull(existingListings);

        var skuById = skus.ToDictionary(s => s.SkuId);
        var byBarcode = new Dictionary<string, CatalogSkuRef>(StringComparer.Ordinal);
        foreach (var s in skus.Where(s => s.Barcode is not null))
        {
            byBarcode.TryAdd(s.Barcode!, s);
        }
        var byCode = new Dictionary<string, CatalogSkuRef>(StringComparer.Ordinal);
        foreach (var s in skus)
        {
            byCode.TryAdd(s.SkuCode, s);
        }
        var byModelColorSize = skus.Where(s => s.ModelCode is not null)
            .GroupBy(s => ModelColorSizeKey(s.ModelCode, s.Color, s.Size), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var listingByBarcode = new Dictionary<string, int>(StringComparer.Ordinal);
        var listingByCode = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var l in existingListings)
        {
            if (l.ChannelBarcode is not null)
            {
                listingByBarcode.TryAdd(l.ChannelBarcode, l.SkuId);
            }
            if (l.ChannelProductCode is not null)
            {
                listingByCode.TryAdd(l.ChannelProductCode, l.SkuId);
            }
        }

        var stockCodeCounts = items.Where(i => i.StockCode is not null)
            .GroupBy(i => i.StockCode!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var modelKeyCounts = items.Where(i => i.ModelCode is not null)
            .GroupBy(i => ModelColorSizeKey(i.ModelCode, i.Color, i.Size), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var results = new CatalogMatch[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            results[i] = MatchOne(items[i], skuById, byBarcode, byCode, byModelColorSize, listingByBarcode, listingByCode, stockCodeCounts, modelKeyCounts);
        }

        // Aynı SKU'ya birden fazla satır düştüyse: hiçbiri bağlanmaz.
        foreach (var group in results.Select((r, idx) => (r, idx)).Where(x => x.r.IsMatched).GroupBy(x => x.r.Sku!.SkuId).Where(g => g.Count() > 1))
        {
            var rows = string.Join(", ", group.Select(x => x.r.Item.Row).Order());
            foreach (var (r, idx) in group)
            {
                results[idx] = r with
                {
                    Sku = null, Step = null, Reason = UnmatchedReasons.Ambiguous,
                    Detail = $"Birden fazla kanal satırı aynı SKU'ya ({r.Sku!.SkuCode}) eşleşti (satırlar: {rows})."
                };
            }
        }

        AddSuggestions(results, skus);
        return results;
    }

    private static CatalogMatch MatchOne(
        ChannelCatalogItem item,
        Dictionary<int, CatalogSkuRef> skuById,
        Dictionary<string, CatalogSkuRef> byBarcode,
        Dictionary<string, CatalogSkuRef> byCode,
        Dictionary<string, List<CatalogSkuRef>> byModelColorSize,
        Dictionary<string, int> listingByBarcode,
        Dictionary<string, int> listingByCode,
        Dictionary<string, int> stockCodeCounts,
        Dictionary<string, int> modelKeyCounts)
    {
        // 0) Bu kanalda zaten bağlı (barkod veya kanal ürün kodu)
        int? byListingBarcode = item.Barcode is not null && listingByBarcode.TryGetValue(item.Barcode, out var lb) ? lb : null;
        int? byListingCode = item.ChannelProductCode is not null && listingByCode.TryGetValue(item.ChannelProductCode, out var lc) ? lc : null;
        if (byListingBarcode is not null && byListingCode is not null && byListingBarcode != byListingCode)
        {
            return Unmatched(item, UnmatchedReasons.Conflict, "Kanal barkodu ve kanal ürün kodu bu kanalda farklı SKU'lara bağlı.");
        }
        if ((byListingBarcode ?? byListingCode) is { } listedSku && skuById.TryGetValue(listedSku, out var listed))
        {
            return Matched(item, listed, CatalogMatchStep.ExistingListing);
        }

        // 1) Barkod
        if (item.Barcode is not null && byBarcode.TryGetValue(item.Barcode, out var byBc))
        {
            return Matched(item, byBc, CatalogMatchStep.Barcode);
        }

        string? ambiguity = null;

        // 2) Satıcı stok kodu = SkuKodu (dosyada tekil olmalı: Trendyol'da stok kodu çoğu zaman model/renk düzeyindedir)
        if (item.StockCode is not null && byCode.TryGetValue(item.StockCode, out var byStock))
        {
            var count = stockCodeCounts.GetValueOrDefault(item.StockCode);
            if (count == 1)
            {
                return Matched(item, byStock, CatalogMatchStep.StockCode);
            }
            ambiguity = $"Satıcı stok kodu dosyada {count} satırda geçiyor (beden düzeyinde değil); stok koduyla bağlanmadı.";
        }

        // 3) Model kodu + renk + beden
        if (item.ModelCode is not null)
        {
            var key = ModelColorSizeKey(item.ModelCode, item.Color, item.Size);
            if (byModelColorSize.TryGetValue(key, out var candidates))
            {
                var inFile = modelKeyCounts.GetValueOrDefault(key);
                if (candidates.Count == 1 && inFile == 1)
                {
                    return Matched(item, candidates[0], CatalogMatchStep.ModelColorSize);
                }
                ambiguity = candidates.Count > 1
                    ? $"Aynı model + renk + bedende {candidates.Count} SKU var ({string.Join(", ", candidates.Take(3).Select(c => c.SkuCode))})."
                    : $"Dosyada aynı model + renk + bedende {inFile} satır var.";
            }
        }

        return ambiguity is null
            ? Unmatched(item, UnmatchedReasons.NoMatch, "Barkod, stok kodu veya model + renk + beden ile SKU bulunamadı.")
            : Unmatched(item, UnmatchedReasons.Ambiguous, ambiguity);
    }

    private static CatalogMatch Matched(ChannelCatalogItem item, CatalogSkuRef sku, CatalogMatchStep step) =>
        new(item, sku, step, null, null, null, null);

    private static CatalogMatch Unmatched(ChannelCatalogItem item, string reason, string detail) =>
        new(item, null, null, reason, detail, null, null);

    // ------------------------------------------------------------------ Ad benzerliği önerisi

    private static void AddSuggestions(CatalogMatch[] results, IReadOnlyList<CatalogSkuRef> skus)
    {
        if (skus.Count == 0 || results.All(r => r.IsMatched))
        {
            return;
        }

        // Ürün adı başına trigram kümesi bir kez hesaplanır
        var byName = skus.GroupBy(s => CatalogText.Normalize(s.ProductName), StringComparer.Ordinal)
            .Where(g => g.Key.Length > 0)
            .Select(g => (Grams: Trigrams(g.Key), Skus: g.ToList()))
            .ToList();

        for (var i = 0; i < results.Length; i++)
        {
            var r = results[i];
            if (r.IsMatched || r.Item.Name is null)
            {
                continue;
            }
            var itemGrams = Trigrams(CatalogText.Normalize(r.Item.Name));
            if (itemGrams.Count == 0)
            {
                continue;
            }

            CatalogSkuRef? best = null;
            var bestScore = 0m;
            foreach (var (grams, group) in byName)
            {
                var score = Dice(itemGrams, grams);
                if (score < SuggestionThreshold || score < bestScore)
                {
                    continue;
                }
                foreach (var sku in group)
                {
                    if (!Compatible(r.Item.Brand, sku.Brand) || !Compatible(r.Item.Color, sku.Color) || !Compatible(r.Item.Size, sku.Size))
                    {
                        continue;
                    }
                    if (best is null || score > bestScore || (score == bestScore && sku.SkuId < best.SkuId))
                    {
                        best = sku;
                        bestScore = score;
                    }
                }
            }

            if (best is not null)
            {
                results[i] = r with { Suggestion = best, SuggestionScore = bestScore };
            }
        }
    }

    /// <summary>İki taraf da doluysa eşit olmalı (Türkçe büyük harf, boşluk normalize).</summary>
    private static bool Compatible(string? a, string? b) =>
        a is null || b is null || string.Equals(CatalogText.Normalize(a), CatalogText.Normalize(b), StringComparison.Ordinal);

    private static decimal Dice(HashSet<string> a, HashSet<string> b)
    {
        var common = a.Count <= b.Count ? a.Count(b.Contains) : b.Count(a.Contains);
        return Math.Round(2m * common / (a.Count + b.Count), 2, MidpointRounding.ToZero);
    }

    private static HashSet<string> Trigrams(string normalized)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var padded = " " + word + " ";
            for (var i = 0; i + 3 <= padded.Length; i++)
            {
                set.Add(padded.Substring(i, 3));
            }
        }
        return set;
    }

    public static string ModelColorSizeKey(string? model, string? color, string? size) =>
        CatalogText.Normalize(model) + "\u001F" + CatalogText.Normalize(color) + "\u001F" + CatalogText.Normalize(size);
}

public static class CatalogText
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>
    /// Karşılaştırma için: kırp, Türkçe kurallarıyla büyük harf, harf/rakam dışını boşluk yap, ardışık boşlukları tekle.
    /// Bulanık değildir: "Beyaz" = "BEYAZ", "Kırmızı" ≠ "Kirmizi".
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }
        var upper = value.Trim().ToUpper(Turkish);
        var sb = new StringBuilder(upper.Length);
        var space = false;
        foreach (var ch in upper)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                space = false;
            }
            else if (!space && sb.Length > 0)
            {
                sb.Append(' ');
                space = true;
            }
        }
        return sb.ToString().TrimEnd();
    }
}
