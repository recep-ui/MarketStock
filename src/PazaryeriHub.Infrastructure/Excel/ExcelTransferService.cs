using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PazaryeriHub.Application.Excel;
using PazaryeriHub.Application.Stock;
using PazaryeriHub.Application.Sync;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.Excel;

/// <summary>
/// Excel şablon / içe aktarma / dışa aktarma.
/// İçe aktarma: (1) dosya okunur, (2) saf doğrulama (zorunlu, uzunluk, dosya içi tekrar), (3) veritabanı kontrolleri,
/// (4) yalnızca geçerli satırlar uygulanır. Hatalar Excel satır numarasıyla raporlanır.
/// </summary>
public sealed class ExcelTransferService(
    PazaryeriHubDbContext db,
    IStockLedger ledger,
    ILogger<ExcelTransferService> logger) : IExcelTransferService
{
    public const string DefaultWarehouseName = "Ana Depo";
    public const string SourceType = "EXCEL";

    private static readonly StringComparer TurkishIgnoreCase = StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), ignoreCase: true);

    private static readonly string[] ProductExtraHeaders = ["Fiziksel", "Rezerve", "Güvenlik Stoğu", "Satılabilir"];
    private static readonly string[] StockExtraHeaders = ["Rezerve", "Güvenlik Stoğu", "Satılabilir", "Ürün Adı"];
    private static readonly string[] ListingExtraHeaders = ["Son Gönderilen Stok", "Son Senkron (UTC)"];

    public byte[] CreateTemplate(ExcelKind kind) =>
        ExcelWorkbook.Write(ExcelLayouts.SheetName(kind), ExcelLayouts.For(kind), [], []);

    public async Task<ImportReport> ImportAsync(ExcelKind kind, Stream content, string? fileName, bool validateOnly, string? user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var read = ExcelWorkbook.Read(content, ExcelLayouts.For(kind));
        if (read.FileErrors.Count > 0)
        {
            return new ImportReport(kind, validateOnly, 0, 0, 0, 0, 0, [], read.FileErrors);
        }

        var started = TimeProvider.System.GetTimestamp();
        var report = kind switch
        {
            ExcelKind.Products => await ImportProductsAsync(read.Rows, validateOnly, ct),
            ExcelKind.Stock => await ImportStockAsync(read.Rows, fileName, validateOnly, user, ct),
            ExcelKind.Listings => await ImportListingsAsync(read.Rows, validateOnly, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        logger.LogInformation(
            "Excel içe aktarma {Tur} (yalnızca doğrulama: {YalnizcaDogrulama}): {Toplam} satır, {Eklenen} eklendi, {Guncellenen} güncellendi, " +
            "{Degismeyen} değişmedi, {HataliSatir} hatalı satır, {Sure} ms",
            kind, validateOnly, report.TotalRows, report.Created, report.Updated, report.Unchanged, report.ErrorRowCount,
            (int)TimeProvider.System.GetElapsedTime(started).TotalMilliseconds);

        return report;
    }

    public async Task<byte[]> ExportAsync(ExcelKind kind, CancellationToken ct)
    {
        switch (kind)
        {
            case ExcelKind.Products:
            {
                var rows = await db.Skus.AsNoTracking().OrderBy(s => s.SkuCode)
                    .Select(s => new object?[]
                    {
                        s.SkuCode, s.Product.Name, s.Product.Brand, s.Product.Category, s.Barcode, s.Color, s.Size, s.Product.ModelCode,
                        db.StockBalances.Where(b => b.SkuId == s.Id).Sum(b => (int?)b.Physical) ?? 0,
                        db.StockBalances.Where(b => b.SkuId == s.Id).Sum(b => (int?)b.Reserved) ?? 0,
                        db.StockBalances.Where(b => b.SkuId == s.Id).Sum(b => (int?)b.SafetyStock) ?? 0,
                        db.StockBalances.Where(b => b.SkuId == s.Id).Sum(b => (int?)b.Available) ?? 0
                    }).ToListAsync(ct);
                return ExcelWorkbook.Write(ExcelLayouts.SheetName(kind), ExcelLayouts.Products, ProductExtraHeaders, rows);
            }
            case ExcelKind.Stock:
            {
                // Sayım föyü: her bakiye satırı + bakiyesi hiç olmayan SKU'lar (varsayılan depoyla, adet 0).
                var balances = await (
                    from b in db.StockBalances
                    join s in db.Skus on b.SkuId equals s.Id
                    join w in db.Warehouses on b.WarehouseId equals w.Id
                    orderby s.SkuCode, w.Name
                    select new object?[] { s.SkuCode, w.Name, "SAYIM", b.Physical, b.Reserved, b.SafetyStock, b.Available, s.Product.Name })
                    .ToListAsync(ct);
                var withoutBalance = await db.Skus.AsNoTracking()
                    .Where(s => !db.StockBalances.Any(b => b.SkuId == s.Id))
                    .OrderBy(s => s.SkuCode)
                    .Select(s => new object?[] { s.SkuCode, DefaultWarehouseName, "SAYIM", 0, 0, 0, 0, s.Product.Name })
                    .ToListAsync(ct);
                return ExcelWorkbook.Write(ExcelLayouts.SheetName(kind), ExcelLayouts.Stock, StockExtraHeaders, balances.Concat(withoutBalance));
            }
            case ExcelKind.Listings:
            {
                var rows = await db.Listings.AsNoTracking().OrderBy(l => l.Sku.SkuCode).ThenBy(l => l.Channel.Code)
                    .Select(l => new object?[]
                    {
                        l.Sku.SkuCode, l.Channel.Code, l.ChannelProductCode, l.ChannelBarcode, l.Status, l.LastSentStock, l.LastSyncUtc
                    }).ToListAsync(ct);
                return ExcelWorkbook.Write(ExcelLayouts.SheetName(kind), ExcelLayouts.Listings, ListingExtraHeaders, rows);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    // ------------------------------------------------------------------ Ürün / SKU

    private async Task<ImportReport> ImportProductsAsync(IReadOnlyList<RawRow> rawRows, bool validateOnly, CancellationToken ct)
    {
        var outcome = ImportValidators.ValidateProducts(rawRows);
        var errors = outcome.Errors.ToList();
        var rows = outcome.Valid.ToList();

        // Veritabanı kontrolü: barkod başka bir SKU'da kayıtlı mı?
        var barcodes = rows.Where(r => r.Barcode is not null).Select(r => r.Barcode!).Distinct().ToList();
        var barcodeOwners = barcodes.Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : (await db.Skus.AsNoTracking().Where(s => s.Barcode != null && barcodes.Contains(s.Barcode))
                .Select(s => new { s.Barcode, s.SkuCode }).ToListAsync(ct))
                .ToDictionary(x => x.Barcode!, x => x.SkuCode, StringComparer.Ordinal);

        rows.RemoveAll(r =>
        {
            if (r.Barcode is not null && barcodeOwners.TryGetValue(r.Barcode, out var owner) && !string.Equals(owner, r.SkuCode, StringComparison.Ordinal))
            {
                errors.Add(new(r.Row, "Barkod", $"Barkod başka bir SKU'da kayıtlı (SKU: {owner})."));
                return true;
            }
            return false;
        });

        var codes = rows.Select(r => r.SkuCode).ToList();
        var existing = (await db.Skus.Include(s => s.Product).Where(s => codes.Contains(s.SkuCode)).ToListAsync(ct))
            .ToDictionary(s => s.SkuCode, StringComparer.Ordinal);

        // Yeni SKU'lar için ürün: aynı Ürün Adı + Marka'ya sahip mevcut ürün varsa ona eklenir, yoksa oluşturulur.
        var newRows = rows.Where(r => !existing.ContainsKey(r.SkuCode)).ToList();
        var names = newRows.Select(r => r.Name).Distinct().ToList();
        var productCandidates = names.Count == 0 ? [] : await db.Products.Where(p => names.Contains(p.Name)).ToListAsync(ct);
        var productsByKey = new Dictionary<string, ProductEntity>(TurkishIgnoreCase);
        foreach (var p in productCandidates.OrderBy(p => p.Id))
        {
            productsByKey.TryAdd(ProductKey(p.Name, p.Brand), p);
        }

        int created = 0, updated = 0, unchanged = 0;
        foreach (var r in rows)
        {
            if (existing.TryGetValue(r.SkuCode, out var sku))
            {
                var changed =
                    !Same(sku.Barcode, r.Barcode) || !Same(sku.Color, r.Color) || !Same(sku.Size, r.Size) ||
                    !Same(sku.Product.Name, r.Name) || !Same(sku.Product.Brand, r.Brand) || !Same(sku.Product.Category, r.Category) ||
                    (r.ModelCode is not null && !Same(sku.Product.ModelCode, r.ModelCode));
                if (!changed)
                {
                    unchanged++;
                    continue;
                }
                sku.Barcode = r.Barcode;
                sku.Color = r.Color;
                sku.Size = r.Size;
                sku.Product.Name = r.Name;
                sku.Product.Brand = r.Brand;
                sku.Product.Category = r.Category;
                // Boş Model Kodu mevcut değeri silmez (eski şablonla yükleme güvenli)
                sku.Product.ModelCode = r.ModelCode ?? sku.Product.ModelCode;
                updated++;
            }
            else
            {
                var key = ProductKey(r.Name, r.Brand);
                if (!productsByKey.TryGetValue(key, out var product))
                {
                    product = new ProductEntity { Name = r.Name, Brand = r.Brand, Category = r.Category, ModelCode = r.ModelCode };
                    db.Products.Add(product);
                    productsByKey[key] = product;
                }
                product.Skus.Add(new SkuEntity { SkuCode = r.SkuCode, Barcode = r.Barcode, Color = r.Color, Size = r.Size, Product = product });
                created++;
            }
        }

        if (!validateOnly && created + updated > 0)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();

        return Report(ExcelKind.Products, validateOnly, rawRows.Count, created, updated, unchanged, errors);
    }

    // ------------------------------------------------------------------ Stok

    private async Task<ImportReport> ImportStockAsync(IReadOnlyList<RawRow> rawRows, string? fileName, bool validateOnly, string? user, CancellationToken ct)
    {
        var outcome = ImportValidators.ValidateStock(rawRows);
        var errors = outcome.Errors.ToList();
        var rows = outcome.Valid.ToList();

        var codes = rows.Select(r => r.SkuCode).Distinct().ToList();
        var skuIds = (await db.Skus.AsNoTracking().Where(s => codes.Contains(s.SkuCode)).Select(s => new { s.SkuCode, s.Id }).ToListAsync(ct))
            .ToDictionary(x => x.SkuCode, x => x.Id, StringComparer.Ordinal);
        var warehouses = (await db.Warehouses.AsNoTracking().ToListAsync(ct))
            .ToDictionary(w => ExcelText.ToKey(w.Name), w => w.Id, StringComparer.Ordinal);
        var defaultWarehouse = warehouses.TryGetValue(ExcelText.ToKey(DefaultWarehouseName), out var dw) ? dw : (int?)null;

        var ids = skuIds.Values.ToList();
        var balances = (await db.StockBalances.Where(b => ids.Contains(b.SkuId)).ToListAsync(ct))
            .ToDictionary(b => (b.SkuId, b.WarehouseId));

        int updated = 0, unchanged = 0;
        foreach (var r in rows)
        {
            if (!skuIds.TryGetValue(r.SkuCode, out var skuId))
            {
                errors.Add(new(r.Row, "SKU Kodu", $"SKU bulunamadı: '{r.SkuCode}'. Önce ürün şablonuyla ekleyin."));
                continue;
            }

            int warehouseId;
            if (r.WarehouseName is null)
            {
                if (defaultWarehouse is null)
                {
                    errors.Add(new(r.Row, "Depo", $"Depo boş ve '{DefaultWarehouseName}' tanımlı değil."));
                    continue;
                }
                warehouseId = defaultWarehouse.Value;
            }
            else if (!warehouses.TryGetValue(ExcelText.ToKey(r.WarehouseName), out warehouseId))
            {
                errors.Add(new(r.Row, "Depo", $"Depo bulunamadı: '{r.WarehouseName}'."));
                continue;
            }

            balances.TryGetValue((skuId, warehouseId), out var balance);
            if (r.Operation == StockImportOperation.Count)
            {
                if (balance is not null && r.Quantity < balance.Reserved)
                {
                    errors.Add(new(r.Row, "Adet", $"Sayılan adet ({r.Quantity}) mevcut rezerveden ({balance.Reserved}) az olamaz."));
                    continue;
                }
                if ((balance?.Physical ?? 0) == r.Quantity && balance is not null)
                {
                    unchanged++; // fark yok: hareket yazılmaz (dosyayı tekrar yüklemek güvenli)
                    continue;
                }
            }

            if (validateOnly)
            {
                updated++;
                continue;
            }

            var request = new StockChangeRequest(skuId, warehouseId, r.Quantity, SourceType, SourceId(fileName, r.Row), user);
            try
            {
                _ = r.Operation == StockImportOperation.Count
                    ? await ledger.AdjustToCountAsync(request, ct)
                    : await ledger.ReceiveAsync(request, ct);
                updated++;
            }
            catch (StockOperationException ex)
            {
                // Satır doğrulandıktan sonra durum değişmiş olabilir (eşzamanlı rezervasyon vb.)
                errors.Add(new(r.Row, "Adet", ex.Message));
            }
        }

        return Report(ExcelKind.Stock, validateOnly, rawRows.Count, 0, updated, unchanged, errors);
    }

    // ------------------------------------------------------------------ Eşleştirme

    private async Task<ImportReport> ImportListingsAsync(IReadOnlyList<RawRow> rawRows, bool validateOnly, CancellationToken ct)
    {
        var outcome = ImportValidators.ValidateListings(rawRows);
        var errors = outcome.Errors.ToList();
        var rows = outcome.Valid.ToList();

        var codes = rows.Select(r => r.SkuCode).Distinct().ToList();
        var skuIds = (await db.Skus.AsNoTracking().Where(s => codes.Contains(s.SkuCode)).Select(s => new { s.SkuCode, s.Id }).ToListAsync(ct))
            .ToDictionary(x => x.SkuCode, x => x.Id, StringComparer.Ordinal);
        var channels = (await db.Channels.AsNoTracking().ToListAsync(ct)).ToDictionary(c => c.Code, c => c.Id, StringComparer.Ordinal);

        var ids = skuIds.Values.ToList();
        var existing = (await db.Listings.Where(l => ids.Contains(l.SkuId)).ToListAsync(ct))
            .ToDictionary(l => (l.SkuId, l.ChannelId));

        int created = 0, updated = 0, unchanged = 0;
        foreach (var r in rows)
        {
            if (!skuIds.TryGetValue(r.SkuCode, out var skuId))
            {
                errors.Add(new(r.Row, "SKU Kodu", $"SKU bulunamadı: '{r.SkuCode}'."));
                continue;
            }
            if (!channels.TryGetValue(r.ChannelCode, out var channelId))
            {
                errors.Add(new(r.Row, "Kanal", $"Kanal bulunamadı: '{r.ChannelCode}'. Geçerli değerler: {string.Join(", ", channels.Keys.Order())}."));
                continue;
            }

            if (existing.TryGetValue((skuId, channelId), out var listing))
            {
                if (Same(listing.ChannelProductCode, r.ChannelProductCode) && Same(listing.ChannelBarcode, r.ChannelBarcode) && listing.Status == r.Status)
                {
                    unchanged++;
                    continue;
                }
                updated++;
            }
            else
            {
                listing = new ChannelListingEntity { SkuId = skuId, ChannelId = channelId };
                db.Listings.Add(listing);
                existing[(skuId, channelId)] = listing;
                created++;
            }
            listing.ChannelProductCode = r.ChannelProductCode;
            listing.ChannelBarcode = r.ChannelBarcode;
            listing.Status = r.Status;
        }

        if (!validateOnly && created + updated > 0)
        {
            var changedListings = db.ChangeTracker.Entries<ChannelListingEntity>()
                .Where(e => e.State is EntityState.Added or EntityState.Modified).Select(e => e.Entity).ToList();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            // Yeni/değişen eşleştirmeler için kanala stok gönderimi tetiklenir (aynı transaction'da olay).
            foreach (var listing in changedListings)
            {
                await OutboxWriter.AddAsync(db, OutboxEvents.ListingChanged, new { ListingId = listing.Id }, ct);
            }
            await tx.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();

        return Report(ExcelKind.Listings, validateOnly, rawRows.Count, created, updated, unchanged, errors);
    }

    // ------------------------------------------------------------------ Yardımcılar

    private static ImportReport Report(ExcelKind kind, bool validateOnly, int total, int created, int updated, int unchanged, List<ImportRowError> errors)
    {
        var sorted = errors.OrderBy(e => e.Row).ThenBy(e => e.Column, StringComparer.Ordinal).ToList();
        var errorRows = sorted.Select(e => e.Row).Distinct().Count();
        return new ImportReport(kind, validateOnly, total, total - errorRows, created, updated, unchanged, sorted, []);
    }

    private static string ProductKey(string name, string? brand) => name.Trim() + "\u001F" + (brand?.Trim() ?? "");

    private static bool Same(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);

    private static string SourceId(string? fileName, int row)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "excel" : Path.GetFileName(fileName);
        var suffix = ":" + row.ToString(CultureInfo.InvariantCulture);
        return (name.Length + suffix.Length > 100 ? name[..(100 - suffix.Length)] : name) + suffix;
    }
}
