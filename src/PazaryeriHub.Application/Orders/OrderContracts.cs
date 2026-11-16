using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Domain.Orders;

namespace PazaryeriHub.Application.Orders;

/// <summary>Geçersiz durum geçişi (HTTP 409). Sipariş değişmez.</summary>
public sealed class InvalidOrderTransitionException(OrderStatus from, OrderStatus to)
    : Exception($"Sipariş durumu '{from}' → '{to}' geçişine izin verilmiyor. İzinli: {string.Join(", ", OrderStateMachine.NextStates(from))}.")
{
    public OrderStatus From { get; } = from;
    public OrderStatus To { get; } = to;
}

/// <summary>Bir siparişin işlenme sonucu.</summary>
/// <param name="Created">Yeni kayıt mı (false: daha önce kaydedilmişti, idempotent tekrar).</param>
/// <param name="StatusChanged">Kanaldaki durum değişikliği uygulandı mı.</param>
/// <param name="RejectedTransition">Kanaldan geçersiz bir durum geldiyse açıklaması (sipariş değişmedi).</param>
public sealed record IngestResult(long OrderId, bool Created, bool StatusChanged, string? RejectedTransition, bool InsufficientStock, int UnmatchedLines);

public interface IOrderIngestionService
{
    /// <summary>
    /// Kanal siparişini idempotent kaydeder: aynı (kanal, sipariş no) ikinci kez gelirse yeni kayıt açılmaz;
    /// kanaldaki durum ilerlemişse geçiş uygulanır; stok etkileri (rezerve/sevk/iptal) her satır için en fazla bir kez yapılır.
    /// Stok yetersizse sipariş reddedilmez, <c>StokYetersiz</c> bayrağıyla kaydedilir (ilke 7).
    /// </summary>
    Task<IngestResult> IngestAsync(string channelCode, NormalizedOrder order, CancellationToken ct);
}

public sealed record OrderListItem(
    long Id, int ChannelId, string ChannelCode, string ChannelOrderNo, OrderStatus Status, decimal Total,
    DateTime OrderDateUtc, bool InsufficientStock, int LineCount, int UnmatchedLineCount);

public sealed record OrderLineDto(
    long Id, int? SkuId, string? SkuCode, string? ProductName, int Quantity, decimal UnitPrice, decimal? Commission,
    bool Reserved, bool Shipped, bool Released);

public sealed record OrderEventDto(long Id, OrderStatus? From, OrderStatus To, string Source, DateTime AtUtc);

public sealed record OrderDetail(
    long Id, int ChannelId, string ChannelCode, string ChannelOrderNo, OrderStatus Status, decimal Total,
    DateTime OrderDateUtc, bool InsufficientStock, CustomerSummary? Customer, CargoInfo? Cargo,
    IReadOnlyList<OrderLineDto> Lines, IReadOnlyList<OrderEventDto> Timeline);

public sealed record OrderQuery(
    int? ChannelId = null,
    OrderStatus? Status = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    string? Sku = null,
    bool? InsufficientStock = null,
    string? Search = null,
    int? Page = null,
    int? PageSize = null);

public interface IOrderService
{
    Task<PagedResult<OrderListItem>> SearchAsync(OrderQuery query, CancellationToken ct);
    Task<OrderDetail> GetAsync(long id, CancellationToken ct);

    /// <summary>Panelden durum değişikliği: yalnızca tek adımlık izinli geçiş (aksi halde <see cref="InvalidOrderTransitionException"/>).</summary>
    Task<OrderDetail> ChangeStatusAsync(long id, OrderStatus target, string source, CancellationToken ct);
}

/// <summary>Kanal job'u sonucu (SiparisCek).</summary>
public sealed record OrderFetchResult(
    string ChannelCode, bool Skipped, string? SkipReason, DateTime? WindowStartUtc, int Fetched, int Created, int Updated,
    int Failed, int InsufficientStock);
