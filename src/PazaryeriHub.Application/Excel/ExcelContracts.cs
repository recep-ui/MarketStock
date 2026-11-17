using System.Text;
using PazaryeriHub.Application.Catalog;

namespace PazaryeriHub.Application.Excel;

/// <summary>Excel içe/dışa aktarma türü.</summary>
public enum ExcelKind
{
    /// <summary>Ürün + SKU (SKU koduna göre ekle/güncelle).</summary>
    Products,

    /// <summary>Stok girişi / sayım (her satır bir defter hareketi).</summary>
    Stock,

    /// <summary>SKU ↔ kanal ürün kodu eşleştirmesi.</summary>
    Listings
}

/// <param name="Row">Excel satır numarası (başlık 1. satır, veri 2. satırdan başlar).</param>
public sealed record ImportRowError(int Row, string? Column, string Message);

/// <summary>İçe aktarma sonucu. <see cref="ValidateOnly"/> ise hiçbir şey yazılmamıştır; sayılar tahmindir.</summary>
public sealed record ImportReport(
    ExcelKind Kind,
    bool ValidateOnly,
    int TotalRows,
    int ValidRows,
    int Created,
    int Updated,
    int Unchanged,
    IReadOnlyList<ImportRowError> Errors,
    IReadOnlyList<string> FileErrors)
{
    /// <summary>Veritabanına yazılan (veya validateOnly'de yazılacak) satır sayısı.</summary>
    public int Applied => Created + Updated;

    public int ErrorRowCount => Errors.Select(e => e.Row).Distinct().Count();
}

public interface IExcelTransferService
{
    /// <summary>Boş şablon (başlık satırı + "Aciklama" sayfası).</summary>
    byte[] CreateTemplate(ExcelKind kind);

    /// <summary>Mevcut veriyi şablonla aynı sütunlarda dışa aktarır (+ bilgi sütunları; içe aktarmada yok sayılır).</summary>
    Task<byte[]> ExportAsync(ExcelKind kind, CancellationToken ct);

    /// <summary>
    /// Dosyayı okur, tüm satırları doğrular ve yalnızca geçerli satırları uygular. Hatalı satırlar satır numarasıyla raporlanır.
    /// <paramref name="validateOnly"/> true ise hiçbir şey yazılmaz.
    /// </summary>
    Task<ImportReport> ImportAsync(ExcelKind kind, Stream content, string? fileName, bool validateOnly, string? user, CancellationToken ct);
}

/// <summary>Şablon sütunu.</summary>
public sealed record ExcelColumn(string Header, bool Required, int? MaxLength, string Description, string Example)
{
    public string Key { get; } = ExcelText.ToKey(Header);
}

/// <summary>Şablon sütun tanımları. Başlıklar Türkçe; eşleştirme büyük/küçük harf, boşluk ve Türkçe karakter duyarsızdır.</summary>
public static class ExcelLayouts
{
    public const int MaxDataRows = 20_000;
    public const long MaxFileBytes = 10 * 1024 * 1024;

    public static readonly ExcelColumn SkuCode = new("SKU Kodu", true, CatalogLimits.SkuCode, "Benzersiz stok kodu. Büyük/küçük harf duyarlıdır.", "SC-TSHIRT-BYZ-M");

    public static readonly ExcelColumn[] Products =
    [
        SkuCode,
        new("Ürün Adı", true, CatalogLimits.ProductName, "Ürün adı. Aynı Ürün Adı + Marka'ya sahip satırlar aynı ürünün varyantlarıdır.", "Basic Tişört"),
        new("Marka", false, CatalogLimits.Brand, "Marka.", "Softcotton"),
        new("Kategori", false, CatalogLimits.Category, "Kategori.", "Tişört"),
        new("Barkod", false, CatalogLimits.Barcode, "Benzersiz barkod (boş bırakılabilir). Hücre metin biçiminde olmalı.", "8690000000017"),
        new("Renk", false, CatalogLimits.Color, "Renk.", "Beyaz"),
        new("Beden", false, CatalogLimits.Size, "Beden.", "M"),
        new("Model Kodu", false, CatalogLimits.ModelCode, "Kanallardaki model kodu (ürün grubu). Boş bırakılırsa mevcut değer silinmez.", "SC-TSHIRT")
    ];

    public static readonly ExcelColumn[] Stock =
    [
        SkuCode,
        new("Depo", false, CatalogLimits.WarehouseName, "Depo adı. Boşsa 'Ana Depo'.", "Ana Depo"),
        new("İşlem", true, 10, "GIRIS: mevcut stoğa ekler (dosyayı iki kez yüklemek iki kez ekler!). SAYIM: fiziksel stoğu bu adede eşitler (tekrar yüklemek güvenlidir).", "SAYIM"),
        new("Adet", true, null, "Tam sayı. GIRIS için > 0, SAYIM için >= 0.", "25")
    ];

    public static readonly ExcelColumn[] Listings =
    [
        SkuCode,
        new("Kanal", true, 20, "TRENDYOL, HEPSIBURADA, BEYMEN veya SOFTCOTTON.", "TRENDYOL"),
        new("Kanal Ürün Kodu", false, CatalogLimits.ChannelProductCode, "Kanaldaki ürün/stok kodu. Kanal Barkod ile en az biri zorunlu.", "TY-123456"),
        new("Kanal Barkod", false, CatalogLimits.ChannelBarcode, "Kanaldaki barkod.", "8690000000017"),
        new("Durum", false, 10, "AKTIF veya PASIF. Boşsa AKTIF.", "AKTIF")
    ];

    public static ExcelColumn[] For(ExcelKind kind) => kind switch
    {
        ExcelKind.Products => Products,
        ExcelKind.Stock => Stock,
        ExcelKind.Listings => Listings,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public static string SheetName(ExcelKind kind) => kind switch
    {
        ExcelKind.Products => "Urunler",
        ExcelKind.Stock => "Stok",
        ExcelKind.Listings => "Eslestirme",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}

/// <summary>Excel'den okunan ham satır. Değerler sütun anahtarına (<see cref="ExcelColumn.Key"/>) göre, kırpılmış ve boşsa null.</summary>
public sealed record RawRow(int Row, IReadOnlyDictionary<string, string?> Values)
{
    public string? this[ExcelColumn column] => Values.TryGetValue(column.Key, out var v) ? v : null;
}

public static class ExcelText
{
    /// <summary>
    /// Başlık ve kod benzeri değerleri karşılaştırma anahtarına çevirir: Türkçe karakterler ASCII'ye, büyük harfe;
    /// boşluk, '*', '_', '-' atılır. Örn. "Ürün Adı *" → "URUNADI", "giriş" → "GIRIS".
    /// </summary>
    public static string ToKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            var mapped = ch switch
            {
                'ı' or 'İ' or 'i' or 'I' => 'I',
                'ş' or 'Ş' => 'S',
                'ğ' or 'Ğ' => 'G',
                'ü' or 'Ü' => 'U',
                'ö' or 'Ö' => 'O',
                'ç' or 'Ç' => 'C',
                _ => char.ToUpperInvariant(ch)
            };
            if (mapped is not (' ' or '*' or '_' or '-'))
            {
                sb.Append(mapped);
            }
        }
        return sb.ToString();
    }
}
