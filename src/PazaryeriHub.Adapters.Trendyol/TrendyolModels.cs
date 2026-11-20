namespace PazaryeriHub.Adapters.Trendyol;

// Trendyol API v2.0 yanıt/istek modelleri. Yalnızca resmi dokümanda adı geçen ve bizim kullandığımız alanlar.
// Kaynaklar (developers.trendyol.com/v2.0/reference/...): updatepriceandinventory, getbatchrequestresult,
// getshipmentpackages, getclaims, filterapprovedproductsinventoryandprice, filterapprovedproducts.

internal sealed record PriceAndInventoryRequest(IReadOnlyList<PriceAndInventoryItem> Items);

/// <summary>Yalnızca stok gönderilir: "When updating only stock or price, sending the other field is not required."</summary>
internal sealed record PriceAndInventoryItem(string Barcode, int Quantity);

/// <summary>Yalnızca fiyat (ADR-028): aynı uç, <c>quantity</c> gönderilmez ("sending the other field is not required").</summary>
internal sealed record PriceOnlyRequest(IReadOnlyList<PriceOnlyItem> Items);

internal sealed record PriceOnlyItem(string Barcode, decimal SalePrice, decimal ListPrice);

internal sealed record BatchRequestCreated(string? BatchRequestId);

/// <summary>Soru (Getting Customer Questions; ADR-029). Müşteri alanları (customerId, userName) bilerek modellenmez → okunmaz.</summary>
internal sealed class QnaQuestion
{
    public long Id { get; init; }
    public string? Text { get; init; }
    public string? Status { get; init; }
    public long? CreationDate { get; init; }
    public string? ProductName { get; init; }
    public string? ProductMainId { get; init; }
    public QnaAnswer? Answer { get; init; }
}

internal sealed class QnaAnswer
{
    public string? Text { get; init; }
}

internal sealed record QnaAnswerBody(string Text);

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

/// <summary>
/// Sipariş paketi. Kaynak: getShipmentPackages, Order V2 (<c>/order/sellers/{sellerId}/v2/orders</c>; 15.10.2026'dan itibaren
/// zorunlu, 2026-11-18 okundu). V2 alanları (shipmentPackageId, shipmentPackageStatus, packageHistories, packageTotalPrice,
/// orderDate, lines[].lineId/lineUnitPrice/stockCode) önceliklidir; eski alanlar (id, status, packageHistory, totalPrice,
/// lines[].id/price/merchantSku) geriye uyumluluk için okunmaya devam eder.
/// </summary>
internal sealed class ShipmentPackage
{
    /// <summary>Eski uç: paket kimliği "id".</summary>
    public long Id { get; init; }

    /// <summary>V2: paket kimliği.</summary>
    public long? ShipmentPackageId { get; init; }
    public string? OrderNumber { get; init; }
    public string? Status { get; init; }

    /// <summary>V2: paket durumu (status ile aynı değer kümesi).</summary>
    public string? ShipmentPackageStatus { get; init; }
    public decimal? TotalPrice { get; init; }

    /// <summary>V2: "Amount to be invoiced".</summary>
    public decimal? PackageTotalPrice { get; init; }

    /// <summary>V2: sipariş tarihi (epoch ms).</summary>
    public long? OrderDate { get; init; }

    public long PackageKey => ShipmentPackageId ?? Id;
    public string? EffectiveStatus => ShipmentPackageStatus ?? Status;
    public string? CustomerFirstName { get; init; }
    public string? CustomerLastName { get; init; }
    public Address? ShipmentAddress { get; init; }
    public string? CargoProviderName { get; init; }

    /// <summary>Dokümanda string; bazı yanıtlarda sayı gelebilir. // TODO: doğrulanmalı</summary>
    public System.Text.Json.JsonElement? CargoTrackingNumber { get; init; }
    public string? CargoTrackingLink { get; init; }
    public List<PackageHistory> PackageHistory { get; init; } = [];

    /// <summary>V2 adı.</summary>
    public List<PackageHistory> PackageHistories { get; init; } = [];
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
    /// <summary>Eski uç: satır kimliği.</summary>
    public long Id { get; init; }

    /// <summary>V2: satır kimliği; updatePackage (Picking) isteğinde <c>lines[].lineId</c> olarak gönderilir.</summary>
    public long? LineId { get; init; }
    public string? Barcode { get; init; }
    public string? MerchantSku { get; init; }

    /// <summary>V2: satıcı stok kodu.</summary>
    public string? StockCode { get; init; }
    public int Quantity { get; init; }

    /// <summary>Eski uç: birim fiyat olduğu varsayılır. // TODO: doğrulanmalı</summary>
    public decimal? Price { get; init; }
    public decimal? Amount { get; init; }

    /// <summary>V2: birim fiyat.</summary>
    public decimal? LineUnitPrice { get; init; }

    public long LineKey => LineId ?? Id;
}

/// <summary>Kaynak: Notify Packages (updatePackage), <c>PUT /order/sellers/{sellerId}/shipment-packages/{packageId}</c>.</summary>
internal sealed record UpdatePackageRequest(IReadOnlyList<UpdatePackageLine> Lines, Dictionary<string, string> Params, string Status);

internal sealed record UpdatePackageLine(long LineId, int Quantity);

/// <summary>Kaynak: Shipping Alternative Delivery, <c>PUT …/shipment-packages/{packageId}/alternative-delivery</c> (kargo takip linki ile).</summary>
internal sealed record AlternativeDeliveryRequest(bool IsPhoneNumber, string TrackingInfo, Dictionary<string, string> Params);

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
    /// <summary>İçerik kimliği: aynı productMainId altındaki her renk ayrı içeriktir (ADR-031).</summary>
    public long? ContentId { get; init; }
    public string? ProductMainId { get; init; }
    public NamedRef? Brand { get; init; }
    public NamedRef? Category { get; init; }
    public string? Title { get; init; }
    public List<ContentAttribute> Attributes { get; init; } = [];
    public List<ApprovedVariant> Variants { get; init; } = [];

    /// <summary>İçerik görselleri (dokümandaki images[].url). Dokümandaki örnek göreli yol; gerçek yanıtta mutlak https (2026-11-20 canlı okuma, ADR-042).</summary>
    public List<ContentImage> Images { get; init; } = [];
}

internal sealed class ContentImage
{
    public string? Url { get; init; }
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

    /// <summary>Ürünün trendyol.com sayfası (dokümandaki variants[].productUrl).</summary>
    public string? ProductUrl { get; init; }
    public VariantPrice? Price { get; init; }
}

internal sealed class VariantPrice
{
    public decimal? SalePrice { get; init; }
    public decimal? ListPrice { get; init; }
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

/// <summary>Kaynak: approveClaimLineItems, <c>PUT /order/sellers/{sellerId}/claims/{claimId}/items/approve</c>.</summary>
internal sealed record ApproveClaimRequest(IReadOnlyList<string> ClaimLineItemIdList, Dictionary<string, string> Params);

// ------------------------------------------------------------------ Satıcı Bilgileri (ADR-025)

/// <summary>Kaynak: Cargo Providers Service (GET /product/lookup/cargo-providers) → [{code, name}].</summary>
internal sealed record CargoProvider(string? Code, string? Name);

/// <summary>Kaynak: getClaimsIssueReasons (GET /order/claim-issue-reasons) → [{id, name}].</summary>
internal sealed record ClaimIssueReason(long? Id, string? Name);
