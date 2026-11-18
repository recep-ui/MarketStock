using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Domain.Pricing;

namespace PazaryeriHub.Application.Pricing;

/// <summary>AppSetting anahtarları (fiyat).</summary>
public static class PriceSettingKeys
{
    /// <summary>Son gönderilen fiyata göre bu yüzdeden fazla değişim otomatik gönderilmez, Yönetici onayı bekler (varsayılan 30).</summary>
    public const string ChangeThresholdPercent = "FiyatSapmaEsigiYuzde";

    public const decimal DefaultChangeThresholdPercent = 30m;
}

public static class SyncTypes
{
    public const string Stock = "STOK";
    public const string Price = "FIYAT";
}

public static class PriceApprovalStates
{
    public const string Pending = "BEKLIYOR";
    public const string Approved = "ONAYLANDI";
    public const string Rejected = "REDDEDILDI";

    /// <summary>Hesaplanan fiyat değişti veya artık onaya gerek yok; karar verilmeden kapandı.</summary>
    public const string Obsolete = "GECERSIZ";
}

public static class PriceApprovalReasons
{
    /// <summary>Değişim eşiği aşıldı.</summary>
    public const string Threshold = "ESIK";

    /// <summary>FIYAT yeteneği DENEME: her değişiklik tek tek onaylanır.</summary>
    public const string Trial = "DENEME";
}

/// <summary>Kanal fiyat kuralı (Channel.Ayarlar.fiyatKurali). Yuvarlama: YOK | TAM | KURUS_99 | KURUS_90.</summary>
public sealed record ChannelPriceRuleDto(decimal Multiplier, decimal FixedAdd, string Rounding, decimal? Floor)
{
    public static ChannelPriceRuleDto From(PriceRule rule) => new(rule.Multiplier, rule.FixedAdd, PriceRoundingCodes.ToCode(rule.Rounding), rule.Floor);
}

public static class PriceRoundingCodes
{
    public static IReadOnlyList<string> All { get; } = ["YOK", "TAM", "KURUS_99", "KURUS_90"];

    public static string ToCode(PriceRounding rounding) => rounding switch
    {
        PriceRounding.Whole => "TAM",
        PriceRounding.Ends99 => "KURUS_99",
        PriceRounding.Ends90 => "KURUS_90",
        _ => "YOK"
    };

    public static bool TryParse(string? code, out PriceRounding rounding)
    {
        switch (code?.Trim().ToUpperInvariant())
        {
            case null or "" or "YOK": rounding = PriceRounding.None; return true;
            case "TAM": rounding = PriceRounding.Whole; return true;
            case "KURUS_99": rounding = PriceRounding.Ends99; return true;
            case "KURUS_90": rounding = PriceRounding.Ends90; return true;
            default: rounding = PriceRounding.None; return false;
        }
    }
}

/// <summary>Kanalın fiyat durumu: kural, adaptörün fiyat kuralı (KDV), yetenek durumu.</summary>
public sealed record ChannelPriceInfo(
    int ChannelId, string ChannelCode, ChannelPriceRuleDto Rule, string CapabilityState,
    bool Supported, bool? VatIncluded, bool SendsListPrice, bool SamePriceForProductVariants, string Note, string? Source,
    bool CanEnable, string? BlockReason);

/// <param name="ListPrice">Liste (üstü çizili) fiyat; boşsa satış fiyatı kullanılır.</param>
/// <param name="SalePrice">Satış fiyatı; null → fiyat kaldırılır (kanala fiyat gitmez).</param>
public sealed record SkuPriceInput(decimal? ListPrice, decimal? SalePrice);

public sealed record SkuPriceDto(int SkuId, string SkuCode, string ProductName, decimal? ListPrice, decimal? SalePrice);

/// <param name="Price">İlan için elle satış fiyatı; null → kaldırılır (kanal kuralı uygulanır).</param>
public sealed record ListingPriceOverrideInput(decimal? Price);

public sealed record ListingPriceDto(int ListingId, decimal? Override, decimal? LastSentSale, decimal? LastSentList, DateTime? LastPriceSyncUtc);

/// <summary>Önizleme satırı durumu.</summary>
public static class PricePreviewStatus
{
    public const string Change = "DEGISECEK";
    public const string Same = "AYNI";
    public const string NeedsApproval = "ONAY_GEREKIR";
    public const string Blocked = "GONDERILEMEZ";
    public const string Rejected = "REDDEDILDI";
}

public sealed record PricePreviewRow(
    int ListingId, int SkuId, string SkuCode, string ProductName, string? ChannelProductCode, string? ChannelBarcode,
    decimal? LastSentSale, decimal? LastSentList, decimal? NewSale, decimal? NewList, decimal? ChangePercent,
    bool FloorApplied, bool FromOverride, string Status, string? Problem, long? PendingApprovalId);

/// <summary>"N ilanın fiyatı değişecek" önizlemesi (ADR-028). Yeteneği açmadan önce Yönetici görür.</summary>
public sealed record PricePreview(
    int ChannelId, string ChannelCode, string CapabilityState, bool CanEnable, string? BlockReason, decimal ThresholdPercent,
    int Total, int Changing, int FirstSend, int NeedsApproval, int Unchanged, int Blocked, IReadOnlyList<PricePreviewRow> Rows);

public sealed record PriceApprovalDto(
    long Id, int ListingId, int ChannelId, string ChannelCode, string SkuCode, string ProductName,
    decimal? OldSale, decimal NewSale, decimal NewList, decimal? ChangePercent, string Reason, string State,
    DateTime CreatedUtc, string? DecidedBy, DateTime? DecidedUtc);

public sealed record PriceHistoryDto(
    long Id, string Type, int? SkuId, string? SkuCode, int? ListingId, string? ChannelCode,
    decimal? OldSale, decimal? NewSale, decimal? OldList, decimal? NewList, string? Detail, string Source, string? User, DateTime AtUtc);

public sealed record PriceSettingsDto(decimal ChangeThresholdPercent);

/// <summary>Excel/panel toplu fiyat satırı.</summary>
public sealed record SkuPriceChange(int SkuId, decimal? ListPrice, decimal? SalePrice);

public sealed record SkuPriceImportResult(int Updated, int Unchanged);

public interface IPriceService
{
    Task<SkuPriceDto> GetSkuPriceAsync(int skuId, CancellationToken ct);

    /// <summary>SKU fiyatını değiştirir; PriceHistory + FiyatDegisti olayı aynı transaction'da (Yönetici).</summary>
    Task<SkuPriceDto> SetSkuPriceAsync(int skuId, SkuPriceInput input, string? user, CancellationToken ct);

    /// <summary>Toplu (Excel). Değişmeyen satırlar yazılmaz. Satırlar önceden doğrulanmış olmalı.</summary>
    Task<SkuPriceImportResult> ApplySkuPricesAsync(IReadOnlyList<SkuPriceChange> changes, string source, string? user, bool validateOnly, CancellationToken ct);

    Task<IReadOnlyList<ChannelPriceInfo>> GetChannelsAsync(CancellationToken ct);
    Task<ChannelPriceInfo> SetChannelRuleAsync(int channelId, ChannelPriceRuleDto rule, string? user, CancellationToken ct);

    Task<ListingPriceDto> SetListingOverrideAsync(int listingId, ListingPriceOverrideInput input, string? user, CancellationToken ct);

    /// <summary>Kanalın aktif ilanları için hesaplanan fiyatlar ve gönderilecek değişiklikler (kanala çağrı yok).</summary>
    Task<PricePreview> GetPreviewAsync(int channelId, bool onlyChanges, CancellationToken ct);

    Task<PagedResult<PriceApprovalDto>> SearchApprovalsAsync(string? state, int? channelId, int? page, int? pageSize, CancellationToken ct);
    Task<PriceApprovalDto> ApproveAsync(long id, string? user, CancellationToken ct);
    Task<PriceApprovalDto> RejectAsync(long id, string? user, CancellationToken ct);

    Task<PagedResult<PriceHistoryDto>> GetHistoryAsync(int? skuId, int? listingId, int? channelId, int? page, int? pageSize, CancellationToken ct);

    Task<PriceSettingsDto> GetSettingsAsync(CancellationToken ct);
    Task<PriceSettingsDto> SetSettingsAsync(PriceSettingsDto settings, string? user, CancellationToken ct);
}

/// <summary>Bir ilan için fiyat senkron kararı (saf; <see cref="PriceSyncPolicy"/>).</summary>
public enum PriceSyncAction
{
    /// <summary>Kanala gönderilecek değer son gönderilenle aynı.</summary>
    NoChange,

    /// <summary>Kuyruğa al / gönder.</summary>
    Send,

    /// <summary>Yönetici onayı gerekir (eşik aşıldı veya yetenek DENEME).</summary>
    NeedsApproval,

    /// <summary>Gönderilemez (fiyat yok, kural geçersiz, varyant fiyatları farklı…).</summary>
    Blocked,

    /// <summary>Bu değer Yönetici tarafından reddedildi; yeni bir değer hesaplanana kadar gönderilmez.</summary>
    Rejected
}

public sealed record PriceSyncDecision(PriceSyncAction Action, string? Reason, decimal? ChangePercent)
{
    public string? ApprovalReason { get; init; }
}

/// <summary>
/// Fiyat senkron kararı (ADR-028; saf fonksiyon, birim testli). Sıra: gönderilemez → aynı → onaylanmış → reddedilmiş → DENEME → eşik → gönder.
/// </summary>
public static class PriceSyncPolicy
{
    /// <param name="approved">Son başarılı gönderimden SONRA onaylanmış değer (satış, liste).</param>
    /// <param name="rejected">Son başarılı gönderimden sonra reddedilmiş değer.</param>
    public static PriceSyncDecision Decide(
        PriceTarget target, decimal? lastSale, decimal? lastList, decimal thresholdPercent, string capabilityState,
        (decimal Sale, decimal List)? approved, (decimal Sale, decimal List)? rejected)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.Sendable)
        {
            return new(PriceSyncAction.Blocked, target.Problem, null);
        }

        var change = PriceAllocator.ChangePercent(lastSale, target.SalePrice);
        if (lastSale == target.SalePrice && lastList == target.ListPrice)
        {
            return new(PriceSyncAction.NoChange, null, change);
        }
        if (approved is { } a && a.Sale == target.SalePrice && a.List == target.ListPrice)
        {
            return new(PriceSyncAction.Send, "Yönetici onayladı.", change);
        }
        if (rejected is { } r && r.Sale == target.SalePrice && r.List == target.ListPrice)
        {
            return new(PriceSyncAction.Rejected, "Bu fiyat Yönetici tarafından reddedildi.", change);
        }
        if (capabilityState == CapabilityStates.Trial)
        {
            return new(PriceSyncAction.NeedsApproval, "FIYAT yeteneği DENEME: her değişiklik onaylanır.", change) { ApprovalReason = PriceApprovalReasons.Trial };
        }
        if (PriceAllocator.ExceedsThreshold(lastSale, target.SalePrice, thresholdPercent))
        {
            return new(PriceSyncAction.NeedsApproval, $"Değişim %{change} > eşik %{thresholdPercent}.", change) { ApprovalReason = PriceApprovalReasons.Threshold };
        }
        return new(PriceSyncAction.Send, null, change);
    }
}
