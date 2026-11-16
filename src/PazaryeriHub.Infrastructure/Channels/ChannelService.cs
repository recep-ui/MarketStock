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
            query = query.Where(l => l.Sku.SkuCode.StartsWith(term) || l.ChannelProductCode == term || l.ChannelBarcode == term);
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(l => l.Sku.SkuCode).ThenBy(l => l.ChannelId)
            .Skip((p - 1) * size).Take(size)
            .Select(l => new ListingDto(l.Id, l.SkuId, l.Sku.SkuCode, l.ChannelId, l.Channel.Code,
                l.ChannelProductCode, l.ChannelBarcode, l.Status, l.LastSentStock, l.LastSyncUtc))
            .ToListAsync(ct);

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

        var listing = await db.Listings.FirstOrDefaultAsync(l => l.SkuId == input.SkuId && l.ChannelId == input.ChannelId, ct);
        if (listing is null)
        {
            listing = new ChannelListingEntity { SkuId = input.SkuId, ChannelId = input.ChannelId };
            db.Listings.Add(listing);
        }
        listing.ChannelProductCode = Text.Clean(input.ChannelProductCode);
        listing.ChannelBarcode = Text.Clean(input.ChannelBarcode);
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
