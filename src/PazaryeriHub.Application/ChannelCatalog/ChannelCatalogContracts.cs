using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Excel;

namespace PazaryeriHub.Application.ChannelCatalog;

/// <summary>
/// Satıcı panelinden indirilen kanal ürün dosyasının biçimi. Başlıklar <see cref="ExcelText.ToKey"/> ile karşılaştırılır
/// (büyük/küçük harf, boşluk ve Türkçe karakter duyarsız). <see cref="Signature"/> sütunlarının tümü varsa biçim tanınır.
/// Kişisel/firma bilgisi sütunları (Beymen: Firma Adı, E posta adresi, Adres…) tanımda yoktur, hiç okunmaz.
/// </summary>
public sealed record ChannelFileFormat(
    string Code,
    string ChannelCode,
    string DisplayName,
    IReadOnlyList<string> Signature,
    string? ChannelProductCode,
    string Barcode,
    string? StockCode,
    string? ModelCode,
    string? Color,
    string? Size,
    string? Name,
    string? Brand,
    string? Category,
    string? Status,
    string? SellerId,
    IReadOnlyList<string> PassiveStatuses)
{
    /// <summary>Metin olarak okunan, bilimsel gösterim kontrolü yapılan kod sütunları.</summary>
    public IEnumerable<string> CodeColumns => new[] { ChannelProductCode, Barcode, StockCode, ModelCode, SellerId }.OfType<string>();

    public bool IsPassive(string? channelStatus) =>
        channelStatus is not null && PassiveStatuses.Any(p => ExcelText.ToKey(p) == ExcelText.ToKey(channelStatus));
}

public static class ChannelFileFormats
{
    /// <summary>Trendyol satıcı paneli → Ürünler dışa aktarımı ("Ürünler" sayfası). Kanal ürün kimliği barkoddur.</summary>
    public static readonly ChannelFileFormat Trendyol = new(
        Code: "TRENDYOL_URUNLER",
        ChannelCode: "TRENDYOL",
        DisplayName: "Trendyol ürün listesi",
        Signature: ["Partner ID", "Barkod", "Tedarikçi Stok Kodu", "Model Kodu", "Ürün Rengi", "Beden"],
        ChannelProductCode: null,
        Barcode: "Barkod",
        StockCode: "Tedarikçi Stok Kodu",
        ModelCode: "Model Kodu",
        Color: "Ürün Rengi",
        Size: "Beden",
        Name: "Ürün Adı",
        Brand: "Marka",
        Category: "Kategori İsmi",
        Status: "Durum",
        SellerId: "Partner ID",
        PassiveStatuses: ["Ürün arşivlendi"]);

    /// <summary>
    /// Beymen satıcı paneli → Ürün Listesi. "Id" Beymen ürün kimliği (36 karakter) olarak Kanal Ürün Kodu'na yazılır.
    /// TODO: doğrulanmalı — dosyadaki "Id" ile API'deki productId'nin aynı değer olduğu resmi dokümanda açıkça yazmıyor;
    /// ilk gerçek stok gönderiminde kontrol edilmeli (BeymenAdapter stok gönderiminde productId kullanır).
    /// </summary>
    public static readonly ChannelFileFormat Beymen = new(
        Code: "BEYMEN_URUN_LISTESI",
        ChannelCode: "BEYMEN",
        DisplayName: "Beymen ürün listesi",
        Signature: ["Id", "Satış Kanalı", "Barkod", "Model Kodu", "Satıcı Renk", "Beden"],
        ChannelProductCode: "Id",
        Barcode: "Barkod",
        StockCode: "Stok Kodu",
        ModelCode: "Model Kodu",
        Color: "Satıcı Renk",
        Size: "Beden",
        Name: "Ürün Adı",
        Brand: "Marka",
        Category: "Beymen Kategori",
        Status: "Durum",
        SellerId: null,
        PassiveStatuses: ["Beymen'e Seçilmedi"]);

    public static readonly IReadOnlyList<ChannelFileFormat> All = [Trendyol, Beymen];

    /// <summary>Başlık anahtarlarına göre biçimi bulur; hiçbiri uymazsa null.</summary>
    public static ChannelFileFormat? Detect(IReadOnlyCollection<string> headerKeys) =>
        All.FirstOrDefault(f => f.Signature.All(h => headerKeys.Contains(ExcelText.ToKey(h))));

    public static string Describe() =>
        string.Join("; ", All.Select(f => $"{f.DisplayName}: {string.Join(", ", f.Signature)}"));
}

public sealed record CatalogImportOptions(bool ValidateOnly, bool CreateMissing);

public static class CatalogImportSources
{
    public const string Excel = "EXCEL";
    public const string Api = "API";
}

public static class CatalogRowOutcomes
{
    public const string Matched = "ESLESTI";
    public const string Created = "OLUSTURULDU";
    public const string NoMatch = UnmatchedReasons.NoMatch;
    public const string Ambiguous = UnmatchedReasons.Ambiguous;
    public const string Conflict = UnmatchedReasons.Conflict;
}

/// <summary>Önizleme / sonuç tablosunda tek satır.</summary>
public sealed record CatalogImportRow(
    int Row,
    string? Barcode,
    string? ChannelProductCode,
    string? StockCode,
    string? Name,
    string? Color,
    string? Size,
    string Outcome,
    string? SkuCode,
    string? Step,
    string? ListingChange,
    string? Detail,
    string? SuggestedSkuCode,
    decimal? SuggestionScore,
    bool Passive);

/// <summary>
/// Kanal kataloğu içe alma sonucu. <see cref="ValidateOnly"/> ise hiçbir şey yazılmamıştır (sayılar "yapılacak" olanlardır).
/// </summary>
public sealed record CatalogImportReport(
    string ChannelCode,
    string? Format,
    string Source,
    bool ValidateOnly,
    bool CreateMissing,
    int TotalRows,
    int ValidRows,
    int MatchedExisting,
    int MatchedByBarcode,
    int MatchedByStockCode,
    int MatchedByModelColorSize,
    int ListingsCreated,
    int ListingsUpdated,
    int ListingsUnchanged,
    int ProductsCreated,
    int SkusCreated,
    int Unmatched,
    int Ambiguous,
    int Conflicts,
    int WithSuggestion,
    int Passive,
    int PushDeferred,
    IReadOnlyList<CatalogImportRow> Rows,
    bool RowsTruncated,
    IReadOnlyList<ImportRowError> Errors,
    IReadOnlyList<string> FileErrors,
    IReadOnlyList<string> Warnings)
{
    public int ErrorRowCount => Errors.Select(e => e.Row).Distinct().Count();
}

/// <summary>Eşleşmeyen kanal ürünü (panel listesi).</summary>
public sealed record UnmatchedItemDto(
    int Id,
    int ChannelId,
    string ChannelCode,
    string Source,
    string? ChannelProductCode,
    string? ChannelBarcode,
    string? ChannelStockCode,
    string? ModelCode,
    string? Color,
    string? Size,
    string? ProductName,
    string? Brand,
    string? ChannelStatus,
    string Reason,
    string? ReasonDetail,
    int? SuggestedSkuId,
    string? SuggestedSkuCode,
    string? SuggestedProductName,
    decimal? SuggestionScore,
    string Status,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    string? ResolvedBy,
    DateTime? ResolvedAtUtc);

public static class UnmatchedStatuses
{
    public const string Pending = "BEKLIYOR";
    public const string Linked = "BAGLANDI";
    public const string Ignored = "YOKSAYILDI";
    public static readonly IReadOnlyList<string> All = [Pending, Linked, Ignored];
}

public sealed record LinkUnmatchedRequest(int SkuId);

public interface IChannelCatalogService
{
    /// <summary>Kanal ürün Excel'ini okur, eşleştirir ve (validateOnly değilse) uygular.</summary>
    Task<CatalogImportReport> ImportFileAsync(int channelId, Stream content, string? fileName, CatalogImportOptions options, string? user, CancellationToken ct);

    /// <summary>Normalize edilmiş kanal ürünlerini eşleştirir ve uygular (Excel ve API kaynağı aynı yoldan geçer).</summary>
    Task<CatalogImportReport> ApplyAsync(int channelId, IReadOnlyList<ChannelCatalogItem> items, string source, CatalogImportOptions options, string? user,
        IReadOnlyList<ImportRowError>? errors, IReadOnlyList<string>? warnings, int? totalRows, string? format, CancellationToken ct);

    Task<PagedResult<UnmatchedItemDto>> SearchUnmatchedAsync(int? channelId, string? status, string? search, int? page, int? pageSize, CancellationToken ct);

    /// <summary>Eşleşmeyen ürünü seçilen SKU'ya elle bağlar (öneri yalnızca ön seçimdir). Çakışmada <see cref="ConflictException"/>.</summary>
    Task<ListingDto> LinkUnmatchedAsync(int id, int skuId, string? user, CancellationToken ct);

    Task IgnoreUnmatchedAsync(int id, string? user, CancellationToken ct);
}
