using Microsoft.EntityFrameworkCore;
using PazaryeriHub.Application.Catalog;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.Catalog;

/// <summary>Ürün / SKU / depo CRUD (EF Core).</summary>
public sealed class CatalogService(PazaryeriHubDbContext db) : ICatalogService
{
    public async Task<PagedResult<ProductDto>> SearchProductsAsync(string? search, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var term = Text.Clean(search);

        var query = db.Products.AsNoTracking();
        if (term is not null)
        {
            // Ad/marka Turkish_CI_AS (büyük/küçük harf duyarsız), SKU/barkod BIN2 (tam eşleşme ön ekiyle)
            query = query.Where(x => x.Name.Contains(term) || (x.Brand != null && x.Brand.Contains(term))
                                     || x.Skus.Any(s => s.SkuCode.StartsWith(term) || (s.Barcode != null && s.Barcode == term)));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.Id)
            .Skip((p - 1) * size).Take(size)
            .Include(x => x.Skus)
            .ToListAsync(ct);

        return new PagedResult<ProductDto>(items.Select(ToDto).ToList(), total, p, size);
    }

    public async Task<ProductDto> GetProductAsync(int id, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().Include(x => x.Skus).FirstOrDefaultAsync(x => x.Id == id, ct)
                      ?? throw new NotFoundException($"Ürün bulunamadı (Id={id}).");
        return ToDto(product);
    }

    public async Task<ProductDto> CreateProductAsync(CreateProductRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var v = new Validator();
        ValidateProduct(v, new ProductInput(request.Name, request.Brand, request.Category));
        var skus = request.Skus ?? [];
        for (var i = 0; i < skus.Count; i++)
        {
            ValidateSku(v, skus[i], $"skus[{i}].");
        }
        v.Must(skus.Select(s => s.SkuCode?.Trim()).Distinct(StringComparer.Ordinal).Count() == skus.Count, "skus", "Aynı SKU kodu birden fazla kez verilmiş.");
        v.ThrowIfInvalid();

        var product = new ProductEntity
        {
            Name = request.Name.Trim(),
            Brand = Text.Clean(request.Brand),
            Category = Text.Clean(request.Category),
            Skus = skus.Select(ToEntity).ToList()
        };
        db.Products.Add(product);
        await SaveAsync(ct);
        return await GetProductAsync(product.Id, ct);
    }

    public async Task<ProductDto> UpdateProductAsync(int id, ProductInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = new Validator();
        ValidateProduct(v, input);
        v.ThrowIfInvalid();

        var product = await db.Products.FirstOrDefaultAsync(x => x.Id == id, ct)
                      ?? throw new NotFoundException($"Ürün bulunamadı (Id={id}).");
        product.Name = input.Name.Trim();
        product.Brand = Text.Clean(input.Brand);
        product.Category = Text.Clean(input.Category);
        await SaveAsync(ct);
        return await GetProductAsync(id, ct);
    }

    public async Task DeleteProductAsync(int id, CancellationToken ct)
    {
        var product = await db.Products.Include(x => x.Skus).FirstOrDefaultAsync(x => x.Id == id, ct)
                      ?? throw new NotFoundException($"Ürün bulunamadı (Id={id}).");
        db.Skus.RemoveRange(product.Skus);
        db.Products.Remove(product);
        await SaveAsync(ct, "Ürünün SKU'larından birine bağlı stok, hareket, sipariş veya eşleştirme kaydı var; ürün silinemez.");
    }

    public async Task<PagedResult<SkuListItem>> SearchSkusAsync(string? search, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var term = Text.Clean(search);

        var query = db.Skus.AsNoTracking();
        if (term is not null)
        {
            query = query.Where(s => s.SkuCode.StartsWith(term) || s.Barcode == term || s.Product.Name.Contains(term));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(s => s.SkuCode)
            .Skip((p - 1) * size).Take(size)
            .Select(s => new SkuListItem(
                s.Id, s.SkuCode, s.Barcode, s.Color, s.Size, s.ProductId, s.Product.Name, s.Product.Brand,
                db.StockBalances.Where(b => b.SkuId == s.Id).Sum(b => (int?)b.Physical) ?? 0,
                db.StockBalances.Where(b => b.SkuId == s.Id).Sum(b => (int?)b.Reserved) ?? 0,
                db.StockBalances.Where(b => b.SkuId == s.Id).Sum(b => (int?)b.SafetyStock) ?? 0,
                db.StockBalances.Where(b => b.SkuId == s.Id).Sum(b => (int?)b.Available) ?? 0))
            .ToListAsync(ct);

        return new PagedResult<SkuListItem>(items, total, p, size);
    }

    public async Task<SkuDto> AddSkuAsync(int productId, SkuInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = new Validator();
        ValidateSku(v, input, "");
        v.ThrowIfInvalid();

        if (!await db.Products.AnyAsync(x => x.Id == productId, ct))
        {
            throw new NotFoundException($"Ürün bulunamadı (Id={productId}).");
        }

        var sku = ToEntity(input);
        sku.ProductId = productId;
        db.Skus.Add(sku);
        await SaveAsync(ct);
        return ToDto(sku);
    }

    public async Task<SkuDto> UpdateSkuAsync(int skuId, SkuInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = new Validator();
        ValidateSku(v, input, "");
        v.ThrowIfInvalid();

        var sku = await db.Skus.FirstOrDefaultAsync(x => x.Id == skuId, ct)
                  ?? throw new NotFoundException($"SKU bulunamadı (Id={skuId}).");
        sku.SkuCode = input.SkuCode.Trim();
        sku.Barcode = Text.Clean(input.Barcode);
        sku.Color = Text.Clean(input.Color);
        sku.Size = Text.Clean(input.Size);
        await SaveAsync(ct);
        return ToDto(sku);
    }

    public async Task DeleteSkuAsync(int skuId, CancellationToken ct)
    {
        var sku = await db.Skus.FirstOrDefaultAsync(x => x.Id == skuId, ct)
                  ?? throw new NotFoundException($"SKU bulunamadı (Id={skuId}).");
        db.Skus.Remove(sku);
        await SaveAsync(ct, "SKU'ya bağlı stok, hareket, sipariş veya eşleştirme kaydı var; SKU silinemez.");
    }

    public async Task<IReadOnlyList<WarehouseDto>> GetWarehousesAsync(CancellationToken ct) =>
        await db.Warehouses.AsNoTracking().OrderBy(w => w.Id).Select(w => new WarehouseDto(w.Id, w.Name)).ToListAsync(ct);

    public async Task<WarehouseDto> CreateWarehouseAsync(string name, CancellationToken ct)
    {
        var v = new Validator();
        v.Required("name", name, CatalogLimits.WarehouseName);
        v.ThrowIfInvalid();

        var entity = new WarehouseEntity { Name = name.Trim() };
        db.Warehouses.Add(entity);
        await SaveAsync(ct);
        return new WarehouseDto(entity.Id, entity.Name);
    }

    private async Task SaveAsync(CancellationToken ct, string? referenceConflictMessage = null)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUnique(ex))
        {
            throw DbErrors.UniqueConflict(ex);
        }
        catch (DbUpdateException ex) when (referenceConflictMessage is not null && DbErrors.IsReferenceViolation(ex))
        {
            throw new ConflictException(referenceConflictMessage, ex);
        }
    }

    internal static void ValidateProduct(Validator v, ProductInput input)
    {
        v.Required("name", input.Name, CatalogLimits.ProductName)
         .MaxLength("brand", input.Brand, CatalogLimits.Brand)
         .MaxLength("category", input.Category, CatalogLimits.Category);
    }

    internal static void ValidateSku(Validator v, SkuInput input, string prefix)
    {
        v.Required(prefix + "skuCode", input.SkuCode, CatalogLimits.SkuCode)
         .MaxLength(prefix + "barcode", input.Barcode, CatalogLimits.Barcode)
         .MaxLength(prefix + "color", input.Color, CatalogLimits.Color)
         .MaxLength(prefix + "size", input.Size, CatalogLimits.Size);
    }

    private static SkuEntity ToEntity(SkuInput s) => new()
    {
        SkuCode = s.SkuCode.Trim(),
        Barcode = Text.Clean(s.Barcode),
        Color = Text.Clean(s.Color),
        Size = Text.Clean(s.Size)
    };

    private static SkuDto ToDto(SkuEntity s) => new(s.Id, s.ProductId, s.SkuCode, s.Barcode, s.Color, s.Size);

    private static ProductDto ToDto(ProductEntity p) =>
        new(p.Id, p.Name, p.Brand, p.Category, DateTime.SpecifyKind(p.CreatedAtUtc, DateTimeKind.Utc),
            p.Skus.OrderBy(s => s.Id).Select(ToDto).ToList());
}
