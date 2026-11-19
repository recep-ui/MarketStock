using PazaryeriHub.Application.Common;

namespace PazaryeriHub.Application.Panel;

public sealed record ChannelRevenue(string ChannelCode, int Orders, decimal Revenue);

public sealed record CriticalStockItem(int SkuId, string SkuCode, string ProductName, int Physical, int Reserved, int Available);

public sealed record JobStateItem(string ChannelCode, string JobType, DateTime? LastSuccessUtc, DateTime? LastAttemptUtc, int ConsecutiveFailures, string? LastError)
{
    /// <summary>Hatanın türü ve ne yapılacağı (ADR-033); iş son çalışmada başarılıysa null.</summary>
    public PazaryeriHub.Application.Channels.ChannelErrorInfo? ErrorInfo =>
        ConsecutiveFailures > 0 ? PazaryeriHub.Application.Channels.ChannelErrorExplainer.Explain(LastError) : null;
}

/// <summary>Genel Bakış: bugünkü siparişler (Türkiye günü), kanal bazlı ciro, kritik stok, hatalı senkronlar.</summary>
public sealed record DashboardDto(
    DateOnly Day,
    int TodayOrders,
    decimal TodayRevenue,
    IReadOnlyList<ChannelRevenue> ByChannel,
    int InsufficientStockOrders,
    int CriticalStockThreshold,
    IReadOnlyList<CriticalStockItem> CriticalStock,
    int SyncErrors,
    int SyncPending,
    IReadOnlyList<JobStateItem> Jobs);

/// <summary>Stok listesinde bir kanal hücresi.</summary>
/// <param name="Expected">Kanala gitmesi gereken (hesaplanan) stok.</param>
/// <param name="ChannelStock">Son mutabakatta kanalda görülen stok (hiç mutabakat yoksa null).</param>
/// <summary>Bir ilanın kanal stoğu. Aynı SKU'nun aynı kanalda birden fazla ilanı olabilir (ADR-023): her ilan ayrı hücre.</summary>
/// <param name="ProductUrl">Ürünün kanaldaki sayfası (kanal API'si veriyorsa; ADR-031).</param>
public sealed record ChannelStockCell(
    string ChannelCode, int ListingId, string Status, int Expected, int? LastSent, int? ChannelStock, DateTime? FetchedAtUtc,
    string? ChannelBarcode = null, string? ChannelProductCode = null, string? ProductUrl = null, string? Variant = null)
{
    public bool HasDeviation => ChannelStock is { } c && c != Expected;
}

public sealed record StockOverviewRow(
    int SkuId, string SkuCode, string ProductName, int Physical, int Reserved, int SafetyStock, int Available,
    IReadOnlyList<ChannelStockCell> Channels)
{
    public bool HasDeviation => Channels.Any(c => c.HasDeviation);
}

public sealed record StockMovementItem(
    long Id, DateTime AtUtc, int SkuId, string SkuCode, string WarehouseName, string Type, int Quantity,
    string? SourceType, string? SourceId, string? User);

public sealed record MovementQuery(
    string? Sku = null, string? Type = null, int? WarehouseId = null, DateTime? FromUtc = null, DateTime? ToUtc = null,
    string? SourceType = null, int? Page = null, int? PageSize = null);

/// <param name="CriticalStockThreshold">Panel "kritik stok" uyarı eşiği (yalnızca uyarı).</param>
/// <param name="LowStockThreshold">
/// Genel düşük stok eşiği (ADR-023): satılabilir &lt;= değer ise TÜM kanallara 0 gönderilir. null → değiştirilmez.
/// </param>
public sealed record AppSettingsDto(int CriticalStockThreshold, int? LowStockThreshold = null);

public interface IPanelQueryService
{
    Task<DashboardDto> GetDashboardAsync(CancellationToken ct);

    /// <param name="onlyDeviations">Yalnızca en az bir kanalda sapması olan satırlar.</param>
    /// <param name="lowStock">Yalnızca satılabilir stoğu kritik eşiğin altında/eşit olanlar.</param>
    Task<PagedResult<StockOverviewRow>> GetStockOverviewAsync(string? search, bool onlyDeviations, bool lowStock, int? page, int? pageSize, CancellationToken ct);

    Task<PagedResult<StockMovementItem>> GetMovementsAsync(MovementQuery query, CancellationToken ct);

    /// <summary>
    /// Bir kanalın stok listesi (ADR-035): kanalın bildirdiği kod/ad/stok ↔ bağlandığı SKU, yanlış eşleştirme uyarılarıyla.
    /// Kanal yoksa <see cref="NotFoundException"/>.
    /// </summary>
    Task<ChannelStockList> GetChannelStockListAsync(
        int channelId, string? search, bool onlyDeviations, bool onlyReview, int? page, int? pageSize, CancellationToken ct);

    /// <summary>SKU detay ekranı (ADR-034). SKU yoksa <see cref="NotFoundException"/>.</summary>
    Task<SkuDetail> GetSkuDetailAsync(int skuId, CancellationToken ct);

    Task<AppSettingsDto> GetSettingsAsync(CancellationToken ct);
    Task<AppSettingsDto> UpdateSettingsAsync(AppSettingsDto settings, CancellationToken ct);
}
