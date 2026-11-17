using PazaryeriHub.Domain.Orders;

namespace PazaryeriHub.Application.Channels;

/// <summary>
/// Kanal adaptörü sözleşmesi (CLAUDE.md §6). Çekirdek kanalı tanımaz; her kanal bu arayüzü uygular.
/// Kimlik doğrulama, hız limiti, sayfalama ve hata çevirisi adaptörün içinde çözülür.
/// Bütün çağrılar Hangfire job'larından yapılır (ilke 5).
/// </summary>
public interface IChannelAdapter
{
    /// <summary>TRENDYOL | HEPSIBURADA | BEYMEN | SOFTCOTTON</summary>
    string ChannelCode { get; }

    /// <summary><paramref name="sinceUtc"/> sonrasında oluşturulan veya güncellenen siparişler.</summary>
    Task<IReadOnlyList<NormalizedOrder>> FetchOrdersAsync(DateTime sinceUtc, CancellationToken ct);

    Task<IReadOnlyList<ChannelStock>> FetchStockAsync(IEnumerable<string>? skus, CancellationToken ct);
    Task<BatchResult> PushStockAsync(IReadOnlyList<StockUpdate> updates, CancellationToken ct);
    Task<BatchResult> PushPriceAsync(IReadOnlyList<PriceUpdate> updates, CancellationToken ct);
    Task<BatchStatus> CheckBatchAsync(string batchId, CancellationToken ct);

    /// <summary>
    /// Sipariş durumu / kargo bildirimi (ADR-026). Spesifikasyon §6'daki <c>(channelOrderNo, status, cargo)</c> imzasından
    /// SAPMA: kanallar satır ve paket kimliği istediği için istek nesnesi alır. Kanala yazar → yalnızca KanalKomutGonder
    /// job'ından ve yazma kapısından (GuardedChannelAdapter) geçerek çağrılır.
    /// </summary>
    Task UpdateOrderStatusAsync(OrderStatusUpdate update, CancellationToken ct);

    /// <summary>Bu kanalda hedef duruma bildirim yapılabilir mi, hangi alanlar zorunlu (statik; kanal çağrısı yok).</summary>
    /// <param name="ownCargo">Sipariş kanalın anlaşmalı kargosu yerine kendi kargomuzla mı gönderiliyor.</param>
    OrderNotificationRule DescribeOrderNotification(OrderStatus target, bool ownCargo);
    Task<IReadOnlyList<NormalizedReturn>> FetchReturnsAsync(DateTime sinceUtc, CancellationToken ct);

    /// <summary>
    /// Kanaldaki ürün kataloğu (sayfalı; ADR-022). <paramref name="cursor"/> null → ilk sayfa; dönen
    /// <see cref="ChannelCatalogPage.NextCursor"/> null ise son sayfadır. İmleç adaptöre özeldir (opak).
    /// Yalnızca resmi dokümanla doğrulanmış alanlar doldurulur; kanal vermiyorsa alan null kalır.
    /// </summary>
    Task<ChannelCatalogPage> FetchCatalogAsync(string? cursor, CancellationToken ct);

    /// <summary>
    /// Satıcı Bilgileri: kanal referans listesi (ADR-025; spesifikasyon §6'ya EK). Salt okumadır.
    /// Kanal bu listeyi doğrulanmış bir servis veya doküman listesiyle vermiyorsa <see cref="NotSupportedException"/> fırlatır.
    /// </summary>
    Task<ChannelReferenceList> FetchReferenceAsync(ChannelReferenceType type, CancellationToken ct);
}

/// <summary>Kanal kataloğundaki tek ürün (varyant). Alanlar kanalın verdiği kadar doludur.</summary>
/// <param name="ChannelProductCode">Kanalın ürün kimliği (Hepsiburada HBSKU, Beymen productId). Trendyol'da barkod kimliktir → null.</param>
/// <param name="StockCode">Satıcının kendi stok kodu (Hepsiburada merchantSku, Trendyol stockCode…).</param>
/// <param name="Passive">Kanalda kapalı / arşivlenmiş.</param>
public sealed record ChannelCatalogEntry(
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
    bool Passive);

public sealed record ChannelCatalogPage(IReadOnlyList<ChannelCatalogEntry> Items, string? NextCursor);

/// <summary>Kanaldan gelen siparişin kanal bağımsız hali.</summary>
/// <param name="ChannelOrderNo">Kanal sipariş numarası (idempotentlik anahtarı, büyük/küçük harf duyarlı).</param>
/// <param name="Status">Kanaldaki durumun normalize edilmiş karşılığı.</param>
/// <param name="Customer">KVKK: yalnızca gerekli özet (ad soyad, il, ilçe). Telefon/e-posta/açık adres TAŞINMAZ.</param>
public sealed record NormalizedOrder(
    string ChannelOrderNo,
    DateTime OrderDateUtc,
    OrderStatus Status,
    decimal TotalAmount,
    IReadOnlyList<NormalizedOrderLine> Lines,
    CustomerSummary? Customer = null,
    CargoInfo? Cargo = null,
    string? ChannelPackageId = null);

/// <summary>Sipariş satırı. SKU eşleştirmesi sırayla: kanal barkodu, kanal ürün kodu (ChannelListing), SKU barkodu, satıcı SKU kodu.</summary>
public sealed record NormalizedOrderLine(
    string? ChannelProductCode,
    string? Barcode,
    string? SellerSku,
    int Quantity,
    decimal UnitPrice,
    decimal? Commission = null,
    string? ChannelLineId = null);

public sealed record CustomerSummary(string? FullName, string? City, string? District);

public sealed record CargoInfo(string? Company, string? TrackingNumber, string? TrackingUrl);

/// <summary>Kanala durum bildirimi (ADR-026).</summary>
/// <param name="ChannelPackageId">Kanalın paket/sipariş kimliği (Trendyol shipmentPackageId, Beymen sipariş id GUID).</param>
/// <param name="Lines">Kanal satır kimlikleri ve adet (Trendyol lineId, Hepsiburada kalem id).</param>
/// <param name="OwnCargo">Kendi kargomuzla gönderim (Kargoda bildiriminde takip bilgisi bizden gider).</param>
public sealed record OrderStatusUpdate(
    string ChannelOrderNo, string? ChannelPackageId, OrderStatus Status, IReadOnlyList<OrderStatusUpdateLine> Lines, CargoInfo? Cargo, bool OwnCargo);

public sealed record OrderStatusUpdateLine(string ChannelLineId, int Quantity);

/// <summary>Kanalın bir hedef durum için bildirim kuralı.</summary>
/// <param name="Supported">Bildirim bu kanalda uygulanmış ve dokümanla doğrulanmış.</param>
/// <param name="NotRequired">Kanal bu durumu kendisi günceller (ör. anlaşmalı kargo teslim alınca Kargoda); bildirim yapılmaz.</param>
public sealed record OrderNotificationRule(
    bool Supported,
    bool NotRequired,
    string? Note,
    bool RequiresPackageId = false,
    bool RequiresLineIds = false,
    bool RequiresTrackingNumber = false,
    bool RequiresTrackingUrl = false,
    bool RequiresCargoCompany = false)
{
    public static OrderNotificationRule NotSupportedBecause(string note) => new(false, false, note);
    public static OrderNotificationRule NotRequiredBecause(string note) => new(false, true, note);
}

/// <summary>Kanalda görünen stok (mutabakat için).</summary>
public sealed record ChannelStock(string? ChannelProductCode, string? Barcode, int Quantity);

public sealed record StockUpdate(string? ChannelProductCode, string? Barcode, int Quantity);

public sealed record PriceUpdate(string? ChannelProductCode, string? Barcode, decimal ListPrice, decimal SalePrice);

/// <summary>Toplu gönderim sonucu. Kanal işi asenkron işliyorsa <see cref="BatchId"/> ile sonradan sorgulanır.</summary>
public sealed record BatchResult(string? BatchId, bool Accepted, string? Error = null);

public enum BatchState
{
    Pending,
    Completed,
    PartiallyFailed,
    Failed
}

public sealed record BatchItemError(string? ChannelProductCode, string? Barcode, string Message);

public sealed record BatchStatus(string BatchId, BatchState State, IReadOnlyList<BatchItemError> Errors);

public sealed record NormalizedReturn(
    string ChannelOrderNo,
    string? ReturnNo,
    DateTime CreatedUtc,
    IReadOnlyList<NormalizedReturnLine> Lines,
    string? Reason = null);

public sealed record NormalizedReturnLine(string? ChannelProductCode, string? Barcode, int Quantity);
