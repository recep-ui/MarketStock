using System.Security.Claims;
using PazaryeriHub.Api.Auth;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.Catalog;
using PazaryeriHub.Application.Stock;

namespace PazaryeriHub.Api.Endpoints;

public sealed record StockReceiptRequest(int SkuId, int WarehouseId, int Quantity, string? Reference);

public sealed record StockCountRequest(int SkuId, int WarehouseId, int CountedQuantity, string? Reference);

public sealed record SafetyStockRequest(int SkuId, int WarehouseId, int Quantity);

/// <summary>
/// Ürün, SKU, depo ve stok uç noktaları.
/// Yetki: okuma tüm roller; ürün/SKU yazma Yönetici; stok yazma Yönetici + Depo (ADR-014).
/// </summary>
public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var products = app.MapGroup("/api/products").WithTags("Ürünler").RequireAuthorization(Policies.Read);
        products.MapGet("/", (ICatalogService s, string? search, int? page, int? pageSize, CancellationToken ct) =>
            s.SearchProductsAsync(search, page, pageSize, ct));
        products.MapGet("/{id:int}", (ICatalogService s, int id, CancellationToken ct) => s.GetProductAsync(id, ct));
        products.MapPost("/", async (ICatalogService s, CreateProductRequest request, CancellationToken ct) =>
        {
            var created = await s.CreateProductAsync(request, ct);
            return Results.Created($"/api/products/{created.Id}", created);
        }).RequireAuthorization(Policies.CatalogWrite);
        products.MapPut("/{id:int}", (ICatalogService s, int id, ProductInput input, CancellationToken ct) => s.UpdateProductAsync(id, input, ct))
            .RequireAuthorization(Policies.CatalogWrite);
        products.MapDelete("/{id:int}", async (ICatalogService s, int id, CancellationToken ct) =>
        {
            await s.DeleteProductAsync(id, ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.CatalogWrite);
        products.MapPost("/{id:int}/skus", async (ICatalogService s, int id, SkuInput input, CancellationToken ct) =>
        {
            var sku = await s.AddSkuAsync(id, input, ct);
            return Results.Created($"/api/skus/{sku.Id}", sku);
        }).RequireAuthorization(Policies.CatalogWrite);

        var skus = app.MapGroup("/api/skus").WithTags("SKU").RequireAuthorization(Policies.Read);
        skus.MapGet("/", (ICatalogService s, string? search, int? page, int? pageSize, CancellationToken ct) =>
            s.SearchSkusAsync(search, page, pageSize, ct));
        skus.MapPut("/{id:int}", (ICatalogService s, int id, SkuInput input, CancellationToken ct) => s.UpdateSkuAsync(id, input, ct))
            .RequireAuthorization(Policies.CatalogWrite);
        skus.MapDelete("/{id:int}", async (ICatalogService s, int id, CancellationToken ct) =>
        {
            await s.DeleteSkuAsync(id, ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.CatalogWrite);

        var warehouses = app.MapGroup("/api/warehouses").WithTags("Depolar").RequireAuthorization(Policies.Read);
        warehouses.MapGet("/", (ICatalogService s, CancellationToken ct) => s.GetWarehousesAsync(ct));

        var stock = app.MapGroup("/api/stock").WithTags("Stok").RequireAuthorization(Policies.Read);
        stock.MapGet("/{skuId:int}", (IStockLedger l, int skuId, CancellationToken ct) => l.GetBalancesAsync(skuId, ct));
        stock.MapPost("/receipts", (IStockLedger l, ClaimsPrincipal user, StockReceiptRequest r, CancellationToken ct) =>
            l.ReceiveAsync(new StockChangeRequest(r.SkuId, r.WarehouseId, r.Quantity, "PANEL", r.Reference, user.UserName()), ct))
            .RequireAuthorization(Policies.StockWrite);
        stock.MapPost("/counts", (IStockLedger l, ClaimsPrincipal user, StockCountRequest r, CancellationToken ct) =>
            l.AdjustToCountAsync(new StockChangeRequest(r.SkuId, r.WarehouseId, r.CountedQuantity, "PANEL", r.Reference, user.UserName()), ct))
            .RequireAuthorization(Policies.StockWrite);
        stock.MapPut("/safety-stock", (IStockLedger l, ClaimsPrincipal user, SafetyStockRequest r, CancellationToken ct) =>
            l.SetSafetyStockAsync(new StockChangeRequest(r.SkuId, r.WarehouseId, r.Quantity, "PANEL", null, user.UserName()), ct))
            .RequireAuthorization(Policies.StockWrite);

        return app;
    }
}
