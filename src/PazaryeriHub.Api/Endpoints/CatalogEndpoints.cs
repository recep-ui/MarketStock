using PazaryeriHub.Application.Catalog;
using PazaryeriHub.Application.Stock;

namespace PazaryeriHub.Api.Endpoints;

public sealed record CreateWarehouseRequest(string Name);

public sealed record StockReceiptRequest(int SkuId, int WarehouseId, int Quantity, string? Reference);

public sealed record StockCountRequest(int SkuId, int WarehouseId, int CountedQuantity, string? Reference);

public sealed record SafetyStockRequest(int SkuId, int WarehouseId, int Quantity);

/// <summary>
/// Ürün, SKU, depo ve stok uç noktaları.
/// // TODO (Aşama 5): yetkilendirme. Şu an kimlik doğrulama yok; yalnızca yerel/geliştirme ortamında kullanılmalı.
/// </summary>
public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var products = app.MapGroup("/api/products").WithTags("Ürünler");
        products.MapGet("/", (ICatalogService s, string? search, int? page, int? pageSize, CancellationToken ct) =>
            s.SearchProductsAsync(search, page, pageSize, ct));
        products.MapGet("/{id:int}", (ICatalogService s, int id, CancellationToken ct) => s.GetProductAsync(id, ct));
        products.MapPost("/", async (ICatalogService s, CreateProductRequest request, CancellationToken ct) =>
        {
            var created = await s.CreateProductAsync(request, ct);
            return Results.Created($"/api/products/{created.Id}", created);
        });
        products.MapPut("/{id:int}", (ICatalogService s, int id, ProductInput input, CancellationToken ct) => s.UpdateProductAsync(id, input, ct));
        products.MapDelete("/{id:int}", async (ICatalogService s, int id, CancellationToken ct) =>
        {
            await s.DeleteProductAsync(id, ct);
            return Results.NoContent();
        });
        products.MapPost("/{id:int}/skus", async (ICatalogService s, int id, SkuInput input, CancellationToken ct) =>
        {
            var sku = await s.AddSkuAsync(id, input, ct);
            return Results.Created($"/api/skus/{sku.Id}", sku);
        });

        var skus = app.MapGroup("/api/skus").WithTags("SKU");
        skus.MapGet("/", (ICatalogService s, string? search, int? page, int? pageSize, CancellationToken ct) =>
            s.SearchSkusAsync(search, page, pageSize, ct));
        skus.MapPut("/{id:int}", (ICatalogService s, int id, SkuInput input, CancellationToken ct) => s.UpdateSkuAsync(id, input, ct));
        skus.MapDelete("/{id:int}", async (ICatalogService s, int id, CancellationToken ct) =>
        {
            await s.DeleteSkuAsync(id, ct);
            return Results.NoContent();
        });

        var warehouses = app.MapGroup("/api/warehouses").WithTags("Depolar");
        warehouses.MapGet("/", (ICatalogService s, CancellationToken ct) => s.GetWarehousesAsync(ct));
        warehouses.MapPost("/", async (ICatalogService s, CreateWarehouseRequest request, CancellationToken ct) =>
        {
            var created = await s.CreateWarehouseAsync(request.Name, ct);
            return Results.Created($"/api/warehouses/{created.Id}", created);
        });

        var stock = app.MapGroup("/api/stock").WithTags("Stok");
        stock.MapGet("/{skuId:int}", (IStockLedger l, int skuId, CancellationToken ct) => l.GetBalancesAsync(skuId, ct));
        stock.MapPost("/receipts", (IStockLedger l, StockReceiptRequest r, CancellationToken ct) =>
            l.ReceiveAsync(new StockChangeRequest(r.SkuId, r.WarehouseId, r.Quantity, "PANEL", r.Reference), ct));
        stock.MapPost("/counts", (IStockLedger l, StockCountRequest r, CancellationToken ct) =>
            l.AdjustToCountAsync(new StockChangeRequest(r.SkuId, r.WarehouseId, r.CountedQuantity, "PANEL", r.Reference), ct));
        stock.MapPut("/safety-stock", (IStockLedger l, SafetyStockRequest r, CancellationToken ct) =>
            l.SetSafetyStockAsync(new StockChangeRequest(r.SkuId, r.WarehouseId, r.Quantity, "PANEL"), ct));

        return app;
    }
}
