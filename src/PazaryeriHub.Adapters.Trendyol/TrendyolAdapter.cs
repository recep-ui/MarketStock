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
/// Kapsam (ADR-015): stok gönderme + sonuç, sipariş çekme, iade çekme, kanal stoğu okuma. Fiyat gönderilmez.
/// Hız limiti ve yeniden deneme HttpClient'a takılan dayanıklılık hattında (Polly) çözülür; burada hata çevirisi yapılır.
/// </summary>
public sealed class TrendyolAdapter(HttpClient http, IChannelCredentialStore credentials, TimeProvider clock, ILogger<TrendyolAdapter> logger)
    : IChannelAdapter
{
    public const string Code = "TRENDYOL";

    /// <summary>Kaynak: updatepriceandinventory — "Maximum 1,000 items, maximum 20,000 stock per product."</summary>
    public const int MaxItemsPerRequest = 1000;
    public const int MaxStockPerProduct = 20_000;

    /// <summary>Kaynak: getshipmentpackages — size en fazla 200.</summary>
    private const int OrderPageSize = 200;

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

    private static IEnumerable<ChannelCatalogEntry> ToCatalogEntries(ApprovedContent content)
    {
        // TODO: doğrulanmalı — renk/beden öznitelik adları kategoriye göre değişebilir; "Renk" ve "Beden" adları varsayılır.
        var contentColor = content.Attributes.FirstOrDefault(a => IsAttribute(a.AttributeName, "Renk"))?.AttributeValues.FirstOrDefault()?.AttributeValue;
        foreach (var v in content.Variants.Where(v => !string.IsNullOrWhiteSpace(v.Barcode)))
        {
            var color = v.Attributes.FirstOrDefault(a => IsAttribute(a.AttributeName, "Renk"))?.AttributeValue ?? contentColor;
            var size = v.Attributes.FirstOrDefault(a => IsAttribute(a.AttributeName, "Beden"))?.AttributeValue;
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
                Passive: v.Archived == true);
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
            var path = $"/order/sellers/{creds.SellerId}/orders?startDate={start}&endDate={end}&page={page}&size={OrderPageSize}" +
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
                    logger.LogWarning("Trendyol paketi {PaketId} atlandı: bilinmeyen durum {Durum}.", package.Id, package.Status);
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
        if (MapStatus(p.Status) is not { } status || string.IsNullOrWhiteSpace(p.OrderNumber))
        {
            return null;
        }

        var created = p.PackageHistory.Where(h => h.CreatedDate is not null).Select(h => h.CreatedDate!.Value).DefaultIfEmpty().Min();
        var orderDate = created > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(created).UtcDateTime : DateTime.UtcNow;
        var name = string.Join(' ', new[] { p.CustomerFirstName, p.CustomerLastName }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return new NormalizedOrder(
            OrderNo(p.OrderNumber, p.Id),
            orderDate,
            status,
            p.TotalPrice ?? p.Lines.Sum(l => l.Amount ?? (l.Price ?? 0) * l.Quantity),
            p.Lines.Select(l => new NormalizedOrderLine(l.Barcode, l.Barcode, l.MerchantSku, l.Quantity, l.Price ?? 0)).ToList(),
            new CustomerSummary(name.Length == 0 ? null : name, p.ShipmentAddress?.City, p.ShipmentAddress?.District),
            p.CargoProviderName is null && p.CargoTrackingNumber is null
                ? null
                : new CargoInfo(p.CargoProviderName, ReadString(p.CargoTrackingNumber), p.CargoTrackingLink));
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
            .Select(i => new NormalizedReturnLine(i.OrderLine!.Barcode, i.OrderLine.Barcode, Math.Max(1, i.ClaimItems.Count)))
            .ToList();
        var reason = c.Items.SelectMany(i => i.ClaimItems).Select(ci => ci.CustomerClaimItemReason?.Name).FirstOrDefault(n => n is not null);
        return new NormalizedReturn(orderNo, c.ClaimId ?? c.Id, created, lines, reason);
    }

    // ------------------------------------------------------------------ Kapsam dışı

    /// <summary>Fiyatlar satıcı panelinden girilir (ADR-015).</summary>
    public Task<BatchResult> PushPriceAsync(IReadOnlyList<PriceUpdate> updates, CancellationToken ct) =>
        throw new NotSupportedException("Fiyat gönderimi kapsam dışı: fiyatlar Trendyol satıcı panelinden girilir (ADR-015).");

    /// <summary>
    /// Durum bildirimi (Picking/Invoiced) paket satır kimlikleri gerektirir; henüz saklanmıyor.
    /// // TODO: Aşama 6 sonrası — updatePackageStatus (PUT /order/sellers/{sellerId}/shipment-packages/{packageId}).
    /// </summary>
    public Task UpdateOrderStatusAsync(string channelOrderNo, OrderStatus status, CargoInfo? cargo, CancellationToken ct) =>
        throw new NotSupportedException("Trendyol'a sipariş durumu bildirimi henüz uygulanmadı.");

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
