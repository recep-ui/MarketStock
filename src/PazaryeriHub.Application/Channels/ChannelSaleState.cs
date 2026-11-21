using PazaryeriHub.Application.Excel;

namespace PazaryeriHub.Application.Channels;

/// <summary>
/// Kanalın bildirdiği ürün durumu (ham metin, KanalVerisi.kanalDurumu) → satış durumu sınıfı (ADR-043). Saf fonksiyon.
/// Yalnızca gerçek kanal yanıtlarında görülen metinler tanınır; tanınmayan metin <see cref="Unknown"/> sayılır (tahmin yapılmaz).
/// Yalnızca panelde gösterim ve filtre içindir: ilan durumunu (AKTIF/PASIF) ve stok gönderimini değiştirmez.
/// </summary>
public static class ChannelSaleState
{
    /// <summary>Kanalda satışta.</summary>
    public const string OnSale = "SATISTA";

    /// <summary>İlan açık ama kanalda stoğu yok (stok gönderilince satışa döner).</summary>
    public const string OutOfStock = "STOK_YOK";

    /// <summary>Kanalda satış dışı: satışa kapalı, arşiv, kara liste, kilitli, seçilmedi, revize bekliyor.</summary>
    public const string OffSale = "SATIS_DISI";

    /// <summary>Kanal durum bildirmedi veya metin tanınmıyor.</summary>
    public const string Unknown = "BILINMIYOR";

    public static IReadOnlyList<string> All { get; } = [OnSale, OutOfStock, OffSale, Unknown];

    public static bool IsValid(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);

    // Kaynaklar: TrendyolAdapter.ToCatalogEntries (archived/blacklisted/locked/onSale → metin), Trendyol Excel "Durum" sütunu,
    // Beymen GET /products status.localizedDescription (deneme modu katalog okuması 2026-11-21).
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        [ExcelText.ToKey("Satışta")] = OnSale,
        [ExcelText.ToKey("Stoğu Tükendi")] = OutOfStock,
        [ExcelText.ToKey("Satışta değil")] = OffSale,
        [ExcelText.ToKey("Kara listede")] = OffSale,
        [ExcelText.ToKey("Kilitli")] = OffSale,
        [ExcelText.ToKey("Arşivlendi")] = OffSale,
        [ExcelText.ToKey("Ürün arşivlendi")] = OffSale,
        [ExcelText.ToKey("Beymen'e Seçilmedi")] = OffSale,
        [ExcelText.ToKey("Revize Bekleniyor")] = OffSale,
    };

    public static string Classify(string? channelStatus)
    {
        var key = ExcelText.ToKey(channelStatus);
        return key.Length > 0 && Known.TryGetValue(key, out var state) ? state : Unknown;
    }
}
