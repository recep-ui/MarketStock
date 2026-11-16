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
    Task UpdateOrderStatusAsync(string channelOrderNo, OrderStatus status, CargoInfo? cargo, CancellationToken ct);
    Task<IReadOnlyList<NormalizedReturn>> FetchReturnsAsync(DateTime sinceUtc, CancellationToken ct);
}

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
    CargoInfo? Cargo = null);

/// <summary>Sipariş satırı. SKU eşleştirmesi sırayla: kanal barkodu, kanal ürün kodu (ChannelListing), SKU barkodu, satıcı SKU kodu.</summary>
public sealed record NormalizedOrderLine(
    string? ChannelProductCode,
    string? Barcode,
    string? SellerSku,
    int Quantity,
    decimal UnitPrice,
    decimal? Commission = null);

public sealed record CustomerSummary(string? FullName, string? City, string? District);

public sealed record CargoInfo(string? Company, string? TrackingNumber, string? TrackingUrl);

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
