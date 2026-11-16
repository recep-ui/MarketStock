using PazaryeriHub.Application.Common;

namespace PazaryeriHub.Application.Catalog;

/// <summary>Alan uzunluk sınırları (veritabanı kolonlarıyla aynı).</summary>
public static class CatalogLimits
{
    public const int ProductName = 300;
    public const int Brand = 100;
    public const int Category = 200;
    public const int SkuCode = 100;
    public const int Barcode = 50;
    public const int Color = 50;
    public const int Size = 30;
    public const int WarehouseName = 100;
    public const int ChannelName = 100;
    public const int ChannelProductCode = 100;
    public const int ChannelBarcode = 50;
}

public sealed record ProductInput(string Name, string? Brand, string? Category);

public sealed record SkuInput(string SkuCode, string? Barcode, string? Color, string? Size);

public sealed record CreateProductRequest(string Name, string? Brand, string? Category, IReadOnlyList<SkuInput>? Skus);

public sealed record SkuDto(int Id, int ProductId, string SkuCode, string? Barcode, string? Color, string? Size);

public sealed record ProductDto(int Id, string Name, string? Brand, string? Category, DateTime CreatedAtUtc, IReadOnlyList<SkuDto> Skus);

/// <summary>SKU listesi satırı: ürün bilgisi + tüm depoların stok toplamı.</summary>
public sealed record SkuListItem(
    int Id, string SkuCode, string? Barcode, string? Color, string? Size,
    int ProductId, string ProductName, string? Brand,
    int Physical, int Reserved, int SafetyStock, int Available);

public sealed record WarehouseDto(int Id, string Name);

public interface ICatalogService
{
    Task<PagedResult<ProductDto>> SearchProductsAsync(string? search, int? page, int? pageSize, CancellationToken ct);
    Task<ProductDto> GetProductAsync(int id, CancellationToken ct);
    Task<ProductDto> CreateProductAsync(CreateProductRequest request, CancellationToken ct);
    Task<ProductDto> UpdateProductAsync(int id, ProductInput input, CancellationToken ct);

    /// <summary>Ürünü ve SKU'larını siler. SKU'lardan birine stok/hareket/sipariş/eşleştirme bağlıysa <see cref="ConflictException"/>.</summary>
    Task DeleteProductAsync(int id, CancellationToken ct);

    Task<PagedResult<SkuListItem>> SearchSkusAsync(string? search, int? page, int? pageSize, CancellationToken ct);
    Task<SkuDto> AddSkuAsync(int productId, SkuInput input, CancellationToken ct);
    Task<SkuDto> UpdateSkuAsync(int skuId, SkuInput input, CancellationToken ct);
    Task DeleteSkuAsync(int skuId, CancellationToken ct);

    /// <summary>Tek depo modeli (ADR-015): seed'deki "Ana Depo". Yeni depo ekleme kapalı; şema çoklu depoya hazır.</summary>
    Task<IReadOnlyList<WarehouseDto>> GetWarehousesAsync(CancellationToken ct);
}
