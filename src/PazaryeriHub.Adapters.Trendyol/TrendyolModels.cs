namespace PazaryeriHub.Adapters.Trendyol;

// Trendyol API v2.0 yanıt/istek modelleri. Yalnızca resmi dokümanda adı geçen ve bizim kullandığımız alanlar.
// Kaynaklar (developers.trendyol.com/v2.0/reference/...): updatepriceandinventory, getbatchrequestresult,
// getshipmentpackages, getclaims, filterapprovedproductsinventoryandprice, filterapprovedproducts.

internal sealed record PriceAndInventoryRequest(IReadOnlyList<PriceAndInventoryItem> Items);

/// <summary>Yalnızca stok gönderilir: "When updating only stock or price, sending the other field is not required."</summary>
internal sealed record PriceAndInventoryItem(string Barcode, int Quantity);

internal sealed record BatchRequestCreated(string? BatchRequestId);

internal sealed class BatchRequestResult
{
    public string? BatchRequestId { get; init; }

    /// <summary>COMPLETED | IN_PROGRESS</summary>
    public string? Status { get; init; }

    public int? ItemCount { get; init; }
    public int? FailedItemCount { get; init; }
    public List<BatchRequestItem> Items { get; init; } = [];
}

internal sealed class BatchRequestItem
{
    /// <summary>Dokümanda şeması verilmemiş (object). Gönderdiğimiz kalemin barkodunu taşıdığı varsayılır. // TODO: doğrulanmalı</summary>
    public System.Text.Json.JsonElement? RequestItem { get; init; }

    /// <summary>SUCCESS | FAILED</summary>
    public string? Status { get; init; }

    public List<string> FailureReasons { get; init; } = [];
}

internal sealed class PagedResponse<T>
{
    public int Page { get; init; }
    public int Size { get; init; }
    public int TotalPages { get; init; }
    public long TotalElements { get; init; }
    public string? NextPageToken { get; init; }
    public List<T> Content { get; init; } = [];
}

internal sealed class ShipmentPackage
{
    /// <summary>Paket kimliği (dokümanda ayrı shipmentPackageId alanı yok; "id").</summary>
    public long Id { get; init; }
    public string? OrderNumber { get; init; }
    public string? Status { get; init; }
    public decimal? TotalPrice { get; init; }
    public string? CustomerFirstName { get; init; }
    public string? CustomerLastName { get; init; }
    public Address? ShipmentAddress { get; init; }
    public string? CargoProviderName { get; init; }

    /// <summary>Dokümanda string; bazı yanıtlarda sayı gelebilir. // TODO: doğrulanmalı</summary>
    public System.Text.Json.JsonElement? CargoTrackingNumber { get; init; }
    public string? CargoTrackingLink { get; init; }
    public List<PackageHistory> PackageHistory { get; init; } = [];
    public List<OrderLine> Lines { get; init; } = [];
}

internal sealed class Address
{
    public string? City { get; init; }
    public string? District { get; init; }
}

internal sealed class PackageHistory
{
    /// <summary>Epoch ms.</summary>
    public long? CreatedDate { get; init; }
    public string? Status { get; init; }
}

internal sealed class OrderLine
{
    public long Id { get; init; }
    public string? Barcode { get; init; }
    public string? MerchantSku { get; init; }
    public int Quantity { get; init; }

    /// <summary>Birim fiyat olduğu varsayılır ("amount" satır tutarı). // TODO: doğrulanmalı</summary>
    public decimal? Price { get; init; }
    public decimal? Amount { get; init; }
}

internal sealed class Claim
{
    public string? Id { get; init; }
    public string? ClaimId { get; init; }
    public string? OrderNumber { get; init; }
    public long? OrderShipmentPackageId { get; init; }

    /// <summary>Epoch ms ("timestamp in GMT").</summary>
    public long? ClaimDate { get; init; }
    public List<ClaimItemGroup> Items { get; init; } = [];
}

internal sealed class ClaimItemGroup
{
    public ClaimOrderLine? OrderLine { get; init; }
    public List<ClaimItem> ClaimItems { get; init; } = [];
}

internal sealed class ClaimOrderLine
{
    public long Id { get; init; }
    public string? Barcode { get; init; }
    public string? MerchantSku { get; init; }
}

internal sealed class ClaimItem
{
    public string? Id { get; init; }
    public ClaimReason? CustomerClaimItemReason { get; init; }
    public ClaimStatus? ClaimItemStatus { get; init; }
}

internal sealed class ClaimReason
{
    public string? Name { get; init; }
}

internal sealed class ClaimStatus
{
    public string? Name { get; init; }
}

/// <summary>filterApprovedProducts içeriği (ürün grubu). Kaynak: /v2.0/reference/filterapprovedproducts (2026-11-17 okundu).</summary>
internal sealed class ApprovedContent
{
    public string? ProductMainId { get; init; }
    public NamedRef? Brand { get; init; }
    public NamedRef? Category { get; init; }
    public string? Title { get; init; }
    public List<ContentAttribute> Attributes { get; init; } = [];
    public List<ApprovedVariant> Variants { get; init; } = [];
}

internal sealed class NamedRef
{
    public string? Name { get; init; }
}

internal sealed class ContentAttribute
{
    public string? AttributeName { get; init; }
    public List<AttributeValueRef> AttributeValues { get; init; } = [];
}

internal sealed class AttributeValueRef
{
    public string? AttributeValue { get; init; }
}

internal sealed class ApprovedVariant
{
    public string? Barcode { get; init; }
    public string? StockCode { get; init; }
    public bool? OnSale { get; init; }
    public bool? Locked { get; init; }
    public bool? Archived { get; init; }
    public bool? Blacklisted { get; init; }
    public List<VariantAttribute> Attributes { get; init; } = [];
}

internal sealed class VariantAttribute
{
    public string? AttributeName { get; init; }
    public string? AttributeValue { get; init; }
}

internal sealed class ApprovedProduct
{
    public List<ProductVariant> Variants { get; init; } = [];
}

internal sealed class ProductVariant
{
    public string? Barcode { get; init; }
    public int? Quantity { get; init; }
}

// ------------------------------------------------------------------ Satıcı Bilgileri (ADR-025)

/// <summary>Kaynak: Cargo Providers Service (GET /product/lookup/cargo-providers) → [{code, name}].</summary>
internal sealed record CargoProvider(string? Code, string? Name);

/// <summary>Kaynak: getClaimsIssueReasons (GET /order/claim-issue-reasons) → [{id, name}].</summary>
internal sealed record ClaimIssueReason(long? Id, string? Name);
