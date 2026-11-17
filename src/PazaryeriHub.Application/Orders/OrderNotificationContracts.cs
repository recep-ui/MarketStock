using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Domain.Orders;

namespace PazaryeriHub.Application.Orders;

/// <summary>Kanal başına sipariş durumu bildirim modu (Channel.Ayarlar.bildirimModu; ADR-026).</summary>
public static class NotificationModes
{
    /// <summary>Varsayılan: Depo durumu değiştirir, ardından seçilen siparişler için ayrıca "Kanala bildir" der.</summary>
    public const string ConfirmAndSend = "ONAYLA_GONDER";

    /// <summary>Panel geçişi bildirimi kendiliğinden kuyruğa alır (gerçek denemelerden sonra kanal bazında açılır).</summary>
    public const string Automatic = "OTOMATIK";

    public static IReadOnlyList<string> All { get; } = [ConfirmAndSend, Automatic];
}

public static class NotifyOutcomes
{
    public const string Queued = "KUYRUGA_ALINDI";
    public const string AwaitingApproval = "ONAY_BEKLIYOR";
    public const string AlreadyExists = "ZATEN_VAR";
    public const string NotRequired = "GEREKMIYOR";
    public const string NotSupported = "DESTEKLENMIYOR";
    public const string Rejected = "REDDEDILDI";
}

public sealed record CargoInput(string? Company, string? TrackingNumber, string? TrackingUrl);

/// <param name="Target">Boşsa siparişin şu anki durumu (Hazırlanıyor veya Kargoda olmalı).</param>
/// <param name="OwnCargo">Kendi kargomuzla gönderim (Kargoda bildiriminde takip bilgisi bizden gider).</param>
public sealed record OrderNotifyRequest(IReadOnlyList<long> OrderIds, OrderStatus? Target, bool OwnCargo, CargoInput? Cargo);

public sealed record OrderNotifyItemResult(long OrderId, string? ChannelOrderNo, string Outcome, long? CommandId, string? Message);

public sealed record OrderNotifyResult(IReadOnlyList<OrderNotifyItemResult> Items);

/// <summary>Bildirim listesi satırı. <paramref name="NotificationState"/>: null = kanala bildirilmedi; aksi halde komut durumu.</summary>
public sealed record OrderNotificationRow(
    long OrderId, int ChannelId, string ChannelCode, string ChannelOrderNo, OrderStatus Status, DateTime OrderDateUtc, string StatusSource,
    long? CommandId, string? NotificationState, string? Error, bool HasChannelIds);

public sealed record OrderNotificationRuleDto(string Status, bool OwnCargo, OrderNotificationRule Rule);

public interface IOrderNotificationService
{
    /// <param name="filter">BILDIRILMEDI (varsayılan) | HATA | BEKLEYEN | TUMU</param>
    /// <param name="search">Kanal sipariş no (içerir).</param>
    Task<PagedResult<OrderNotificationRow>> SearchAsync(int? channelId, string? filter, string? search, int? page, int? pageSize, CancellationToken ct);

    Task<OrderNotifyResult> NotifyAsync(OrderNotifyRequest request, string? user, CancellationToken ct);

    /// <summary>OTOMATIK moddaki kanalda panel geçişinden sonra çağrılır; ONAYLA_GONDER modunda hiçbir şey yapmaz.</summary>
    Task<OrderNotifyItemResult?> OnPanelTransitionAsync(long orderId, OrderStatus target, string? user, CancellationToken ct);

    Task<IReadOnlyList<ChannelCommandDto>> GetForOrderAsync(long orderId, CancellationToken ct);

    Task<IReadOnlyList<OrderNotificationRuleDto>> GetRulesAsync(int channelId, CancellationToken ct);

    Task<string> GetModeAsync(int channelId, CancellationToken ct);

    Task SetModeAsync(int channelId, string mode, string? user, CancellationToken ct);
}
