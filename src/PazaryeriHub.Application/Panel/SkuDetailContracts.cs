using PazaryeriHub.Domain.Orders;

namespace PazaryeriHub.Application.Panel;

public sealed record WarehouseBalance(int WarehouseId, string WarehouseName, int Physical, int Reserved, int SafetyStock, int Available);

/// <summary>SKU satış özeti. Net = İptal ve İade dışındaki siparişler (ADR-034).</summary>
public sealed record SkuSalesSummary(int Units7Days, int Units30Days, int Orders30Days, decimal Revenue30Days, DateTime? LastSoldUtc);

public sealed record SkuOrderItem(
    long OrderId, string ChannelCode, string ChannelOrderNo, OrderStatus Status, DateTime OrderDateUtc, int Quantity, decimal UnitPrice, bool InsufficientStock);

/// <summary>SKU detay ekranı: ürün bilgisi, depo bakiyeleri, kanal ilanları, fiyat, satış özeti, son hareketler ve siparişler.</summary>
public sealed record SkuDetail(
    int SkuId, string SkuCode, string? Barcode, int ProductId, string ProductName, string? Brand, string? Category, string? Color, string? Size,
    decimal? ListPrice, decimal? SalePrice,
    int Physical, int Reserved, int SafetyStock, int Available,
    IReadOnlyList<WarehouseBalance> Warehouses,
    IReadOnlyList<ChannelStockCell> Channels,
    SkuSalesSummary Sales,
    IReadOnlyList<StockMovementItem> RecentMovements,
    IReadOnlyList<SkuOrderItem> RecentOrders)
{
    public bool HasDeviation => Channels.Any(c => c.HasDeviation);
}
