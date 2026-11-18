using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Domain.Orders;

namespace PazaryeriHub.Adapters.Trendyol;

/// <summary>
/// Trendyol gerçek adaptörü (API v2.0). Yalnızca resmi dokümanda (developers.trendyol.com/v2.0) okunan uçlar kullanılır;
/// dokümanda açık olmayan noktalar <c>// TODO: doğrulanmalı</c> ile işaretlidir (bkz. docs/kanal-api-notlari.md).
/// Kapsam: stok gönderme + sonuç, sipariş çekme, iade çekme, kanal stoğu okuma; fiyat gönderimi (ADR-028) uygulanmış ama
/// KDV dokümanda doğrulanamadığı için FIYAT yeteneği açılamaz.
/// Hız limiti ve yeniden deneme HttpClient'a takılan dayanıklılık hattında (Polly) çözülür; burada hata çevirisi yapılır.
/// </summary>
public sealed class TrendyolAdapter(HttpClient http, IChannelCredentialStore credentials, TimeProvider clock, ILogger<TrendyolAdapter> logger)
    : IChannelAdapter
{
    public const string Code = "TRENDYOL";

    /// <summary>Kaynak: updatepriceandinventory — "Maximum 1,000 items, maximum 20,000 stock per product."</summary>
    public const int MaxItemsPerRequest = 1000;
    public const int MaxStockPerProduct = 20_000;

    /// <summary>Kaynak: getShipmentPackages V2 — "Recommended Endpoint" örneğinde size=50 (V2 için üst sınır yazmıyor).</summary>
    private const int OrderPageSize = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ChannelCode => Code;

    // ------------------------------------------------------------------ Stok

    public async Task<BatchResult> PushStockAsync(IReadOnlyList<StockUpdate> updates, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count > MaxItemsPerRequest)
        {
            throw new ArgumentException($"Trendyol tek istekte en fazla {MaxItemsPerRequest} kalem kabul eder.", nameof(updates));
        }

        var items = new List<PriceAndInventoryItem>(updates.Count);
        foreach (var u in updates)
        {
            // Trendyol'da eşleştirme anahtarı barkoddur (KanalBarkod; yoksa KanalUrunKodu barkod kabul edilir).
            var barcode = u.Barcode ?? u.ChannelProductCode
                          ?? throw new ArgumentException("Trendyol stok güncellemesinde barkod zorunludur.", nameof(updates));
            var quantity = Math.Clamp(u.Quantity, 0, MaxStockPerProduct);
            if (quantity != u.Quantity)
            {
                logger.LogWarning("Trendyol: {Barkod} için stok {Istenen} → {Gonderilen} (ürün başına üst sınır).", barcode, u.Quantity, quantity);
            }
            items.Add(new PriceAndInventoryItem(barcode, quantity));
        }

        var creds = await CredentialsAsync(ct);
        var response = await SendAsync<BatchRequestCreated>(creds, HttpMethod.Post,
            $"/inventory/sellers/{creds.SellerId}/products/price-and-inventory", new PriceAndInventoryRequest(items), ct);

        return string.IsNullOrWhiteSpace(response?.BatchRequestId)
            ? throw new ChannelApiException(Code, "Trendyol stok isteğini aldı ancak takip numarası (batchRequestId) dönmedi.")
            : new BatchResult(response.BatchRequestId, Accepted: true);
    }

    public async Task<BatchStatus> CheckBatchAsync(string batchId, CancellationToken ct)
    {
        var creds = await CredentialsAsync(ct);
        var result = await SendAsync<BatchRequestResult>(creds, HttpMethod.Get,
            $"/product/sellers/{creds.SellerId}/products/batch-requests/{Uri.EscapeDataString(batchId)}", null, ct)
            ?? throw new ChannelApiException(Code, "Trendyol toplu işlem sonucu boş döndü.");
        return MapBatch(batchId, result);
    }

    internal static BatchStatus MapBatch(string batchId, BatchRequestResult result)
    {
        if (!string.Equals(result.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            return new BatchStatus(batchId, BatchState.Pending, []);
        }

        var errors = result.Items
            .Where(i => string.Equals(i.Status, "FAILED", StringComparison.OrdinalIgnoreCase))
            .Select(i =>
            {
                var barcode = ReadBarcode(i.RequestItem);
                var reason = i.FailureReasons.Count > 0 ? string.Join("; ", i.FailureReasons) : "Trendyol kalemi reddetti.";
                return new BatchItemError(barcode, barcode, $"Trendyol: {reason}");
            }).ToList();

        var total = result.ItemCount ?? result.Items.Count;
        var state = errors.Count == 0 ? BatchState.Completed
            : errors.Count >= total ? BatchState.Failed
            : BatchState.PartiallyFailed;
        return new BatchStatus(batchId, state, errors);
    }

    /// <summary>Kanal stoğu (mutabakat). Barkod listesi 50'lik parçalarla sorgulanır (dokümandaki üst sınır).</summary>
    public async Task<IReadOnlyList<ChannelStock>> FetchStockAsync(IEnumerable<string>? skus, CancellationToken ct)
    {
        var creds = await CredentialsAsync(ct);
        var result = new List<ChannelStock>();
        var basePath = $"/product/sellers/{creds.SellerId}/products/approved/inventory-and-price";

        if (skus is null)
        {
            string? token = null;
            var page = 0;
            do
            {
                // page × size en fazla 10.000; ötesi nextPageToken ile (dokümana göre)
                var query = token is null ? $"?page={page}&size=100" : $"?size=100&nextPageToken={Uri.EscapeDataString(token)}";
                var response = await SendAsync<PagedResponse<ApprovedProduct>>(creds, HttpMethod.Get, basePath + query, null, ct);
                if (response is null || response.Content.Count == 0)
                {
                    break;
                }
                result.AddRange(ToChannelStock(response.Content));
                token = response.NextPageToken;
                page++;
                if (token is null && page >= response.TotalPages)
                {
                    break;
                }
            } while (true);
            return result;
        }

        foreach (var chunk in skus.Distinct(StringComparer.Ordinal).Chunk(50))
        {
            // TODO: doğrulanmalı — "barcodes" dizisinin sorguda virgülle mi tekrarlı parametreyle mi verileceği dokümanda açık değil.
            var query = "?size=100&barcodes=" + string.Join(',', chunk.Select(Uri.EscapeDataString));
            var response = await SendAsync<PagedResponse<ApprovedProduct>>(creds, HttpMethod.Get, basePath + query, null, ct);
            if (response is not null)
            {
                result.AddRange(ToChannelStock(response.Content));
            }
        }
        return result;
    }

    /// <summary>
    /// Kanal kataloğu: <c>GET /product/sellers/{sellerId}/products/approved</c> (filterApprovedProducts; size ≤ 100,
    /// page × size ≤ 10.000, ötesi nextPageToken). İçerik: productMainId → model kodu, title, brand.name, category.name;
    /// varyant: barcode, stockCode, archived/locked/onSale, attributes. İmleç: "p:{sayfa}" veya "t:{nextPageToken}".
    /// </summary>
    public async Task<ChannelCatalogPage> FetchCatalogAsync(string? cursor, CancellationToken ct)
    {
        var creds = await CredentialsAsync(ct);
        var basePath = $"/product/sellers/{creds.SellerId}/products/approved";
        var page = 0;
        string? token = null;
        if (cursor is not null && cursor.StartsWith("t:", StringComparison.Ordinal))
        {
            token = cursor[2..];
        }
        else if (cursor is not null && cursor.StartsWith("p:", StringComparison.Ordinal))
        {
            page = int.Parse(cursor[2..], CultureInfo.InvariantCulture);
        }

        var query = token is null ? $"?page={page}&size=100" : $"?size=100&nextPageToken={Uri.EscapeDataString(token)}";
        var response = await SendAsync<PagedResponse<ApprovedContent>>(creds, HttpMethod.Get, basePath + query, null, ct);
        if (response is null || response.Content.Count == 0)
        {
            return new ChannelCatalogPage([], null);
        }

        var items = response.Content.SelectMany(ToCatalogEntries).ToList();
        string? next = response.NextPageToken is { Length: > 0 } nt ? "t:" + nt
            : token is null && page + 1 < response.TotalPages ? "p:" + (page + 1).ToString(CultureInfo.InvariantCulture)
            : null;
        return new ChannelCatalogPage(items, next);
    }

    /// <summary>Beden yerine kullanılan varyant özellik adları (havlu/nevresimde "Ebat" gibi). // TODO: doğrulanmalı — kategoriye göre değişebilir.</summary>
    private static readonly string[] SizeAttributeNames = ["Beden", "Ebat", "Boyut", "Ölçü", "Numara"];

    /// <summary>
    /// Katalog öğeleri (ADR-031). Trendyol'da aynı productMainId altındaki her renk ayrı içeriktir (contentId); içerik içindeki
    /// varyantlar varyant özellikleriyle (beden/ebat) ayrılır. Varyant anahtarı = "c{contentId}" + varyant özellikleri: farklı renk/ebat
    /// içerikleri ürün oluştururken asla tek SKU'da birleşmez. Renk/ebat okunamasa bile içerik kimliği ayırır.
    /// Ürün linki (productUrl) ve fiyat (price.salePrice/listPrice) dokümandaki alanlardır; salt okunur.
    /// </summary>
    private static IEnumerable<ChannelCatalogEntry> ToCatalogEntries(ApprovedContent content)
    {
        var contentColor = content.Attributes.FirstOrDefault(a => IsAttribute(a.AttributeName, "Renk"))?.AttributeValues.FirstOrDefault()?.AttributeValue;
        foreach (var v in content.Variants.Where(v => !string.IsNullOrWhiteSpace(v.Barcode)))
        {
            var color = v.Attributes.FirstOrDefault(a => IsAttribute(a.AttributeName, "Renk"))?.AttributeValue ?? contentColor;
            var size = v.Attributes.FirstOrDefault(a => SizeAttributeNames.Any(n => IsAttribute(a.AttributeName, n)))?.AttributeValue;
            var variantAttrs = v.Attributes
                .Where(x => !string.IsNullOrWhiteSpace(x.AttributeName) && !string.IsNullOrWhiteSpace(x.AttributeValue))
                .Select(x => (Name: x.AttributeName!.Trim(), Value: x.AttributeValue!.Trim()))
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .ToList();
            var described = (contentColor is not null && !variantAttrs.Any(x => IsAttribute(x.Name, "Renk")) ? [("Renk", contentColor.Trim())] : Array.Empty<(string, string)>())
                .Concat(variantAttrs.Select(x => (x.Name, x.Value)))
                .Select(x => $"{x.Item1}: {x.Item2}").ToList();
            var variantKey = content.ContentId is { } cid
                ? "c" + cid.ToString(CultureInfo.InvariantCulture) + string.Concat(variantAttrs.Select(x => "|" + x.Name + "=" + x.Value))
                : variantAttrs.Count > 0 ? string.Join("|", variantAttrs.Select(x => x.Name + "=" + x.Value)) : null;
            var status = v.Archived == true ? "Arşivlendi"
                : v.Blacklisted == true ? "Kara listede"
                : v.Locked == true ? "Kilitli"
                : v.OnSale == true ? "Satışta"
                : "Satışta değil";
            yield return new ChannelCatalogEntry(
                ChannelProductCode: null,
                Barcode: v.Barcode!.Trim(),
                StockCode: Clean(v.StockCode),
                ModelCode: Clean(content.ProductMainId),
                Color: Clean(color),
                Size: Clean(size),
                Name: Clean(content.Title),
                Brand: Clean(content.Brand?.Name),
                Category: Clean(content.Category?.Name),
                ChannelStatus: status,
                Passive: v.Archived == true,
                VariantKey: variantKey,
                VariantAttributes: described.Count > 0 ? string.Join("; ", described) : null,
                ProductUrl: Clean(v.ProductUrl),
                SalePrice: v.Price?.SalePrice,
                ListPrice: v.Price?.ListPrice);
        }
    }

    private static bool IsAttribute(string? name, string expected) =>
        name is not null && string.Equals(name.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IEnumerable<ChannelStock> ToChannelStock(IEnumerable<ApprovedProduct> products) =>
        products.SelectMany(p => p.Variants)
            .Where(v => !string.IsNullOrEmpty(v.Barcode))
            .Select(v => new ChannelStock(v.Barcode, v.Barcode, v.Quantity ?? 0));

    // ------------------------------------------------------------------ Siparişler

    /// <summary>
    /// Kaynak: getshipmentpackages. Trendyol'da birim "sipariş paketi"dir; bir sipariş birden fazla pakete bölünebilir.
    /// Bu yüzden kanal sipariş no = "{orderNumber}/{paketId}" (benzersiz ve izlenebilir; ADR-017).
    /// </summary>
    public async Task<IReadOnlyList<NormalizedOrder>> FetchOrdersAsync(DateTime sinceUtc, CancellationToken ct)
    {
        var creds = await CredentialsAsync(ct);
        var start = ToEpochMs(sinceUtc);
        var end = ToEpochMs(clock.GetUtcNow().UtcDateTime);
        var orders = new List<NormalizedOrder>();

        // TODO: doğrulanmalı — startDate/endDate'in hangi tarihe (oluşturma / son değişiklik) uygulandığı ve en fazla aralık dokümanda yok.
        for (var page = 0; ; page++)
        {
            // Order V2 (15.10.2026'dan itibaren zorunlu; eski /orders kullanımdan kalktı). Tarih aralığı en fazla iki hafta.
            var path = $"/order/sellers/{creds.SellerId}/v2/orders?startDate={start}&endDate={end}&page={page}&size={OrderPageSize}" +
                       "&orderByField=PackageLastModifiedDate&orderByDirection=ASC";
            var response = await SendAsync<PagedResponse<ShipmentPackage>>(creds, HttpMethod.Get, path, null, ct);
            if (response is null)
            {
                break;
            }

            foreach (var package in response.Content)
            {
                var normalized = MapPackage(package);
                if (normalized is null)
                {
                    logger.LogWarning("Trendyol paketi {PaketId} atlandı: bilinmeyen durum {Durum}.", package.PackageKey, package.EffectiveStatus);
                    continue;
                }
                orders.Add(normalized);
            }

            if (page + 1 >= response.TotalPages || response.Content.Count == 0)
            {
                break;
            }
        }
        return orders;
    }

    internal static NormalizedOrder? MapPackage(ShipmentPackage p)
    {
        if (MapStatus(p.EffectiveStatus) is not { } status || string.IsNullOrWhiteSpace(p.OrderNumber))
        {
            return null;
        }

        var history = p.PackageHistories.Count > 0 ? p.PackageHistories : p.PackageHistory;
        var created = p.OrderDate ?? history.Where(h => h.CreatedDate is not null).Select(h => h.CreatedDate!.Value).DefaultIfEmpty().Min();
        var orderDate = created > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(created).UtcDateTime : DateTime.UtcNow;
        var name = string.Join(' ', new[] { p.CustomerFirstName, p.CustomerLastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var packageId = p.PackageKey;

        return new NormalizedOrder(
            OrderNo(p.OrderNumber, packageId),
            orderDate,
            status,
            p.PackageTotalPrice ?? p.TotalPrice ?? p.Lines.Sum(l => l.Amount ?? (l.LineUnitPrice ?? l.Price ?? 0) * l.Quantity),
            p.Lines.Select(l => new NormalizedOrderLine(l.Barcode, l.Barcode, l.StockCode ?? l.MerchantSku, l.Quantity, l.LineUnitPrice ?? l.Price ?? 0,
                ChannelLineId: l.LineKey > 0 ? l.LineKey.ToString(CultureInfo.InvariantCulture) : null)).ToList(),
            new CustomerSummary(name.Length == 0 ? null : name, p.ShipmentAddress?.City, p.ShipmentAddress?.District),
            p.CargoProviderName is null && p.CargoTrackingNumber is null
                ? null
                : new CargoInfo(p.CargoProviderName, ReadString(p.CargoTrackingNumber), p.CargoTrackingLink),
            ChannelPackageId: packageId > 0 ? packageId.ToString(CultureInfo.InvariantCulture) : null);
    }

    /// <summary>
    /// Trendyol paket durumu → sipariş durumu. Durumların anlamı dokümanda tanımlı değil; eşleme akış sayfasındaki sıraya göre
    /// (Created → Picking → Invoiced → Shipped → Delivered). // TODO: doğrulanmalı (özellikle UnDelivered, UnSupplied, AtCollectionPoint).
    /// UnPacked: paket bölündü, kalemleri yeni paketlere taşındı → eski paket İptal sayılır (rezervi yeni paketler alır).
    /// </summary>
    internal static OrderStatus? MapStatus(string? status) => status switch
    {
        "Created" or "Awaiting" => OrderStatus.Yeni,
        "Picking" or "Invoiced" => OrderStatus.Hazirlaniyor,
        "Shipped" or "AtCollectionPoint" => OrderStatus.Kargoda,
        "Delivered" => OrderStatus.TeslimEdildi,
        "Cancelled" or "UnSupplied" or "UnPacked" => OrderStatus.Iptal,
        "Returned" or "UnDelivered" => OrderStatus.Iade,
        _ => null
    };

    public static string OrderNo(string orderNumber, long packageId) =>
        orderNumber + "/" + packageId.ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ İadeler

    /// <summary>Kaynak: getclaims. Her claimItem bir adet kabul edilir. // TODO: doğrulanmalı</summary>
    public async Task<IReadOnlyList<NormalizedReturn>> FetchReturnsAsync(DateTime sinceUtc, CancellationToken ct)
    {
        var creds = await CredentialsAsync(ct);
        var start = ToEpochMs(sinceUtc);
        var end = ToEpochMs(clock.GetUtcNow().UtcDateTime);
        var returns = new List<NormalizedReturn>();

        for (var page = 0; ; page++)
        {
            var path = $"/order/sellers/{creds.SellerId}/claims?startDate={start}&endDate={end}&page={page}&size=200";
            var response = await SendAsync<PagedResponse<Claim>>(creds, HttpMethod.Get, path, null, ct);
            if (response is null)
            {
                break;
            }
            returns.AddRange(response.Content.Select(MapClaim).OfType<NormalizedReturn>());
            if (page + 1 >= response.TotalPages || response.Content.Count == 0)
            {
                break;
            }
        }
        return returns;
    }

    internal static NormalizedReturn? MapClaim(Claim c)
    {
        if (string.IsNullOrWhiteSpace(c.OrderNumber))
        {
            return null;
        }
        var orderNo = c.OrderShipmentPackageId is { } packageId ? OrderNo(c.OrderNumber, packageId) : c.OrderNumber;
        var created = c.ClaimDate is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.UtcNow;
        var lines = c.Items.Where(i => i.OrderLine?.Barcode is not null)
            .Select(i => new NormalizedReturnLine(i.OrderLine!.Barcode, i.OrderLine.Barcode, Math.Max(1, i.ClaimItems.Count),
                LineKey: i.OrderLine.Id > 0 ? i.OrderLine.Id.ToString(CultureInfo.InvariantCulture) : i.OrderLine.Barcode,
                ChannelItemIds: i.ClaimItems.Select(ci => ci.Id).OfType<string>().ToList()))
            .ToList();
        var reason = c.Items.SelectMany(i => i.ClaimItems).Select(ci => ci.CustomerClaimItemReason?.Name).FirstOrDefault(n => n is not null);
        // Talep durumu kalem bazlıdır; ilk kalemin durumu gösterilir (Created, WaitingInAction, Accepted, Rejected, ...)
        var status = c.Items.SelectMany(i => i.ClaimItems).Select(ci => ci.ClaimItemStatus?.Name).FirstOrDefault(n => n is not null);
        return new NormalizedReturn(orderNo, c.ClaimId ?? c.Id, created, lines, reason, status);
    }

    // ------------------------------------------------------------------ Satıcı Bilgileri (ADR-025)

    /// <summary>
    /// Kargo firmaları: Cargo Providers Service, <c>GET /product/lookup/cargo-providers</c> → <c>[{code, name}]</c>
    /// (2026-11-18 okundu). Dokümana göre bu kodlar ürün oluşturma/güncellemedeki <c>cargoProviders</c> alanı içindir.
    /// // TODO: doğrulanmalı — sipariş tarafında (kendi kargo / alternatif teslimat) aynı kodların kullanıldığı (M2'de okunacak).
    /// İade red gerekçeleri: getClaimsIssueReasons, <c>GET /order/claim-issue-reasons</c> → <c>[{id, name}]</c>;
    /// <c>id</c> createClaimIssue servisine <c>claimIssueReasonId</c> olarak gönderilir.
    /// Not (doküman): 1651 "İade paketi tarafıma teslim edilmedi" gerekçesi, iade "Waiting Action" durumuna geçtikten sonraki
    /// ilk 24 saat içinde seçilemez.
    /// </summary>
    public async Task<ChannelReferenceList> FetchReferenceAsync(ChannelReferenceType type, CancellationToken ct)
    {
        var creds = await CredentialsAsync(ct);
        switch (type)
        {
            case ChannelReferenceType.CargoProvider:
            {
                var list = await SendAsync<List<CargoProvider>>(creds, HttpMethod.Get, "/product/lookup/cargo-providers", null, ct) ?? [];
                return new ChannelReferenceList(
                    list.Where(c => !string.IsNullOrWhiteSpace(c.Code))
                        .Select(c => new ChannelReferenceItem(c.Code!.Trim(), string.IsNullOrWhiteSpace(c.Name) ? c.Code!.Trim() : c.Name!.Trim()))
                        .ToList(),
                    ReferenceSource.Api);
            }
            case ChannelReferenceType.ClaimRejectReason:
            {
                var list = await SendAsync<List<ClaimIssueReason>>(creds, HttpMethod.Get, "/order/claim-issue-reasons", null, ct) ?? [];
                return new ChannelReferenceList(
                    list.Where(r => r.Id is not null && !string.IsNullOrWhiteSpace(r.Name))
                        .Select(r => new ChannelReferenceItem(r.Id!.Value.ToString(CultureInfo.InvariantCulture), r.Name!.Trim(),
                            r.Id == 1651 ? """{"not":"İade 'Waiting Action' durumuna geçtikten sonraki ilk 24 saatte seçilemez."}""" : null))
                        .ToList(),
                    ReferenceSource.Api);
            }
            default:
                throw new NotSupportedException($"Trendyol için '{type}' referans tipi desteklenmiyor.");
        }
    }

    // ------------------------------------------------------------------ İade kararı (ADR-027)

    /// <summary>Dokümana göre dosya eki ZORUNLU OLMAYAN red gerekçeleri (createClaimIssue): 1651, 451, 2101.</summary>
    internal static readonly IReadOnlyList<string> RejectReasonsWithoutAttachment = ["1651", "451", "2101"];

    /// <summary>
    /// Kaynaklar (2026-11-18 okundu): approveClaimLineItems, createClaimIssue.
    /// - Onay: <c>PUT /order/sellers/{sellerId}/claims/{claimId}/items/approve</c> <c>{claimLineItemIdList:[…], params:{}}</c>
    ///   (yalnızca "WaitingInAction" durumunda; onaydan sonra fraud kontrolü için "WaitingFraudCheck" görülebilir).
    /// - Red: <c>POST …/claims/{claimId}/issue?claimIssueReasonId=&amp;claimItemIdList=&amp;description=</c>. Dokümana göre 1651, 451 ve 2101
    ///   dışındaki gerekçelerde form-data dosya eki ZORUNLU; uygulama dosya barındırmadığı için yalnızca bu üç gerekçe desteklenir.
    ///   // TODO: doğrulanmalı — birden fazla claimItemId'nin sorguda virgülle mi tekrarlı parametreyle mi verileceği; eksiz POST gövdesi.
    /// - "Teslim alındı" bildirimi Trendyol'da yok.
    /// </summary>
    public async Task DecideReturnAsync(ReturnDecisionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var items = request.Lines.SelectMany(l => l.ChannelItemIds).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToList();
        if (items.Count == 0)
        {
            throw new ArgumentException("Trendyol iade kararı için claimItem kimlikleri zorunludur.", nameof(request));
        }
        var creds = await CredentialsAsync(ct);
        var basePath = $"/order/sellers/{creds.SellerId}/claims/{Uri.EscapeDataString(request.ReturnNo)}";
        switch (request.Kind)
        {
            case ReturnDecisionKind.Approve:
                await SendAsync<JsonElement?>(creds, HttpMethod.Put, basePath + "/items/approve", new ApproveClaimRequest(items, []), ct);
                return;
            case ReturnDecisionKind.Reject:
            {
                var reason = request.ReasonCode?.Trim();
                if (reason is null || !RejectReasonsWithoutAttachment.Contains(reason))
                {
                    throw new ArgumentException("Bu Trendyol red gerekçesi dosya eki istiyor; yalnızca 1651, 451 ve 2101 desteklenir.", nameof(request));
                }
                var description = (request.Description ?? "").Trim();
                if (description.Length > 500)
                {
                    throw new ArgumentException("Trendyol red açıklaması en fazla 500 karakter olabilir.", nameof(request));
                }
                var query = $"?claimIssueReasonId={Uri.EscapeDataString(reason)}&claimItemIdList={Uri.EscapeDataString(string.Join(',', items))}" +
                            $"&description={Uri.EscapeDataString(description)}";
                await SendAsync<JsonElement?>(creds, HttpMethod.Post, basePath + "/issue" + query, null, ct);
                return;
            }
            default:
                throw new NotSupportedException("Trendyol'da 'iade paketi elime ulaştı' bildirimi yok.");
        }
    }

    public ReturnDecisionRule DescribeReturnDecision(ReturnDecisionKind kind) => kind switch
    {
        ReturnDecisionKind.Approve => new(true, "Yalnızca 'WaitingInAction' durumundaki talep onaylanabilir."),
        ReturnDecisionKind.Reject => new(true,
            "Trendyol diğer gerekçelerde dosya eki istiyor; uygulama yalnızca eksiz gerekçeleri (1651, 451, 2101) gönderir. Açıklama en fazla 500 karakter.",
            RequiresReason: true, AllowedReasons: RejectReasonsWithoutAttachment),
        _ => ReturnDecisionRule.NotSupportedBecause("Trendyol'da 'iade paketi elime ulaştı' bildirimi yok.")
    };

    // ------------------------------------------------------------------ Soru-Cevap (ADR-029)

    internal const string QnaSource = "developers.trendyol.com/v2.0/docs/getting-customer-questions, /answering-customer-questions (2026-11-18 okundu)";

    /// <summary>
    /// <c>GET /qna/sellers/{sellerId}/questions/filter?startDate&amp;endDate(ms)&amp;page(0'dan)&amp;size(≤50)&amp;orderByField=LastModifiedDate</c>.
    /// Tarih aralığı en fazla 2 hafta → pencere 14 günlük parçalara bölünür. Müşteri adı/kimliği okunmaz (KVKK).
    /// TODO: doğrulanmalı — startDate/endDate'in soru oluşturma mı son güncelleme tarihine mi uygulandığı dokümanda net değil
    /// (parametre açıklaması "orders" diyor); IadeCek gibi örtüşmeli pencere kullanılır.
    /// </summary>
    public async Task<IReadOnlyList<PazaryeriHub.Application.Questions.NormalizedQuestion>> FetchQuestionsAsync(DateTime sinceUtc, CancellationToken ct)
    {
        var creds = await CredentialsAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var result = new Dictionary<long, PazaryeriHub.Application.Questions.NormalizedQuestion>();
        for (var from = sinceUtc; from < now; from = from.AddDays(14))
        {
            var to = from.AddDays(14) < now ? from.AddDays(14) : now;
            for (var page = 0; ; page++)
            {
                var path = $"/qna/sellers/{creds.SellerId}/questions/filter?startDate={ToEpochMs(from)}&endDate={ToEpochMs(to)}" +
                           $"&page={page}&size=50&orderByField=LastModifiedDate&orderByDirection=ASC";
                var response = await SendAsync<PagedResponse<QnaQuestion>>(creds, HttpMethod.Get, path, null, ct);
                if (response is null)
                {
                    break;
                }
                foreach (var q in response.Content)
                {
                    if (MapQuestion(q) is { } mapped)
                    {
                        result[q.Id] = mapped;
                    }
                }
                if (page + 1 >= response.TotalPages || response.Content.Count == 0)
                {
                    break;
                }
            }
        }
        return result.Values.ToList();
    }

    /// <summary>
    /// Durumlar: WAITING_FOR_ANSWER → cevap bekliyor (yalnızca bu cevaplanabilir); ANSWERED, WAITING_FOR_APPROVE → cevaplandı;
    /// REPORTED, REJECTED, UNANSWERED → kapandı. Bilinmeyen durum kapandı sayılır (cevap denenmez).
    /// </summary>
    internal static PazaryeriHub.Application.Questions.NormalizedQuestion? MapQuestion(QnaQuestion q)
    {
        if (q.Id <= 0 || string.IsNullOrWhiteSpace(q.Text))
        {
            return null;
        }
        var state = q.Status switch
        {
            "WAITING_FOR_ANSWER" => PazaryeriHub.Application.Questions.QuestionChannelState.Waiting,
            "ANSWERED" or "WAITING_FOR_APPROVE" => PazaryeriHub.Application.Questions.QuestionChannelState.Answered,
            _ => PazaryeriHub.Application.Questions.QuestionChannelState.Closed
        };
        var created = q.CreationDate is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.UtcNow;
        return new(q.Id.ToString(CultureInfo.InvariantCulture), q.Text, q.ProductName, q.ProductMainId, created, q.Status, state,
            AnswerDeadlineUtc: null, AnswerText: q.Answer?.Text);
    }

    /// <summary><c>POST /qna/sellers/{sellerId}/questions/{id}/answers</c> <c>{"text"}</c>; 10–2000 karakter.</summary>
    public async Task AnswerQuestionAsync(PazaryeriHub.Application.Questions.QuestionAnswerRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rule = DescribeQuestionAnswer();
        var text = request.Text?.Trim() ?? "";
        if (text.Length < rule.MinLength || text.Length > rule.MaxLength)
        {
            throw new ArgumentException($"Trendyol cevabı {rule.MinLength}–{rule.MaxLength} karakter olmalı.", nameof(request));
        }
        if (!long.TryParse(request.QuestionNo, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            throw new ArgumentException("Trendyol soru numarası sayısal olmalı.", nameof(request));
        }
        var creds = await CredentialsAsync(ct);
        await SendAsync<object>(creds, HttpMethod.Post, $"/qna/sellers/{creds.SellerId}/questions/{id}/answers", new QnaAnswerBody(text), ct);
    }

    public PazaryeriHub.Application.Questions.QuestionAnswerRule DescribeQuestionAnswer() => new(true, 10, 2000,
        "Yalnızca 'cevap bekliyor' durumundaki soru cevaplanabilir; cevap yasaklı kelime kontrolünden geçer, reddedilirse soru tekrar cevap bekler.");

    // ------------------------------------------------------------------ Fiyat (ADR-028)

    internal const string PriceSource =
        "developers.trendyol.com/v2.0/docs/stock-and-price-update-updatepriceandinventory ve /reference/updatepriceandinventory (2026-11-18 okundu)";

    /// <summary>
    /// Uç ve alanlar doğrulandı: <c>POST /inventory/sellers/{sellerId}/products/price-and-inventory</c>
    /// <c>{items:[{barcode, salePrice, listPrice}]}</c>; "listPrice cannot be lower than salePrice"; yalnızca fiyat gönderilebilir.
    /// TODO: doğrulanmalı — fiyatların KDV dahil olup olmadığı dokümanda YAZMIYOR → VatIncluded = null, yetenek açılamaz.
    /// </summary>
    public PriceUpdateRule DescribePriceUpdate() => new(
        Supported: true, VatIncluded: null, SendsListPrice: true, SamePriceForProductVariants: false,
        "Trendyol dokümanı fiyatın KDV dahil mi hariç mi olduğunu belirtmiyor. Resmi bir kaynak docs/ altına konunca açılabilir.",
        PriceSource);

    public async Task<BatchResult> PushPriceAsync(IReadOnlyList<PriceUpdate> updates, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count > MaxItemsPerRequest)
        {
            throw new ArgumentException($"Trendyol tek istekte en fazla {MaxItemsPerRequest} kalem kabul eder.", nameof(updates));
        }

        var items = new List<PriceOnlyItem>(updates.Count);
        foreach (var u in updates)
        {
            var barcode = u.Barcode ?? u.ChannelProductCode
                          ?? throw new ArgumentException("Trendyol fiyat güncellemesinde barkod zorunludur.", nameof(updates));
            if (u.SalePrice <= 0m || u.ListPrice < u.SalePrice)
            {
                throw new ArgumentException($"Trendyol: {barcode} için geçersiz fiyat (satış > 0 ve liste >= satış olmalı).", nameof(updates));
            }
            items.Add(new PriceOnlyItem(barcode, u.SalePrice, u.ListPrice));
        }

        var creds = await CredentialsAsync(ct);
        var response = await SendAsync<BatchRequestCreated>(creds, HttpMethod.Post,
            $"/inventory/sellers/{creds.SellerId}/products/price-and-inventory", new PriceOnlyRequest(items), ct);

        return string.IsNullOrWhiteSpace(response?.BatchRequestId)
            ? throw new ChannelApiException(Code, "Trendyol fiyat isteğini aldı ancak takip numarası (batchRequestId) dönmedi.")
            : new BatchResult(response.BatchRequestId, Accepted: true);
    }

    // ------------------------------------------------------------------ Sipariş durumu bildirimi (ADR-026)

    /// <summary>
    /// Kaynaklar (2026-11-18 okundu): Order Process Flow, Notify Packages (updatePackage), Shipping Alternative Delivery.
    /// - Hazırlanıyor → <c>PUT /order/sellers/{sellerId}/shipment-packages/{packageId}</c> <c>{lines:[{lineId, quantity}], params:{}, status:"Picking"}</c>.
    ///   Akışta "Invoiced" adımı <b>opsiyonel</b>; fatura kapsam dışı olduğu için gönderilmez.
    /// - Kargoda (Trendyol anlaşmalı kargo): kargo firması teslim alınca paket kendiliğinden Shipped olur → bildirim yok.
    /// - Kargoda (kendi kargo): <c>PUT …/shipment-packages/{packageId}/alternative-delivery</c> <c>{isPhoneNumber:false, trackingInfo:"takip linki", params:{}}</c>;
    ///   başarıda paket Shipped olur. // TODO: doğrulanmalı — alternatif teslimatın satıcı hesabında açık olması gerekip gerekmediği;
    ///   dokümanın veri tipi tablosunda "isPhoneNumber should be always true" yazıyor (link örneğiyle çelişkili).
    /// // TODO: doğrulanmalı — paket zaten Picking iken tekrar Picking gönderildiğinde dönen yanıt (dokümanda yanıt örneği yok).
    /// </summary>
    public async Task UpdateOrderStatusAsync(OrderStatusUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        var packageId = ResolvePackageId(update);
        var creds = await CredentialsAsync(ct);
        var basePath = $"/order/sellers/{creds.SellerId}/shipment-packages/{packageId.ToString(CultureInfo.InvariantCulture)}";
        switch (update.Status)
        {
            case OrderStatus.Hazirlaniyor:
            {
                if (update.Lines.Count == 0)
                {
                    throw new ArgumentException("Trendyol Picking bildirimi için satır kimlikleri (lineId) zorunludur.", nameof(update));
                }
                var lines = update.Lines.Select(l => new UpdatePackageLine(
                    long.TryParse(l.ChannelLineId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                        ? id : throw new ArgumentException($"Geçersiz Trendyol lineId: '{l.ChannelLineId}'.", nameof(update)),
                    l.Quantity)).ToList();
                await SendAsync<JsonElement?>(creds, HttpMethod.Put, basePath, new UpdatePackageRequest(lines, [], "Picking"), ct);
                return;
            }
            case OrderStatus.Kargoda when update.OwnCargo:
            {
                var url = update.Cargo?.TrackingUrl;
                if (string.IsNullOrWhiteSpace(url))
                {
                    throw new ArgumentException("Trendyol alternatif teslimat bildirimi için kargo takip linki zorunludur.", nameof(update));
                }
                await SendAsync<JsonElement?>(creds, HttpMethod.Put, basePath + "/alternative-delivery",
                    new AlternativeDeliveryRequest(false, url.Trim(), []), ct);
                return;
            }
            default:
                throw new NotSupportedException($"Trendyol'a '{update.Status}' durumu bildirilmez.");
        }
    }

    public OrderNotificationRule DescribeOrderNotification(OrderStatus target, bool ownCargo) => target switch
    {
        OrderStatus.Hazirlaniyor => new(true, false,
            "Picking bildirilir (Trendyol panelinde 'Sipariş İşleme Alındı'). Invoiced adımı opsiyonel; fatura kapsam dışı olduğu için gönderilmez.",
            RequiresLineIds: true),
        OrderStatus.Kargoda when !ownCargo => OrderNotificationRule.NotRequiredBecause(
            "Trendyol anlaşmalı kargo: kargo firması paketi teslim alınca Trendyol durumu kendisi Shipped yapar; takip numarası Trendyol'dan gelir."),
        OrderStatus.Kargoda => new(true, false,
            "Alternatif teslimat: kargo takip linki gönderilir, paket Shipped olur. Satıcı hesabında alternatif teslimat yetkisi gerekebilir (doğrulanmadı).",
            RequiresTrackingUrl: true),
        _ => OrderNotificationRule.NotSupportedBecause("Bu durum Trendyol'a bildirilmez.")
    };

    /// <summary>
    /// Paket kimliği: istekteki değer, yoksa kanal sipariş no'sunun "/" sonrası ("{orderNumber}/{paketId}", ADR-017).
    /// Bu yüzden kural paket kimliğini zorunlu tutmaz (eski siparişlerde de çözülür).
    /// </summary>
    internal static long ResolvePackageId(OrderStatusUpdate update)
    {
        var raw = update.ChannelPackageId;
        if (string.IsNullOrWhiteSpace(raw))
        {
            var slash = update.ChannelOrderNo.LastIndexOf('/');
            raw = slash >= 0 ? update.ChannelOrderNo[(slash + 1)..] : null;
        }
        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : throw new ArgumentException($"Trendyol paket kimliği bulunamadı ({update.ChannelOrderNo}).", nameof(update));
    }

    // ------------------------------------------------------------------ HTTP

    private async Task<TrendyolCredentials> CredentialsAsync(CancellationToken ct)
    {
        var json = await credentials.GetJsonAsync(Code, ct)
                   ?? throw new ChannelApiException(Code, "Trendyol API bilgileri girilmemiş (Ayarlar → Kanallar).");
        return TrendyolCredentials.Parse(json);
    }

    private async Task<T?> SendAsync<T>(TrendyolCredentials creds, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, creds.BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{creds.ApiKey}:{creds.ApiSecret}")));
        request.Headers.TryAddWithoutValidation("User-Agent", creds.UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ChannelApiException(Code, "Trendyol'a ulaşılamadı.", transient: true, inner: ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ChannelApiException(Code, "Trendyol zamanında yanıt vermedi.", transient: true, inner: ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw Translate(response.StatusCode, text, creds.Environment);
            }
            return string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize<T>(text, Json);
        }
    }

    /// <summary>Ham Trendyol hatasını kullanıcıya gösterilecek sade Türkçe mesaja çevirir.</summary>
    internal static ChannelApiException Translate(HttpStatusCode status, string body, string environment)
    {
        var code = (int)status;
        var detail = ExtractMessage(body);
        var message = status switch
        {
            HttpStatusCode.Unauthorized => "Trendyol API bilgileri hatalı (satıcı no, API Key ve API Secret'ı kontrol edin).",
            HttpStatusCode.Forbidden => "Trendyol isteği reddetti (erişim/User-Agent).",
            HttpStatusCode.NotFound => "Trendyol kaydı bulunamadı.",
            HttpStatusCode.TooManyRequests => "Trendyol istek sınırı aşıldı; daha sonra yeniden denenecek.",
            HttpStatusCode.ServiceUnavailable when environment == TrendyolCredentials.Stage =>
                "Trendyol test ortamına erişilemedi (IP adresinin beyaz listeye eklenmesi gerekir).",
            _ when code >= 500 => $"Trendyol geçici bir hata verdi (HTTP {code}).",
            _ when detail?.Contains("15 dakika", StringComparison.OrdinalIgnoreCase) == true =>
                "Aynı istek 15 dakika içinde tekrar gönderilemez (Trendyol); değer değişince yeniden gönderilecek.",
            _ => detail is null ? $"Trendyol isteği reddetti (HTTP {code})." : $"Trendyol isteği reddetti: {detail}"
        };
        var transient = status is HttpStatusCode.TooManyRequests || code >= 500;
        return new ChannelApiException(Code, message, code, transient);
    }

    private static string? ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                {
                    var messages = errors.EnumerateArray()
                        .Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.GetString() : e.ToString())
                        .Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
                    if (messages.Count > 0)
                    {
                        return Truncate(string.Join("; ", messages));
                    }
                }
                foreach (var name in new[] { "message", "error", "exception" })
                {
                    if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        return Truncate(value.GetString()!);
                    }
                }
            }
        }
        catch (JsonException)
        {
            return Truncate(body);
        }
        return null;
    }

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300] + "…";

    private static string? ReadBarcode(JsonElement? item) =>
        item is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty("barcode", out var b) && b.ValueKind == JsonValueKind.String
            ? b.GetString()
            : null;

    private static string? ReadString(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString(),
        { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
        _ => null
    };

    private static long ToEpochMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
