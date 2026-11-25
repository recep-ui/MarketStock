using System.Security.Cryptography;
using System.Text;
using PazaryeriHub.Application.ChannelCatalog;
using PazaryeriHub.Application.Common;

namespace PazaryeriHub.Application.Panel;

/// <summary>İlanın kanal tarafı: kanalın bildirdiği kodlar ve ürün bilgisi (bilinmeyen alan null).</summary>
public sealed record ChannelSide(
    string? Barcode, string? ProductCode, string? StockCode, string? Name, string? Brand, string? Color, string? Size, string? Variant);

/// <summary>İlanın defter tarafı: bağlandığı SKU.</summary>
public sealed record LedgerSide(string SkuCode, string? Barcode, string ProductName, string? Brand, string? Color, string? Size);

public sealed record MatchWarning(string Code, string Message);

/// <param name="Basis">Eşleşmeyi kanal verisinin neyle doğruladığı: BARKOD, STOK_KODU veya DIGER (elle / model / bilinmiyor).</param>
public sealed record ListingMatchResult(string Basis, IReadOnlyList<MatchWarning> Warnings);

public static class MatchBases
{
    public const string Barcode = "BARKOD";
    public const string StockCode = "STOK_KODU";
    public const string Other = "DIGER";
}

public static class MatchWarnings
{
    public const string BarcodeDiffers = "BARKOD_FARKLI";
    public const string StockCodeDiffers = "STOK_KODU_FARKLI";
    public const string ColorDiffers = "RENK_FARKLI";
    public const string SizeDiffers = "BEDEN_FARKLI";
    /// <summary>İki tarafta da ebat ölçüsü var ve farklı (ADR-053).</summary>
    public const string DimensionDiffers = "EBAT_FARKLI";
    /// <summary>Bir tarafta beden/ebat var, diğerinde hiç yok (ADR-053).</summary>
    public const string DimensionMissing = "EBAT_EKSIK";
    public const string NameDiffers = "AD_BENZEMIYOR";
}

/// <summary>
/// Yanlış eşleştirme belirtileri (ADR-035). Saf fonksiyon; yalnızca uyarı üretir, eşleştirmeyi değiştirmez.
/// Bir alan için uyarı ancak iki tarafta da değer varsa üretilir (bilinmeyen = sorun değil).
/// </summary>
public static class ListingMatchCheck
{
    /// <summary>Bu değerin altındaki ad benzerliği "ad benzemiyor" uyarısı verir. Öneri eşiğinden (0,60) bilinçli olarak düşük:
    /// kanal adları marka/renk/ölçü ekleri taşır; yalnızca belirgin farklılık uyarılır.</summary>
    public const decimal NameSimilarityThreshold = 0.30m;

    public static ListingMatchResult Evaluate(ChannelSide channel, LedgerSide ledger)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(ledger);
        var warnings = new List<MatchWarning>();

        var barcodeKnown = Has(channel.Barcode) && Has(ledger.Barcode);
        var barcodeSame = barcodeKnown && string.Equals(channel.Barcode!.Trim(), ledger.Barcode!.Trim(), StringComparison.Ordinal);
        var stockKnown = Has(channel.StockCode) && Has(ledger.SkuCode);
        var stockSame = stockKnown && string.Equals(channel.StockCode!.Trim(), ledger.SkuCode.Trim(), StringComparison.OrdinalIgnoreCase);

        // Kodlardan biri eşleşmeyi doğruluyorsa diğerinin farkı uyarılmaz: stok kodu aynıysa barkod farkı (ör. çoklanan ilan,
        // ADR-023) ve barkod aynıysa stok kodu farkı (kanalın kendi kodu) normaldir.
        if (barcodeKnown && !barcodeSame && !stockSame)
        {
            warnings.Add(new(MatchWarnings.BarcodeDiffers, $"Barkod farklı: kanalda {channel.Barcode}, SKU'da {ledger.Barcode}."));
        }
        if (!barcodeSame && stockKnown && !stockSame)
        {
            warnings.Add(new(MatchWarnings.StockCodeDiffers, $"Stok kodu farklı: kanalda {channel.StockCode}, SKU {ledger.SkuCode}."));
        }
        if (Differs(channel.Color, ledger.Color))
        {
            warnings.Add(new(MatchWarnings.ColorDiffers, $"Renk farklı: kanalda {channel.Color}, SKU'da {ledger.Color}."));
        }
        AddSizeWarning(channel, ledger, warnings);
        if (Has(channel.Name) && Has(ledger.ProductName)
            && ChannelCatalogMatcher.NameSimilarity(channel.Name, ledger.ProductName) < NameSimilarityThreshold)
        {
            warnings.Add(new(MatchWarnings.NameDiffers, $"Ürün adı benzemiyor: kanalda \"{channel.Name}\", defterde \"{ledger.ProductName}\"."));
        }

        var basis = barcodeSame ? MatchBases.Barcode : stockSame ? MatchBases.StockCode : MatchBases.Other;
        return new ListingMatchResult(basis, warnings);
    }

    /// <summary>
    /// Onay imzası (ADR-036): SKU, iki taraftaki barkod/kod/ad/renk/beden, kanal varyant metni (ADR-053, "v2") ve uyarı kodları (sırasız). Metinler
    /// <see cref="CatalogText.Normalize"/>, kodlar kırpılarak girer; yalnızca biçim farkı imzayı bozmaz. SHA-256, küçük harf hex.
    /// </summary>
    public static string Signature(int skuId, ChannelSide channel, LedgerSide ledger, IReadOnlyList<MatchWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(warnings);
        static string Code(string? v) => v?.Trim() ?? "";
        var parts = new[]
        {
            "v2", skuId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Code(ledger.SkuCode).ToUpperInvariant(), Code(ledger.Barcode), CatalogText.Normalize(ledger.ProductName),
            CatalogText.Normalize(ledger.Color), CatalogText.Normalize(ledger.Size),
            Code(channel.Barcode), Code(channel.ProductCode), Code(channel.StockCode).ToUpperInvariant(), CatalogText.Normalize(channel.Name),
            CatalogText.Normalize(channel.Color), CatalogText.Normalize(channel.Size), CatalogText.Normalize(channel.Variant),
            string.Join(',', warnings.Select(w => w.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001F', parts))));
    }

    /// <summary>
    /// Beden/ebat kontrolü (ADR-053). Ebat önce beden/ebat alanından, alan boşsa kanal varyant metninden, o da yoksa addan okunur.
    /// İki tarafta da ebat ölçüsü varsa sayı olarak karşılaştırılır (EBAT_FARKLI); değilse iki alan metin olarak (BEDEN_FARKLI).
    /// Bir tarafta beden/ebat varken diğerinde hiç yoksa EBAT_EKSIK. Kanalın ürün bilgisi hiç okunmamışsa (ad ve varyant yok)
    /// kanal tarafı "bilinmiyor" sayılır, eksik denmez.
    /// </summary>
    private static void AddSizeWarning(ChannelSide channel, LedgerSide ledger, List<MatchWarning> warnings)
    {
        var channelDims = SizeDimensions.Parse(channel.Size) ?? SizeDimensions.Parse(channel.Variant) ?? SizeDimensions.Parse(channel.Name);
        var ledgerDims = SizeDimensions.Parse(ledger.Size) ?? SizeDimensions.Parse(ledger.ProductName);
        if (channelDims is not null && ledgerDims is not null)
        {
            if (channelDims != ledgerDims)
            {
                warnings.Add(new(MatchWarnings.DimensionDiffers,
                    $"Ebat farklı: kanalda {SizeDimensions.Display(channelDims)}, SKU'da {SizeDimensions.Display(ledgerDims)}."));
            }
            return;
        }
        if (Has(channel.Size) && Has(ledger.Size))
        {
            if (Differs(channel.Size, ledger.Size))
            {
                warnings.Add(new(MatchWarnings.SizeDiffers, $"Beden/ebat farklı: kanalda {channel.Size}, SKU'da {ledger.Size}."));
            }
            return;
        }
        var channelSize = channelDims is not null ? SizeDimensions.Display(channelDims) : channel.Size?.Trim();
        var ledgerSize = ledgerDims is not null ? SizeDimensions.Display(ledgerDims) : ledger.Size?.Trim();
        var channelKnown = Has(channel.Name) || Has(channel.Variant) || Has(channel.Size);
        if (Has(ledgerSize) && !Has(channelSize) && channelKnown)
        {
            warnings.Add(new(MatchWarnings.DimensionMissing, $"Ebat bilgisi eksik: kanalda yok, SKU'da {ledgerSize}."));
        }
        else if (Has(channelSize) && !Has(ledgerSize))
        {
            warnings.Add(new(MatchWarnings.DimensionMissing, $"Ebat bilgisi eksik: SKU'da yok, kanalda {channelSize}."));
        }
    }

    private static bool Has(string? value) => !string.IsNullOrWhiteSpace(value);

    private static bool Differs(string? channel, string? ledger) =>
        Has(channel) && Has(ledger) && CatalogText.Normalize(channel) != CatalogText.Normalize(ledger);
}

/// <summary>"Doğru eşleşme" onayı (ADR-036).</summary>
/// <param name="Valid">Onay bugünkü eşleşme bilgisini kapsıyor mu (imza aynı). false: bilgi onaydan sonra değişti, yeniden kontrol gerekir.</param>
/// <param name="ApprovedWarnings">Onaylandığı andaki uyarı kodları.</param>
public sealed record MatchApproval(string? ApprovedBy, DateTime ApprovedAtUtc, bool Valid, IReadOnlyList<string> ApprovedWarnings);

/// <summary>Kanal bazlı stok listesinde bir ilan: kanalın bildirdiği ↔ bağlandığı SKU (ADR-035).</summary>
/// <param name="ChannelStock">Son mutabakatta kanaldan çekilen stok (hiç çekilmediyse veya son çekimde kanalda bulunamadıysa null).</param>
/// <param name="Expected">Bu ilana gitmesi gereken (defterden hesaplanan, ilan stok kuralı dahil) stok.</param>
/// <param name="ImageUrl">Kanalın kendi API'sinden gelen küçük resim (yalnızca https; ADR-042). Görsel karşılaştırma içindir.</param>
/// <param name="ChannelSalePrice">Son katalog çekiminde kanalda görünen satış fiyatı (salt okuma).</param>
/// <param name="StockRuleType">İlan stok kuralı: UST_LIMIT | SABIT | null (ADR-042).</param>
/// <param name="PriceOverride">İlana özel satış fiyatı (ADR-028); null ise SKU fiyatı + kanal kuralı.</param>
/// <param name="SkuSalePrice">SKU'nun temel satış fiyatı.</param>
/// <param name="LastSentSalePrice">Kanalın onayladığı son satış fiyatı.</param>
/// <param name="ChannelStatus">Son katalog çekiminde kanalın bildirdiği ürün durumu (ham metin; ADR-043).</param>
public sealed record ChannelStockListRow(
    int ListingId, string Status, ChannelSide Channel, string? ProductUrl, int? ChannelStock, DateTime? FetchedAtUtc,
    int SkuId, string SkuCode, string? SkuBarcode, string ProductName, string? Color, string? Size,
    int Expected, int? LastSent, string MatchBasis, IReadOnlyList<MatchWarning> Warnings, MatchApproval? Approval = null,
    string? ImageUrl = null, decimal? ChannelSalePrice = null, decimal? ChannelListPrice = null,
    string? StockRuleType = null, int? StockRuleValue = null,
    decimal? PriceOverride = null, decimal? SkuSalePrice = null, decimal? LastSentSalePrice = null,
    string? ChannelStatus = null)
{
    /// <summary>Kanal durumunun sınıfı (ADR-043): SATISTA | STOK_YOK | SATIS_DISI | BILINMIYOR. Yalnızca gösterim/filtre.</summary>
    public string SaleState => PazaryeriHub.Application.Channels.ChannelSaleState.Classify(ChannelStatus);

    public bool HasDeviation => ChannelStock is { } c && c != Expected;

    /// <summary>Son mutabakatta kanalda bulunamadı (çekim var, değer yok; ADR-040). Sapma sayılmaz.</summary>
    public bool MissingInChannel => ChannelStock is null && FetchedAtUtc is not null;

    /// <summary>Uyarı var ve geçerli bir "doğru eşleşme" onayı yok (ADR-036).</summary>
    public bool NeedsReview => Warnings.Count > 0 && Approval is not { Valid: true };

    /// <summary>Onay imzası için defter tarafı (satırdaki değerlerden).</summary>
    public LedgerSide Ledger => new(SkuCode, SkuBarcode, ProductName, null, Color, Size);
}

/// <param name="UnmatchedCount">Kanalda görülen ama hiçbir SKU'ya bağlanmamış (bekleyen) ürün sayısı.</param>
/// <param name="SaleStateCounts">Kanalın tüm ilanlarının satış durumu sınıfına göre adetleri (ADR-043; filtreden bağımsız).</param>
public sealed record ChannelStockList(
    int ChannelId, string ChannelCode, PagedResult<ChannelStockListRow> Rows,
    int TotalListings, int DeviationCount, int ReviewCount, int NotFetchedCount, int UnmatchedCount, DateTime? LastFetchedUtc,
    int ApprovedCount = 0, int MissingCount = 0, IReadOnlyDictionary<string, int>? SaleStateCounts = null);

/// <summary>"Doğru eşleşme" onayı (ADR-036). Eşleştirmeyi değiştirmez; yalnızca uyarıyı kapatır.</summary>
public interface IListingMatchApprovalService
{
    /// <summary>
    /// İlanın bugünkü uyarılarını onaylar ve güncel satırı döndürür. İlan yoksa <see cref="NotFoundException"/>; uyarı yoksa
    /// <see cref="ConflictException"/>. Aynı bilgi zaten onaylıysa yeni kayıt açılmaz; eski (geçersiz) onay "YENILENDI" ile kapanır.
    /// </summary>
    Task<ChannelStockListRow> ApproveAsync(int listingId, string? user, CancellationToken ct);

    /// <summary>Açık onayı geri alır (kayıt kapanır, silinmez). Açık onay yoksa <see cref="NotFoundException"/>.</summary>
    Task<ChannelStockListRow> RevokeAsync(int listingId, string? user, CancellationToken ct);
}
