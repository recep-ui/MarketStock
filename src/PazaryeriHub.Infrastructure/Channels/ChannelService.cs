using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PazaryeriHub.Application.Catalog;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Sync;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.Channels;

/// <summary>Kanal ayarları ve SKU ↔ kanal ürün eşleştirmeleri (EF Core).</summary>
public sealed class ChannelService(PazaryeriHubDbContext db) : IChannelService
{
    public async Task<IReadOnlyList<ChannelDto>> GetChannelsAsync(CancellationToken ct) =>
        await db.Channels.AsNoTracking().OrderBy(c => c.Id)
            .Select(c => new ChannelDto(c.Id, c.Code, c.Name, c.SharePercent, c.Active, c.SettingsJson, c.EncryptedCredentials != null))
            .ToListAsync(ct);

    public async Task<ChannelDto> UpdateChannelAsync(int id, ChannelUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        var v = new Validator();
        v.Required("name", update.Name, CatalogLimits.ChannelName)
         .Must(update.SharePercent is >= 0 and <= 100, "sharePercent", "Kanal payı 0 ile 100 arasında olmalı.")
         .Must(decimal.Round(update.SharePercent, 2) == update.SharePercent, "sharePercent", "Kanal payı en fazla 2 ondalık basamak içerebilir.")
         .Must(IsValidJsonObjectOrNull(update.SettingsJson), "settingsJson", "Ayarlar geçerli bir JSON nesnesi olmalı.");
        v.ThrowIfInvalid();

        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == id, ct)
                      ?? throw new NotFoundException($"Kanal bulunamadı (Id={id}).");
        var affectsStock = channel.SharePercent != update.SharePercent || channel.Active != update.Active
                           || !string.Equals(channel.SettingsJson, Text.Clean(update.SettingsJson), StringComparison.Ordinal);
        channel.Name = update.Name.Trim();
        channel.SharePercent = update.SharePercent;
        channel.Active = update.Active;
        channel.SettingsJson = Text.Clean(update.SettingsJson);

        // Pay/aktiflik/ayar değişirse kanalın tüm eşleştirmeleri yeniden hesaplanıp gönderilir (aynı transaction'da olay).
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        if (affectsStock)
        {
            await OutboxWriter.AddAsync(db, OutboxEvents.ChannelRefresh, new { ChannelId = channel.Id }, ct);
        }
        await tx.CommitAsync(ct);

        return new ChannelDto(channel.Id, channel.Code, channel.Name, channel.SharePercent, channel.Active, channel.SettingsJson,
            channel.EncryptedCredentials is not null);
    }

    public async Task<PagedResult<ListingDto>> SearchListingsAsync(int? channelId, string? search, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var term = Text.Clean(search);

        var query = db.Listings.AsNoTracking();
        if (channelId is { } cid)
        {
            query = query.Where(l => l.ChannelId == cid);
        }
        if (term is not null)
        {
            // Kanaldaki ad KanalVerisi JSON'unda (Türkçe karakterler kaçışlı saklanabilir) → JSON_VALUE ile okunur (ADR-044)
            var pattern = "%" + term.Replace("[", "[[]", StringComparison.Ordinal).Replace("%", "[%]", StringComparison.Ordinal)
                .Replace("_", "[_]", StringComparison.Ordinal) + "%";
            var byChannelName = db.Database.SqlQuery<int>(
                $"SELECT Id AS [Value] FROM dbo.ChannelListing WHERE JSON_VALUE(KanalVerisi, '$.urunAdi') LIKE {pattern}");
            query = query.Where(l => l.Sku.SkuCode.StartsWith(term) || l.ChannelProductCode == term || l.ChannelBarcode == term
                || l.Sku.Product.Name.Contains(term) || byChannelName.Contains(l.Id));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(l => l.Sku.SkuCode).ThenBy(l => l.ChannelId)
            .Skip((p - 1) * size).Take(size)
            .Select(l => new
            {
                Dto = new ListingDto(l.Id, l.SkuId, l.Sku.SkuCode, l.ChannelId, l.Channel.Code,
                    l.ChannelProductCode, l.ChannelBarcode, l.Status, l.LastSentStock, l.LastSyncUtc, null, null),
                l.ChannelDataJson,
                ProductName = l.Sku.Product.Name, Brand = l.Sku.Product.Brand, SkuBarcode = l.Sku.Barcode, l.Sku.Color, l.Sku.Size
            })
            .ToListAsync(ct);
        var items = rows.Select(r =>
        {
            var data = ListingChannelData.Parse(r.ChannelDataJson);
            // Eşleşme dayanağı ve uyarı sayısı kanal sekmesindeki kontrolle aynı kuraldan (ADR-035); onay durumu orada izlenir
            var check = PazaryeriHub.Application.Panel.ListingMatchCheck.Evaluate(
                new(r.Dto.ChannelBarcode, r.Dto.ChannelProductCode, data.StockCode, data.Name, data.Brand, data.Color, data.Size, data.Variant),
                new(r.Dto.SkuCode, r.SkuBarcode, r.ProductName, r.Brand, r.Color, r.Size));
            return r.Dto with
            {
                ProductUrl = data.ProductUrl, Variant = data.Variant, ChannelName = data.Name, ImageUrl = data.ImageUrl,
                ChannelStatus = data.ChannelStatus, ProductName = r.ProductName, SkuBarcode = r.SkuBarcode, SkuColor = r.Color, SkuSize = r.Size,
                MatchBasis = check.Basis, WarningCount = check.Warnings.Count
            };
        }).ToList();

        return new PagedResult<ListingDto>(items, total, p, size);
    }

    public async Task<ListingDto> UpsertListingAsync(ListingInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var status = Text.Clean(input.Status)?.ToUpperInvariant() ?? ListingStatus.Active;
        var v = new Validator();
        v.MaxLength("channelProductCode", input.ChannelProductCode, CatalogLimits.ChannelProductCode)
         .MaxLength("channelBarcode", input.ChannelBarcode, CatalogLimits.ChannelBarcode)
         .Must(Text.Clean(input.ChannelProductCode) is not null || Text.Clean(input.ChannelBarcode) is not null,
               "channelProductCode", "Kanal ürün kodu veya kanal barkodundan en az biri girilmeli.")
         .Must(ListingStatus.All.Contains(status), "status", "Durum AKTIF veya PASIF olmalı.");
        v.ThrowIfInvalid();

        if (!await db.Skus.AnyAsync(s => s.Id == input.SkuId, ct))
        {
            throw new NotFoundException($"SKU bulunamadı (Id={input.SkuId}).");
        }
        if (!await db.Channels.AnyAsync(c => c.Id == input.ChannelId, ct))
        {
            throw new NotFoundException($"Kanal bulunamadı (Id={input.ChannelId}).");
        }

        var barcode = Text.Clean(input.ChannelBarcode);
        var code = Text.Clean(input.ChannelProductCode);
        ChannelListingEntity? listing;
        if (input.Id is { } listingId)
        {
            listing = await db.Listings.FirstOrDefaultAsync(l => l.Id == listingId, ct)
                      ?? throw new NotFoundException($"Eşleştirme bulunamadı (Id={listingId}).");
            if (listing.ChannelId != input.ChannelId)
            {
                throw new ConflictException("İlanın kanalı değiştirilemez; yeni eşleştirme ekleyin.");
            }
        }
        else
        {
            // İlan kanal içinde barkod / kanal ürün koduyla tanınır (ADR-023): aynı SKU'nun başka ilanının üzerine yazılmaz.
            var matches = await db.Listings
                .Where(l => l.ChannelId == input.ChannelId && ((barcode != null && l.ChannelBarcode == barcode) || (code != null && l.ChannelProductCode == code)))
                .ToListAsync(ct);
            if (matches.Count > 1)
            {
                throw new ConflictException("Kanal barkodu ve kanal ürün kodu bu kanalda farklı ilanlara bağlı.");
            }
            listing = matches.SingleOrDefault();
            if (listing is not null && listing.SkuId != input.SkuId)
            {
                var ownerId = listing.SkuId;
                var owner = await db.Skus.Where(s => s.Id == ownerId).Select(s => s.SkuCode).FirstOrDefaultAsync(ct);
                throw new ConflictException($"Bu barkod/kod bu kanalda başka bir SKU'ya ({owner}) bağlı. Önce o eşleştirmeyi silin.");
            }
        }
        if (listing is null)
        {
            listing = new ChannelListingEntity { SkuId = input.SkuId, ChannelId = input.ChannelId };
            db.Listings.Add(listing);
        }
        listing.SkuId = input.SkuId;
        listing.ChannelProductCode = code;
        listing.ChannelBarcode = barcode;
        listing.Status = status;

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            await OutboxWriter.AddAsync(db, OutboxEvents.ListingChanged, new { ListingId = listing.Id }, ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUnique(ex))
        {
            throw DbErrors.UniqueConflict(ex);
        }

        return await db.Listings.AsNoTracking().Where(l => l.Id == listing.Id)
            .Select(l => new ListingDto(l.Id, l.SkuId, l.Sku.SkuCode, l.ChannelId, l.Channel.Code,
                l.ChannelProductCode, l.ChannelBarcode, l.Status, l.LastSentStock, l.LastSyncUtc))
            .SingleAsync(ct);
    }

    public async Task DeleteListingAsync(int id, CancellationToken ct)
    {
        var listing = await db.Listings.FirstOrDefaultAsync(l => l.Id == id, ct)
                      ?? throw new NotFoundException($"Eşleştirme bulunamadı (Id={id}).");
        db.Listings.Remove(listing);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsReferenceViolation(ex))
        {
            throw new ConflictException("Eşleştirmeye bağlı senkron veya mutabakat kaydı var; silmek yerine PASIF yapın.", ex);
        }
    }

    private static bool IsValidJsonObjectOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
