using PazaryeriHub.Application.Common;

namespace PazaryeriHub.Application.Channels;

/// <summary>Kanal bilgisi. Şifreli kimlik bilgisi (KimlikSifreli) hiçbir yanıtta dönmez; yalnızca var/yok bilgisi verilir.</summary>
public sealed record ChannelDto(int Id, string Code, string Name, decimal SharePercent, bool Active, string? SettingsJson, bool HasCredentials);

public sealed record ChannelUpdate(string Name, decimal SharePercent, bool Active, string? SettingsJson);

public static class ListingStatus
{
    public const string Active = "AKTIF";
    public const string Passive = "PASIF";
    public static readonly string[] All = [Active, Passive];
}

/// <summary>SKU ↔ kanal ürün eşleştirmesi (ChannelListing).</summary>
public sealed record ListingDto(
    int Id, int SkuId, string SkuCode, int ChannelId, string ChannelCode,
    string? ChannelProductCode, string? ChannelBarcode, string Status,
    int? LastSentStock, DateTime? LastSyncUtc);

/// <summary>
/// Eşleştirme ekle/güncelle. Bir SKU bir kanalda birden fazla ilana sahip olabilir (ADR-023); ilan kanal içinde barkod veya
/// kanal ürün koduyla tanınır. <paramref name="Id"/> verilirse o ilan güncellenir; verilmezse aynı barkod/kodlu ilan aranır,
/// yoksa yeni ilan açılır.
/// </summary>
public sealed record ListingInput(int SkuId, int ChannelId, string? ChannelProductCode, string? ChannelBarcode, string? Status, int? Id = null);

public interface IChannelService
{
    Task<IReadOnlyList<ChannelDto>> GetChannelsAsync(CancellationToken ct);
    Task<ChannelDto> UpdateChannelAsync(int id, ChannelUpdate update, CancellationToken ct);

    Task<PagedResult<ListingDto>> SearchListingsAsync(int? channelId, string? search, int? page, int? pageSize, CancellationToken ct);

    /// <summary>(SkuId, ChannelId) için eşleştirmeyi ekler veya günceller.</summary>
    Task<ListingDto> UpsertListingAsync(ListingInput input, CancellationToken ct);
    Task DeleteListingAsync(int id, CancellationToken ct);
}
