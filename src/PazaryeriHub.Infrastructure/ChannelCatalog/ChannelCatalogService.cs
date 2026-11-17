using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PazaryeriHub.Application.ChannelCatalog;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Excel;
using PazaryeriHub.Application.Sync;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.ChannelCatalog;

/// <summary>
/// Kanal kataloğu içe alma (ADR-020). Excel ve kanal API'si aynı yoldan geçer:
/// eşleştir (<see cref="ChannelCatalogMatcher"/>) → çakışma kontrolü → eşleşenleri ChannelListing'e yaz →
/// (isteğe bağlı) eşleşmeyenlerden ürün/SKU oluştur → kalanları UnmatchedChannelItem'a yaz.
/// <para>Güvenlik kuralları:</para>
/// <list type="bullet">
/// <item>Mevcut bir eşleştirme farklı bir barkod/kodu gösteriyorsa ÜZERİNE YAZILMAZ ("çakışma").</item>
/// <item>Ad benzerliği yalnızca öneridir; bağlama elle yapılır.</item>
/// <item>Stoğu hiç girilmemiş SKU'ların eşleştirmeleri kanala gönderilmez (pazaryerinde stok 0'a çekilmesin);
///       stok girildiğinde StokDegisti olayıyla gönderilir.</item>
/// </list>
/// </summary>
public sealed class ChannelCatalogService(
    PazaryeriHubDbContext db,
    TimeProvider clock,
    ILogger<ChannelCatalogService> logger) : IChannelCatalogService
{
    public const int MaxReportRows = 2_000;

    public async Task<CatalogImportReport> ImportFileAsync(int channelId, Stream content, string? fileName, CatalogImportOptions options, string? user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        var channel = await FindChannelAsync(channelId, ct);
        var read = ChannelCatalogFileReader.Read(content, channel.Code);
        if (read.FileErrors.Count > 0)
        {
            return EmptyReport(channel.Code, read.Format?.Code, CatalogImportSources.Excel, options, read.FileErrors);
        }
        return await ApplyCoreAsync(channel, read.Items, CatalogImportSources.Excel, options, user, read.Errors, read.Warnings, read.TotalRows, read.Format?.Code, ct);
    }

    public async Task<CatalogImportReport> ApplyAsync(int channelId, IReadOnlyList<ChannelCatalogItem> items, string source, CatalogImportOptions options, string? user,
        IReadOnlyList<ImportRowError>? errors, IReadOnlyList<string>? warnings, int? totalRows, string? format, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);
        var channel = await FindChannelAsync(channelId, ct);
        return await ApplyCoreAsync(channel, items, source, options, user, errors ?? [], warnings ?? [], totalRows ?? items.Count, format, ct);
    }

    // ------------------------------------------------------------------ Eşleştir + uygula

    private async Task<CatalogImportReport> ApplyCoreAsync(
        ChannelEntity channel, IReadOnlyList<ChannelCatalogItem> items, string source, CatalogImportOptions options, string? user,
        IReadOnlyList<ImportRowError> fileRowErrors, IReadOnlyList<string> readWarnings, int totalRows, string? format, CancellationToken ct)
    {
        var started = clock.GetTimestamp();
        var now = clock.GetUtcNow().UtcDateTime;

        var skus = await db.Skus.AsNoTracking()
            .Select(s => new CatalogSkuRef(s.Id, s.SkuCode, s.Barcode, s.Product.ModelCode, s.Color, s.Size, s.Product.Name, s.Product.Brand))
            .ToListAsync(ct);
        var skuById = skus.ToDictionary(s => s.SkuId);
        var listings = await db.Listings.Where(l => l.ChannelId == channel.Id).ToListAsync(ct);
        var skusWithBalance = (await db.StockBalances.Select(b => b.SkuId).Distinct().ToListAsync(ct)).ToHashSet();

        var matches = ChannelCatalogMatcher.Match(items, skus,
            listings.Select(l => new ExistingListingRef(l.SkuId, l.ChannelProductCode, l.ChannelBarcode)).ToList());

        var state = new ListingIndex(listings);
        var counts = new Counters();
        var rows = new List<CatalogImportRow>(matches.Count);
        var changedListings = new List<ChannelListingEntity>();
        var resolvedKeys = new HashSet<string>(StringComparer.Ordinal);
        var unmatched = new List<CatalogMatch>();

        foreach (var m in matches)
        {
            if (!m.IsMatched)
            {
                unmatched.Add(m);
                continue;
            }

            var item = m.Item;
            var sku = m.Sku!;
            var conflict = state.FindConflict(sku, item, skuById);
            if (conflict is not null)
            {
                var c = m with { Sku = null, Step = null, Reason = UnmatchedReasons.Conflict, Detail = conflict };
                unmatched.Add(c);
                continue;
            }

            var change = Upsert(state, sku.SkuId, channel.Id, item, source, changedListings);
            counts.Count(m.Step!.Value, change);
            resolvedKeys.Add(item.Key);
            rows.Add(Row(item, CatalogRowOutcomes.Matched, sku.SkuCode, StepName(m.Step), change, null, null, null));
        }

        // Eşleşmeyenlerden ürün/SKU oluşturma (yalnızca açıkça istenirse; belirsiz/çakışan satırlar hariç)
        if (options.CreateMissing)
        {
            var (created, ambiguousNew) = await CreateMissingAsync(channel, items, unmatched, skus, state, source, changedListings, ct);
            foreach (var (original, replaced) in ambiguousNew)
            {
                unmatched[unmatched.IndexOf(original)] = replaced;
            }
            foreach (var (match, skuCode, detail) in created)
            {
                unmatched.Remove(match);
                resolvedKeys.Add(match.Item.Key);
                rows.Add(Row(match.Item, CatalogRowOutcomes.Created, skuCode, null, "YENI", detail, null, null));
            }
            counts.SkusCreated = created.Count;
            counts.ProductsCreated = db.ChangeTracker.Entries<ProductEntity>().Count(e => e.State == EntityState.Added);
        }

        foreach (var u in unmatched)
        {
            rows.Add(Row(u.Item, u.Reason!, null, null, null, u.Detail, u.Suggestion?.SkuCode, u.SuggestionScore));
        }

        await UpsertUnmatchedAsync(channel.Id, source, unmatched, resolvedKeys, user, now, ct);

        // Stoğu hiç girilmemiş SKU'lar kanala gönderilmez (yeni oluşturulan SKU'ların bakiyesi yoktur)
        var toPush = changedListings.Where(l => l.SkuId != 0 && skusWithBalance.Contains(l.SkuId)).ToList();
        var pushDeferred = changedListings.Count - toPush.Count;

        if (options.ValidateOnly)
        {
            db.ChangeTracker.Clear();
        }
        else
        {
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);
                foreach (var listing in toPush)
                {
                    await OutboxWriter.AddAsync(db, OutboxEvents.ListingChanged, new { ListingId = listing.Id }, ct);
                }
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (DbErrors.IsUnique(ex))
            {
                throw DbErrors.UniqueConflict(ex);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        var warnings = readWarnings.ToList();
        if (pushDeferred > 0)
        {
            warnings.Add($"{pushDeferred} eşleştirmenin SKU'sunda henüz stok girişi yok; bu ürünler kanala gönderilmedi. Stok/sayım girildiğinde otomatik gönderilir.");
        }

        var ordered = rows.OrderBy(r => OutcomeOrder(r.Outcome)).ThenBy(r => r.Row).ToList();
        var report = new CatalogImportReport(
            channel.Code, format, source, options.ValidateOnly, options.CreateMissing, totalRows, items.Count,
            counts.Existing, counts.Barcode, counts.StockCode, counts.ModelColorSize,
            counts.Created + counts.SkusCreated, counts.Updated, counts.Unchanged, counts.ProductsCreated, counts.SkusCreated,
            unmatched.Count(u => u.Reason == UnmatchedReasons.NoMatch),
            unmatched.Count(u => u.Reason == UnmatchedReasons.Ambiguous),
            unmatched.Count(u => u.Reason == UnmatchedReasons.Conflict),
            unmatched.Count(u => u.Suggestion is not null),
            items.Count(i => i.Passive),
            pushDeferred,
            ordered.Take(MaxReportRows).ToList(), ordered.Count > MaxReportRows,
            fileRowErrors, [], warnings);

        logger.LogInformation(
            "Kanal kataloğu içe alma {Kanal} ({Kaynak}, yalnızca doğrulama: {YalnizcaDogrulama}, SKU oluştur: {Olustur}): {Toplam} satır, " +
            "{Eslesen} eşleşti, {YeniSku} SKU oluşturuldu, {Eslesmeyen} eşleşmedi, {Belirsiz} belirsiz, {Cakisma} çakışma, {Hatali} hatalı satır, {Sure} ms",
            channel.Code, source, options.ValidateOnly, options.CreateMissing, totalRows,
            counts.Existing + counts.Barcode + counts.StockCode + counts.ModelColorSize, counts.SkusCreated,
            report.Unmatched, report.Ambiguous, report.Conflicts, report.ErrorRowCount,
            (int)clock.GetElapsedTime(started).TotalMilliseconds);

        return report;
    }

    private string Upsert(ListingIndex state, int skuId, int channelId, ChannelCatalogItem item, string source, List<ChannelListingEntity> changed)
    {
        var json = ChannelData(item, source);
        if (!state.BySku.TryGetValue(skuId, out var listing))
        {
            listing = new ChannelListingEntity
            {
                SkuId = skuId, ChannelId = channelId, ChannelBarcode = item.Barcode, ChannelProductCode = item.ChannelProductCode,
                Status = item.Passive ? ListingStatus.Passive : ListingStatus.Active, ChannelDataJson = json
            };
            db.Listings.Add(listing);
            state.Add(listing);
            changed.Add(listing);
            return "YENI";
        }

        // Kanal "kapalı" diyorsa PASIF; aksi halde elle verilmiş durum korunur.
        var status = item.Passive ? ListingStatus.Passive : listing.Status;
        var barcode = item.Barcode ?? listing.ChannelBarcode;
        var code = item.ChannelProductCode ?? listing.ChannelProductCode;
        listing.ChannelDataJson = json;
        if (Same(listing.ChannelBarcode, barcode) && Same(listing.ChannelProductCode, code) && listing.Status == status)
        {
            return "DEGISMEDI";
        }
        listing.ChannelBarcode = barcode;
        listing.ChannelProductCode = code;
        listing.Status = status;
        state.Index(listing);
        changed.Add(listing);
        return "GUNCELLENDI";
    }

    private async Task<(List<(CatalogMatch Match, string SkuCode, string Detail)> Created, List<(CatalogMatch Original, CatalogMatch Ambiguous)> Ambiguous)> CreateMissingAsync(
        ChannelEntity channel, IReadOnlyList<ChannelCatalogItem> items, List<CatalogMatch> unmatched, IReadOnlyList<CatalogSkuRef> skus,
        ListingIndex state, string source, List<ChannelListingEntity> changed, CancellationToken ct)
    {
        var result = new List<(CatalogMatch, string, string)>();
        var ambiguous = new List<(CatalogMatch, CatalogMatch)>();
        // Ad benzerliği önerisi olan satırlar mevcut bir ürünün kopyası olabilir: onlardan SKU oluşturulmaz, karar kullanıcıda.
        var candidates = unmatched.Where(u => u.Reason == UnmatchedReasons.NoMatch && u.Item.Barcode is not null && u.Suggestion is null).ToList();
        if (candidates.Count == 0)
        {
            return (result, ambiguous);
        }

        var usedCodes = skus.Select(s => s.SkuCode).ToHashSet(StringComparer.Ordinal);
        var usedBarcodes = skus.Where(s => s.Barcode is not null).Select(s => s.Barcode!).ToHashSet(StringComparer.Ordinal);
        var stockCodeCounts = items.Where(i => i.StockCode is not null).GroupBy(i => i.StockCode!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var variantCodeCounts = items.Select(VariantCode).OfType<string>().GroupBy(c => c, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var modelKeyRows = items.Where(i => i.ModelCode is not null)
            .GroupBy(i => ChannelCatalogMatcher.ModelColorSizeKey(i.ModelCode, i.Color, i.Size), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(i => i.Row).ToList(), StringComparer.Ordinal);

        // Ürün grubu: aynı Model Kodu + Marka → aynı ürün (yoksa Ürün Adı + Marka). Mevcut ürünler Model Kodu ile bulunur.
        var modelCodes = candidates.Select(c => c.Item.ModelCode).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var existingProducts = modelCodes.Count == 0 ? [] : await db.Products.Where(p => p.ModelCode != null && modelCodes.Contains(p.ModelCode)).ToListAsync(ct);
        var products = new Dictionary<string, ProductEntity>(StringComparer.Ordinal);
        foreach (var p in existingProducts.OrderBy(p => p.Id))
        {
            products.TryAdd(ProductKey(p.ModelCode, p.Name, p.Brand), p);
        }

        foreach (var match in candidates)
        {
            var item = match.Item;
            if (usedBarcodes.Contains(item.Barcode!))
            {
                continue;
            }

            // Aynı model + renk + beden dosyada birden fazla satırda (ör. Trendyol "Çoklanan Ürün Satışı"): aynı fiziksel ürün
            // olabilir; ayrı SKU açmak stoğu böler. Oluşturulmaz, karar kullanıcıda.
            if (item.ModelCode is not null
                && modelKeyRows.TryGetValue(ChannelCatalogMatcher.ModelColorSizeKey(item.ModelCode, item.Color, item.Size), out var sameRows)
                && sameRows.Count > 1)
            {
                ambiguous.Add((match, match with
                {
                    Reason = UnmatchedReasons.Ambiguous,
                    Detail = $"Dosyada aynı model + renk + beden {sameRows.Count} satırda var (satırlar: {string.Join(", ", sameRows.Take(10))}); " +
                             "aynı ürünün kopyası olabilir, SKU oluşturulmadı. Doğru SKU'yu elle bağlayın."
                }));
                continue;
            }

            var skuCode = ChooseSkuCode(item, stockCodeCounts, variantCodeCounts, usedCodes);
            if (skuCode is null)
            {
                continue; // eşleşmeyenlerde kalır
            }

            var key = ProductKey(item.ModelCode, item.Name, item.Brand);
            products.TryGetValue(key, out var product);
            if (product is null)
            {
                product = new ProductEntity
                {
                    Name = item.Name ?? item.ModelCode ?? item.Barcode!,
                    Brand = item.Brand,
                    Category = item.Category,
                    ModelCode = item.ModelCode
                };
                db.Products.Add(product);
                products[key] = product;
            }

            var sku = new SkuEntity { SkuCode = skuCode, Barcode = item.Barcode, Color = item.Color, Size = item.Size, Product = product };
            db.Skus.Add(sku);
            usedCodes.Add(skuCode);
            usedBarcodes.Add(item.Barcode!);

            var listing = new ChannelListingEntity
            {
                Sku = sku, ChannelId = channel.Id, ChannelBarcode = item.Barcode, ChannelProductCode = item.ChannelProductCode,
                Status = item.Passive ? ListingStatus.Passive : ListingStatus.Active, ChannelDataJson = ChannelData(item, source)
            };
            db.Listings.Add(listing);
            changed.Add(listing);
            state.IndexCodes(listing);

            // Id == 0: bu yüklemede oluşturulan ürün
            result.Add((match, skuCode, product.Id == 0 ? $"Yeni ürün: {product.Name}" : $"Mevcut ürüne eklendi: {product.Name}"));
        }
        return (result, ambiguous);
    }

    /// <summary>
    /// Yeni SKU kodu: (1) satıcı stok kodu dosyada ve sistemde tekilse o; (2) MODEL-RENK-BEDEN tekilse o; (3) barkod.
    /// </summary>
    private static string? ChooseSkuCode(ChannelCatalogItem item, Dictionary<string, int> stockCodeCounts, Dictionary<string, int> variantCodeCounts, HashSet<string> usedCodes)
    {
        if (item.StockCode is not null && stockCodeCounts.GetValueOrDefault(item.StockCode) == 1 && !usedCodes.Contains(item.StockCode))
        {
            return item.StockCode;
        }
        if (VariantCode(item) is { } variant && variantCodeCounts.GetValueOrDefault(variant) == 1 && !usedCodes.Contains(variant))
        {
            return variant;
        }
        return item.Barcode is not null && !usedCodes.Contains(item.Barcode) ? item.Barcode : null;
    }

    /// <summary>MODEL-RENK-BEDEN (ASCII büyük harf, boşluksuz). Model kodu yoksa null.</summary>
    internal static string? VariantCode(ChannelCatalogItem item)
    {
        if (item.ModelCode is null)
        {
            return null;
        }
        var parts = new[] { item.ModelCode, item.Color, item.Size }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => ExcelText.ToKey(p)).Where(p => p.Length > 0);
        var code = string.Join('-', parts);
        return code.Length is > 0 and <= Application.Catalog.CatalogLimits.SkuCode ? code : null;
    }

    private async Task UpsertUnmatchedAsync(int channelId, string source, List<CatalogMatch> unmatched, HashSet<string> resolvedKeys, string? user, DateTime now, CancellationToken ct)
    {
        var existing = (await db.UnmatchedChannelItems.Where(u => u.ChannelId == channelId).ToListAsync(ct))
            .ToDictionary(u => u.ChannelKey, StringComparer.Ordinal);

        foreach (var m in unmatched)
        {
            var item = m.Item;
            if (item.Key.Length == 0)
            {
                continue;
            }
            if (!existing.TryGetValue(item.Key, out var row))
            {
                row = new UnmatchedChannelItemEntity { ChannelId = channelId, ChannelKey = item.Key, FirstSeenUtc = now, Status = UnmatchedStatuses.Pending };
                db.UnmatchedChannelItems.Add(row);
                existing[item.Key] = row;
            }
            else if (row.Status == UnmatchedStatuses.Linked)
            {
                // Daha önce bağlanmıştı ama artık eşleşmiyor (eşleştirme silinmiş olabilir) → yeniden beklemeye
                row.Status = UnmatchedStatuses.Pending;
                row.ResolvedBy = null;
                row.ResolvedAtUtc = null;
            }

            row.Source = source;
            row.ChannelProductCode = item.ChannelProductCode;
            row.ChannelBarcode = item.Barcode;
            row.ChannelStockCode = item.StockCode;
            row.ModelCode = item.ModelCode;
            row.Color = item.Color;
            row.Size = item.Size;
            row.ProductName = item.Name;
            row.Brand = item.Brand;
            row.Category = item.Category;
            row.ChannelStatus = item.ChannelStatus;
            row.Reason = m.Reason!;
            row.ReasonDetail = Truncate(m.Detail, 400);
            row.SuggestedSkuId = m.Suggestion?.SkuId;
            row.SuggestionScore = m.SuggestionScore;
            row.LastSeenUtc = now;
        }

        foreach (var key in resolvedKeys)
        {
            if (existing.TryGetValue(key, out var row) && row.Status != UnmatchedStatuses.Linked)
            {
                row.Status = UnmatchedStatuses.Linked;
                row.ResolvedBy = Truncate($"otomatik ({user ?? "sistem"})", 100);
                row.ResolvedAtUtc = now;
                row.LastSeenUtc = now;
            }
        }
    }

    // ------------------------------------------------------------------ Eşleşmeyenler

    public async Task<PagedResult<UnmatchedItemDto>> SearchUnmatchedAsync(int? channelId, string? status, string? search, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var term = Text.Clean(search);
        var st = Text.Clean(status)?.ToUpperInvariant() ?? UnmatchedStatuses.Pending;

        var query = db.UnmatchedChannelItems.AsNoTracking();
        if (channelId is { } cid)
        {
            query = query.Where(u => u.ChannelId == cid);
        }
        if (st != "TUMU")
        {
            if (!UnmatchedStatuses.All.Contains(st))
            {
                throw new RequestValidationException(new Dictionary<string, string[]> { ["status"] = ["Durum BEKLIYOR, BAGLANDI, YOKSAYILDI veya TUMU olmalı."] });
            }
            query = query.Where(u => u.Status == st);
        }
        if (term is not null)
        {
            query = query.Where(u => u.ChannelBarcode == term || u.ChannelProductCode == term
                                     || (u.ChannelStockCode != null && u.ChannelStockCode.StartsWith(term))
                                     || (u.ModelCode != null && u.ModelCode.StartsWith(term))
                                     || (u.ProductName != null && u.ProductName.Contains(term)));
        }

        var total = await query.CountAsync(ct);
        var items = await (
                from u in query
                join c in db.Channels on u.ChannelId equals c.Id
                join s in db.Skus on u.SuggestedSkuId equals (int?)s.Id into sj
                from s in sj.DefaultIfEmpty()
                orderby u.LastSeenUtc descending, u.Id
                select new { u, c.Code, SkuCode = s == null ? null : s.SkuCode, ProductName = s == null ? null : s.Product.Name })
            .Skip((p - 1) * size).Take(size)
            .ToListAsync(ct);

        return new PagedResult<UnmatchedItemDto>(items.Select(x => new UnmatchedItemDto(
            x.u.Id, x.u.ChannelId, x.Code, x.u.Source, x.u.ChannelProductCode, x.u.ChannelBarcode, x.u.ChannelStockCode, x.u.ModelCode,
            x.u.Color, x.u.Size, x.u.ProductName, x.u.Brand, x.u.ChannelStatus, x.u.Reason, x.u.ReasonDetail,
            x.u.SuggestedSkuId, x.SkuCode, x.ProductName, x.u.SuggestionScore, x.u.Status,
            Utc(x.u.FirstSeenUtc), Utc(x.u.LastSeenUtc), x.u.ResolvedBy, x.u.ResolvedAtUtc is { } r ? Utc(r) : null)).ToList(), total, p, size);
    }

    public async Task<ListingDto> LinkUnmatchedAsync(int id, int skuId, string? user, CancellationToken ct)
    {
        var item = await db.UnmatchedChannelItems.FirstOrDefaultAsync(u => u.Id == id, ct)
                   ?? throw new NotFoundException($"Eşleşmeyen kayıt bulunamadı (Id={id}).");
        if (item.Status == UnmatchedStatuses.Linked)
        {
            throw new ConflictException("Bu kanal ürünü zaten bağlanmış.");
        }
        var sku = await db.Skus.AsNoTracking().Where(s => s.Id == skuId)
                      .Select(s => new CatalogSkuRef(s.Id, s.SkuCode, s.Barcode, s.Product.ModelCode, s.Color, s.Size, s.Product.Name, s.Product.Brand))
                      .FirstOrDefaultAsync(ct)
                  ?? throw new NotFoundException($"SKU bulunamadı (Id={skuId}).");
        var channel = await FindChannelAsync(item.ChannelId, ct);

        var listings = await db.Listings.Where(l => l.ChannelId == item.ChannelId).ToListAsync(ct);
        var state = new ListingIndex(listings);
        var passive = ChannelFileFormats.All.FirstOrDefault(f => f.ChannelCode == channel.Code)?.IsPassive(item.ChannelStatus) ?? false;
        var catalogItem = new ChannelCatalogItem(0, item.ChannelProductCode, item.ChannelBarcode, item.ChannelStockCode, item.ModelCode,
            item.Color, item.Size, item.ProductName, item.Brand, item.Category, item.ChannelStatus, passive);

        var skuCodes = await db.Skus.AsNoTracking()
            .Where(s => listings.Select(l => l.SkuId).Contains(s.Id))
            .Select(s => new CatalogSkuRef(s.Id, s.SkuCode, null, null, null, null, "", null))
            .ToDictionaryAsync(s => s.SkuId, ct);
        var conflict = state.FindConflict(sku, catalogItem, skuCodes);
        if (conflict is not null)
        {
            throw new ConflictException(conflict);
        }

        var changed = new List<ChannelListingEntity>();
        Upsert(state, sku.SkuId, item.ChannelId, catalogItem, item.Source, changed);
        var now = clock.GetUtcNow().UtcDateTime;
        item.Status = UnmatchedStatuses.Linked;
        item.ResolvedBy = Truncate(user, 100);
        item.ResolvedAtUtc = now;

        var hasBalance = await db.StockBalances.AnyAsync(b => b.SkuId == sku.SkuId, ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            if (hasBalance)
            {
                foreach (var listing in changed)
                {
                    await OutboxWriter.AddAsync(db, OutboxEvents.ListingChanged, new { ListingId = listing.Id }, ct);
                }
            }
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUnique(ex))
        {
            throw DbErrors.UniqueConflict(ex);
        }

        var listingId = state.BySku[sku.SkuId].Id;
        db.ChangeTracker.Clear();
        return await db.Listings.AsNoTracking().Where(l => l.Id == listingId)
            .Select(l => new ListingDto(l.Id, l.SkuId, l.Sku.SkuCode, l.ChannelId, l.Channel.Code,
                l.ChannelProductCode, l.ChannelBarcode, l.Status, l.LastSentStock, l.LastSyncUtc))
            .SingleAsync(ct);
    }

    public async Task IgnoreUnmatchedAsync(int id, string? user, CancellationToken ct)
    {
        var item = await db.UnmatchedChannelItems.FirstOrDefaultAsync(u => u.Id == id, ct)
                   ?? throw new NotFoundException($"Eşleşmeyen kayıt bulunamadı (Id={id}).");
        if (item.Status != UnmatchedStatuses.Pending)
        {
            throw new ConflictException("Yalnızca bekleyen kayıtlar yok sayılabilir.");
        }
        item.Status = UnmatchedStatuses.Ignored;
        item.ResolvedBy = Truncate(user, 100);
        item.ResolvedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ Yardımcılar

    private async Task<ChannelEntity> FindChannelAsync(int channelId, CancellationToken ct) =>
        await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId, ct)
        ?? throw new NotFoundException($"Kanal bulunamadı (Id={channelId}).");

    private static CatalogImportReport EmptyReport(string channelCode, string? format, string source, CatalogImportOptions options, IReadOnlyList<string> fileErrors) =>
        new(channelCode, format, source, options.ValidateOnly, options.CreateMissing, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            [], false, [], fileErrors, []);

    private static CatalogImportRow Row(ChannelCatalogItem item, string outcome, string? skuCode, string? step, string? change, string? detail, string? suggestion, decimal? score) =>
        new(item.Row, item.Barcode, item.ChannelProductCode, item.StockCode, item.Name, item.Color, item.Size,
            outcome, skuCode, step, change, detail, suggestion, score, item.Passive);

    private static string? StepName(CatalogMatchStep? step) => step switch
    {
        CatalogMatchStep.ExistingListing => "MEVCUT_ESLESTIRME",
        CatalogMatchStep.Barcode => "BARKOD",
        CatalogMatchStep.StockCode => "STOK_KODU",
        CatalogMatchStep.ModelColorSize => "MODEL_RENK_BEDEN",
        _ => null
    };

    private static int OutcomeOrder(string outcome) => outcome switch
    {
        CatalogRowOutcomes.Conflict => 0,
        CatalogRowOutcomes.Ambiguous => 1,
        CatalogRowOutcomes.NoMatch => 2,
        CatalogRowOutcomes.Created => 3,
        _ => 4
    };

    /// <summary>ChannelListing.KanalVerisi: yalnızca beyaz listedeki ürün alanları (kişisel veri yok).</summary>
    private static string ChannelData(ChannelCatalogItem item, string source) =>
        JsonSerializer.Serialize(new { kaynak = source, kanalDurumu = item.ChannelStatus, modelKodu = item.ModelCode, stokKodu = item.StockCode });

    private static string ProductKey(string? modelCode, string? name, string? brand) =>
        (modelCode is not null ? "M:" + modelCode : "N:" + CatalogText.Normalize(name)) + "\u001F" + CatalogText.Normalize(brand);

    private static bool Same(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private sealed class Counters
    {
        public int Existing, Barcode, StockCode, ModelColorSize, Created, Updated, Unchanged, SkusCreated, ProductsCreated;

        public void Count(CatalogMatchStep step, string change)
        {
            switch (step)
            {
                case CatalogMatchStep.ExistingListing: Existing++; break;
                case CatalogMatchStep.Barcode: Barcode++; break;
                case CatalogMatchStep.StockCode: StockCode++; break;
                case CatalogMatchStep.ModelColorSize: ModelColorSize++; break;
            }
            switch (change)
            {
                case "YENI": Created++; break;
                case "GUNCELLENDI": Updated++; break;
                default: Unchanged++; break;
            }
        }
    }

    /// <summary>Bir kanalın eşleştirmeleri: SKU, barkod ve kanal ürün koduna göre.</summary>
    private sealed class ListingIndex
    {
        public Dictionary<int, ChannelListingEntity> BySku { get; } = [];
        private readonly Dictionary<string, ChannelListingEntity> byBarcode = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ChannelListingEntity> byCode = new(StringComparer.Ordinal);

        public ListingIndex(IEnumerable<ChannelListingEntity> listings)
        {
            foreach (var l in listings)
            {
                BySku[l.SkuId] = l;
                IndexCodes(l);
            }
        }

        public void Add(ChannelListingEntity listing)
        {
            BySku[listing.SkuId] = listing;
            IndexCodes(listing);
        }

        public void Index(ChannelListingEntity listing) => IndexCodes(listing);

        public void IndexCodes(ChannelListingEntity listing)
        {
            if (listing.ChannelBarcode is not null)
            {
                byBarcode.TryAdd(listing.ChannelBarcode, listing);
            }
            if (listing.ChannelProductCode is not null)
            {
                byCode.TryAdd(listing.ChannelProductCode, listing);
            }
        }

        /// <summary>Bağlamak mevcut bir eşleştirmenin üzerine yazacaksa açıklama döner; yazılmaz.</summary>
        public string? FindConflict(CatalogSkuRef sku, ChannelCatalogItem item, IReadOnlyDictionary<int, CatalogSkuRef> skuById)
        {
            string Name(ChannelListingEntity l) => l.Sku?.SkuCode ?? (skuById.TryGetValue(l.SkuId, out var s) ? s.SkuCode : $"Id {l.SkuId}");

            if (item.Barcode is not null && byBarcode.TryGetValue(item.Barcode, out var lb) && lb.SkuId != sku.SkuId)
            {
                return $"Bu kanal barkodu bu kanalda başka bir SKU'ya bağlı ({Name(lb)}).";
            }
            if (item.ChannelProductCode is not null && byCode.TryGetValue(item.ChannelProductCode, out var lc) && lc.SkuId != sku.SkuId)
            {
                return $"Bu kanal ürün kodu bu kanalda başka bir SKU'ya bağlı ({Name(lc)}).";
            }
            if (BySku.TryGetValue(sku.SkuId, out var existing))
            {
                if (existing.ChannelBarcode is not null && item.Barcode is not null && !string.Equals(existing.ChannelBarcode, item.Barcode, StringComparison.Ordinal))
                {
                    return $"{sku.SkuCode} bu kanalda başka bir barkoda bağlı ({existing.ChannelBarcode}); üzerine yazılmadı.";
                }
                if (existing.ChannelProductCode is not null && item.ChannelProductCode is not null
                    && !string.Equals(existing.ChannelProductCode, item.ChannelProductCode, StringComparison.Ordinal))
                {
                    return $"{sku.SkuCode} bu kanalda başka bir kanal ürün koduna bağlı ({existing.ChannelProductCode}); üzerine yazılmadı.";
                }
            }
            return null;
        }
    }
}
