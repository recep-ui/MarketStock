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
    bool Passive,
    string? VariantKey = null,
    string? VariantAttributes = null,
    string? ProductUrl = null,
    decimal? SalePrice = null,
    decimal? ListPrice = null,
    string? ImageUrl = null)
{
    /// <summary>Renk veya beden biliniyor mu (model + renk + beden eşleşmesi/gruplaması ancak o zaman güvenilir).</summary>
    public bool HasColorOrSize => Color is not null || Size is not null;

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
/// → (3) model kodu + renk + beden (bizde tek aday SKU varsa). Bir satır için birden fazla aday varsa bağlama yapılmaz (belirsiz).
/// Ürün adı benzerliği yalnızca eşleşmeyenler için ÖNERİ üretir; asla otomatik bağlamaz.
/// Aynı SKU'ya birden fazla kanal satırı düşebilir (ADR-023): her satır o SKU'nun ayrı ilanı olur (ör. Trendyol
/// "Çoklanan Ürün Satışı"); tam ortak havuz gereği her ilan aynı stoğu görür.
/// </summary>
public static class ChannelCatalogMatcher
{
    /// <summary>Öneri için en düşük ad benzerliği (trigram Dice, 0–1).</summary>
    public const decimal SuggestionThreshold = 0.60m;

    /// <summary>İki ürün adının benzerliği (normalize + trigram Dice, 0–1); biri boşsa 0. Öneriyle aynı ölçü (ADR-035).</summary>
    public static decimal NameSimilarity(string? a, string? b)
    {
        var x = Trigrams(CatalogText.Normalize(a));
        var y = Trigrams(CatalogText.Normalize(b));
        return x.Count == 0 || y.Count == 0 ? 0m : Dice(x, y);
    }

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

        AddCodeSuggestions(results, skus);
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

        // 3) Model kodu + renk + beden: bizde tek aday SKU varsa bağlanır. Dosyada aynı varyant birden fazla satırdaysa
        //    (çoklanan ilan) hepsi aynı SKU'nun ayrı ilanları olur (ADR-023).
        //    Renk ve beden ikisi de bilinmiyorsa model kodu tek başına varyantı tanımlamaz (aynı modelin farklı renk/ebatları):
        //    bu adım atlanır, satır eşleşmeyenlere düşer (ADR-031).
        if (item.ModelCode is not null && item.HasColorOrSize)
        {
            var key = ModelColorSizeKey(item.ModelCode, item.Color, item.Size);
            if (byModelColorSize.TryGetValue(key, out var candidates))
            {
                if (candidates.Count == 1)
                {
                    return Matched(item, candidates[0], CatalogMatchStep.ModelColorSize);
                }
                ambiguity = $"Aynı model + renk + bedende {candidates.Count} SKU var ({string.Join(", ", candidates.Take(3).Select(c => c.SkuCode))}).";
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

    // ------------------------------------------------------------------ Harf farkıyla kod önerisi

    /// <summary>
    /// ADR-055: satıcı stok kodu bir SkuKodu'na veya SKU barkoduna yalnızca büyük/küçük harf ve Türkçe I/İ/ı farkıyla uyuyorsa
    /// (ör. Hepsiburada "MİCROBORNOZ21" ↔ "Microbornoz21") ÖNERİ üretilir (skor 1). Otomatik bağlanmaz: kodlar ikili
    /// karşılaştırılır ve model düzeyindeki bir stok kodu ("Chain75150") başka bir SKU koduna ("CHAIN75150") harf farkıyla
    /// uyabilir. Harf farkıyla birden fazla SKU tutuyorsa öneri yok.
    /// </summary>
    private static void AddCodeSuggestions(CatalogMatch[] results, IReadOnlyList<CatalogSkuRef> skus)
    {
        if (results.All(r => r.IsMatched || r.Item.StockCode is null))
        {
            return;
        }
        var byFolded = new Dictionary<string, List<CatalogSkuRef>>(StringComparer.Ordinal);
        foreach (var s in skus)
        {
            foreach (var code in new[] { s.SkuCode, s.Barcode }.Where(c => !string.IsNullOrWhiteSpace(c)).Select(CatalogText.FoldCode).Distinct())
            {
                if (!byFolded.TryGetValue(code, out var list))
                {
                    byFolded[code] = list = [];
                }
                if (!list.Exists(x => x.SkuId == s.SkuId))
                {
                    list.Add(s);
                }
            }
        }

        for (var i = 0; i < results.Length; i++)
        {
            var r = results[i];
            // Yalnızca hiç aday bulunamayanlar: belirsiz/çakışma satırının açıklaması korunur
            if (r.Reason != UnmatchedReasons.NoMatch || r.Item.StockCode is null
                || !byFolded.TryGetValue(CatalogText.FoldCode(r.Item.StockCode), out var candidates) || candidates.Count != 1)
            {
                continue;
            }
            var sku = candidates[0];
            results[i] = r with
            {
                Suggestion = sku,
                SuggestionScore = 1m,
                Detail = $"Satıcı stok kodu {sku.SkuCode} SKU'suna yalnızca harf farkıyla (büyük/küçük, I/İ) uyuyor; doğruysa onaylayın.",
            };
        }
    }

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
            if (r.IsMatched || r.Item.Name is null || r.Suggestion is not null)
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
    /// Kod öneri anahtarı (ADR-055): kırp, I/İ/ı/i → I, gerisi kültürden bağımsız büyük harf. Tire, boşluk, rakam korunur.
    /// Kanallar kodu Türkçe ya da İngilizce kuralla büyütebildiği için iki yazım da aynı anahtara düşer. Yalnızca öneri içindir.
    /// </summary>
    public static string FoldCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }
        var trimmed = value.Trim();
        var sb = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            sb.Append(ch is 'i' or 'ı' or 'İ' ? 'I' : char.ToUpperInvariant(ch));
        }
        return sb.ToString();
    }

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
