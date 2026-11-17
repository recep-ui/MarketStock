using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Domain.Orders;

namespace PazaryeriHub.Adapters.Beymen;

/// <summary>
/// Beymen Partner API adaptörü. Kaynak: https://partner-docs.beymen.com/en (bkz. docs/kanal-api-notlari.md).
/// Dokümanda açık olmayan noktalar <c>// TODO: doğrulanmalı</c> ile işaretlidir. Kapsam (ADR-015): stok + sonuç, kanal stoğu,
/// sipariş ve iade çekme. Fiyat gönderilmez.
/// <para>Eşleştirme anahtarı Beymen <b>productId (GUID)</b>'dir ("Kanal Ürün Kodu"); barkod "Kanal Barkod" alanında tutulabilir.</para>
/// </summary>
public sealed class BeymenAdapter(HttpClient http, IChannelCredentialStore credentials, TimeProvider clock, ILogger<BeymenAdapter> logger)
    : IChannelAdapter
{
    public const string Code = "BEYMEN";

    /// <summary>Kaynak: #bulk-price-and-stock-update — en fazla 1000 kalem; quantity 0–1.000.000.</summary>
    public const int MaxItemsPerRequest = 1000;
    public const int MaxQuantity = 1_000_000;

    /// <summary>
    /// Siparişlerde "son güncellenme" alanı yok; tarih filtresi sipariş tarihine göredir. Durum değişikliklerini yakalamak için
    /// her çalışmada en az bu kadar gün geriye bakılır (ADR-019). // TODO: doğrulanmalı (hacme göre ayarlanmalı).
    /// </summary>
    internal const int StatusLookbackDays = 7;

    private const int PageSize = 100;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ChannelCode => Code;

    // ------------------------------------------------------------------ Stok

    public async Task<BatchResult> PushStockAsync(IReadOnlyList<StockUpdate> updates, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count > MaxItemsPerRequest)
        {
            throw new ArgumentException($"Beymen tek istekte en fazla {MaxItemsPerRequest} kalem kabul eder.", nameof(updates));
        }

        var items = updates.Select(u => new PriceStockItem(
            u.ChannelProductCode ?? throw new ArgumentException("Beymen stok güncellemesinde productId (Kanal Ürün Kodu) zorunludur.", nameof(updates)),
            Math.Clamp(u.Quantity, 0, MaxQuantity))).ToList();

        var creds = await CredentialsAsync(ct);
        using var doc = await SendAsync(creds, HttpMethod.Post, "/batch/products/price-stock", new { items }, ct);
        var processId = doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object ? Str(doc.RootElement, "processId") : null;
        return processId is null
            ? throw new ChannelApiException(Code, "Beymen stok isteğini aldı ancak takip numarası (processId) dönmedi.")
            : new BatchResult(processId, Accepted: true);
    }

    /// <summary>
    /// Kaynak: #bulk-price-and-stock-update-result. Tüm kalemler işlenmeden önce HTTP 400 "The matching is incomplete." döner
    /// (= beklemede); sonra <c>items[]{productId, success, message}</c>.
    /// </summary>
    public async Task<BatchStatus> CheckBatchAsync(string batchId, CancellationToken ct)
    {
        var creds = await CredentialsAsync(ct);
        try
        {
            using var doc = await SendAsync(creds, HttpMethod.Get, $"/batch/products/price-stock/{Uri.EscapeDataString(batchId)}", null, ct);
            return MapBatch(batchId, doc?.RootElement);
        }
        catch (ChannelApiException ex) when (ex.StatusCode == 400 && ex.Message.Contains("matching is incomplete", StringComparison.OrdinalIgnoreCase))
        {
            return new BatchStatus(batchId, BatchState.Pending, []);
        }
    }

    internal static BatchStatus MapBatch(string batchId, JsonElement? root)
    {
        var items = root is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().ToList()
            : [];
        var errors = items
            .Where(i => i.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.False)
            .Select(i => new BatchItemError(Str(i, "productId"), null, $"Beymen: {Str(i, "message") ?? "kalem reddedildi"}"))
            .ToList();
        var state = errors.Count == 0 ? BatchState.Completed : errors.Count >= items.Count ? BatchState.Failed : BatchState.PartiallyFailed;
        return new BatchStatus(batchId, state, errors);
    }

    /// <summary>
    /// Kanal kataloğu: <c>GET /products/price-stock/search</c> (size ≤ 1000; 10.000 kayıttan sonra searchAfter).
    /// Doğrulanmış alanlar yalnızca <c>id</c> (→ Kanal Ürün Kodu, productId) ve <c>barcode</c>; model/renk/beden bu uçta yok.
    /// TODO: doğrulanmalı — Beymen'in ürün içeriği (model, renk, beden) dönen bir listeleme ucu dokümanda bulunursa eklenmeli.
    /// İmleç: "p:{sayfa}" veya "s:{sayfa}:{searchAfter}".
    /// </summary>
    public async Task<ChannelCatalogPage> FetchCatalogAsync(string? cursor, CancellationToken ct)
    {
        const int size = 1000;
        var page = 0;
        string? searchAfter = null;
        if (cursor is not null)
        {
            var parts = cursor.Split(':', 3);
            page = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            searchAfter = parts[0] == "s" && parts.Length == 3 ? parts[2] : null;
        }

        var creds = await CredentialsAsync(ct);
        var query = searchAfter is null ? $"?size={size}&page={page}" : $"?size={size}&searchAfter={Uri.EscapeDataString(searchAfter)}";
        using var doc = await SendAsync(creds, HttpMethod.Get, "/products/price-stock/search" + query, null, ct);
        if (doc is null)
        {
            return new ChannelCatalogPage([], null);
        }
        var items = Items(doc.RootElement).ToList();
        var entries = items
            .Select(i => (Id: Str(i, "id"), Barcode: Str(i, "barcode")))
            .Where(x => x.Id is not null)
            .Select(x => new ChannelCatalogEntry(x.Id!.Trim(), x.Barcode?.Trim(), null, null, null, null, null, null, null, null, false))
            .ToList();

        var pageCount = Int(doc.RootElement, "pageCount");
        var nextSearchAfter = (page + 1) * size >= 10_000 ? Str(doc.RootElement, "searchAfter") : null;
        var last = items.Count < size || (nextSearchAfter is null && ((page + 1) * size >= 10_000 || (pageCount is { } pc && page + 1 >= pc)));
        var nextPage = (page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var next = last ? null : nextSearchAfter is not null ? $"s:{nextPage}:{nextSearchAfter}" : $"p:{nextPage}";
        return new ChannelCatalogPage(entries, next);
    }

    /// <summary>
    /// Kanal stoğu (mutabakat): <c>GET /products/price-stock/search</c> (size ≤1000; 10.000 kayıttan sonra searchAfter).
    /// Arama productId ile filtrelenemediği için tüm liste sayfalanır ve istenen productId'ler süzülür.
    /// </summary>
    public async Task<IReadOnlyList<ChannelStock>> FetchStockAsync(IEnumerable<string>? skus, CancellationToken ct)
    {
        var wanted = skus?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var creds = await CredentialsAsync(ct);
        var result = new List<ChannelStock>();
        string? searchAfter = null;
        for (var page = 0; ; page++)
        {
            var query = searchAfter is null ? $"?size=1000&page={page}" : $"?size=1000&searchAfter={Uri.EscapeDataString(searchAfter)}";
            using var doc = await SendAsync(creds, HttpMethod.Get, "/products/price-stock/search" + query, null, ct);
            if (doc is null)
            {
                break;
            }
            var items = Items(doc.RootElement).ToList();
            foreach (var i in items)
            {
                var id = Str(i, "id");
                if (id is null || (wanted is not null && !wanted.Contains(id)))
                {
                    continue;
                }
                var count = i.TryGetProperty("stock", out var stock) && stock.ValueKind == JsonValueKind.Object ? Int(stock, "count") : null;
                result.Add(new ChannelStock(id, Str(i, "barcode"), count ?? 0));
            }

            var pageCount = Int(doc.RootElement, "pageCount");
            searchAfter = (page + 1) * 1000 >= 10_000 ? Str(doc.RootElement, "searchAfter") : null;
            if (items.Count < 1000 || (searchAfter is null && pageCount is { } pc && page + 1 >= pc))
            {
                break;
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ Siparişler

    /// <summary>
    /// Kaynak: #order-listing, #cancel-listing. Kanal sipariş no = "{orderNumber}/{shipmentNumber}" (paket bölünebilir; ADR-019).
    /// İki sorgu: (1) sipariş tarihine göre [min(since, şimdi−7 gün), şimdi] — yeni siparişler ve son 7 günün durum değişiklikleri;
    /// (2) iptaller (<c>shipmentStatus=Cancelled</c>, iptal tarihine göre) [since, şimdi].
    /// </summary>
    public async Task<IReadOnlyList<NormalizedOrder>> FetchOrdersAsync(DateTime sinceUtc, CancellationToken ct)
    {
        var creds = await CredentialsAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var orderWindowStart = sinceUtc < now.AddDays(-StatusLookbackDays) ? sinceUtc : now.AddDays(-StatusLookbackDays);

        var raw = new List<JsonElement>();
        raw.AddRange(await PageAsync(creds, "/orders", $"startDate={Date(orderWindowStart)}&endDate={Date(now)}", ct));
        raw.AddRange(await PageAsync(creds, "/orders", $"shipmentStatus=Cancelled&startDate={Date(sinceUtc)}&endDate={Date(now)}", ct));

        var orders = new Dictionary<string, NormalizedOrder>(StringComparer.Ordinal);
        foreach (var item in raw)
        {
            if (MapOrder(item) is { } order)
            {
                orders[order.ChannelOrderNo] = order; // aynı paket iki sorguda gelirse sonuncusu (iptal) geçerli
            }
            else
            {
                logger.LogWarning("Beymen siparişi atlandı: sipariş no veya durum okunamadı ({SiparisNo}).", Str(item, "orderNumber"));
            }
        }
        return orders.Values.ToList();
    }

    internal static NormalizedOrder? MapOrder(JsonElement o)
    {
        var orderNumber = Str(o, "orderNumber");
        if (orderNumber is null || MapStatus(Int(o, "shipmentStatus"), o.TryGetProperty("isReturnedShipment", out var r) && r.ValueKind == JsonValueKind.True) is not { } status)
        {
            return null;
        }

        var lines = o.TryGetProperty("shipmentLines", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(l =>
            {
                var product = l.TryGetProperty("product", out var p) && p.ValueKind == JsonValueKind.Object ? p : (JsonElement?)null;
                return new NormalizedOrderLine(
                    Str(l, "productId"),
                    product is { } pb ? Str(pb, "barcode") : null,
                    product is { } ps ? Str(ps, "stockCode") : null,
                    Int(l, "quantity") ?? 1,
                    Dec(l, "price") ?? 0,
                    Dec(l, "commission"));
            }).ToList()
            : [];

        var address = o.TryGetProperty("shipmentAddress", out var a) && a.ValueKind == JsonValueKind.Object ? a : (JsonElement?)null;
        var provider = Str(o, "shipmentProvider");
        var tracking = Str(o, "shipmentTrackingNumber");
        return new NormalizedOrder(
            OrderNo(orderNumber, Str(o, "shipmentNumber")),
            ParseDate(Str(o, "orderDate")) ?? DateTime.UtcNow,
            status,
            Dec(o, "totalAmount") ?? lines.Sum(l => l.UnitPrice * l.Quantity),
            lines,
            new CustomerSummary(Str(o, "customerName"), address is { } ac ? Str(ac, "city") : null, address is { } ad ? Str(ad, "district") : null),
            provider is null && tracking is null ? null : new CargoInfo(provider, tracking, Str(o, "shipmentTrackingUrl")),
            ChannelPackageId: Str(o, "id"));
    }

    /// <summary>
    /// ShipmentStatus (#order-listing): NewOrder 1, ReadyToShip 2, Shipped 4, DeliveredToCustomer 8, Cancelled 16, Divided 32.
    /// Divided: paket bölündü, kalemler yeni paketlerde → eski paket İptal. Cancelled + isReturnedShipment (teslim edilemedi) → İade.
    /// </summary>
    internal static OrderStatus? MapStatus(int? shipmentStatus, bool returnedShipment) => shipmentStatus switch
    {
        1 => OrderStatus.Yeni,
        2 => OrderStatus.Hazirlaniyor,
        4 => OrderStatus.Kargoda,
        8 => OrderStatus.TeslimEdildi,
        16 => returnedShipment ? OrderStatus.Iade : OrderStatus.Iptal,
        32 => OrderStatus.Iptal,
        _ => null
    };

    public static string OrderNo(string orderNumber, string? shipmentNumber) =>
        string.IsNullOrWhiteSpace(shipmentNumber) ? orderNumber : orderNumber + "/" + shipmentNumber;

    // ------------------------------------------------------------------ İadeler

    /// <summary>Kaynak: #return-listing — <c>GET /claims</c> (startDate/endDate, sayfalı).</summary>
    public async Task<IReadOnlyList<NormalizedReturn>> FetchReturnsAsync(DateTime sinceUtc, CancellationToken ct)
    {
        var creds = await CredentialsAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var claims = await PageAsync(creds, "/claims", $"startDate={Date(sinceUtc)}&endDate={Date(now)}", ct);
        return claims.Select(MapClaim).OfType<NormalizedReturn>().ToList();
    }

    internal static NormalizedReturn? MapClaim(JsonElement c)
    {
        var orderNumber = Str(c, "orderNumber");
        if (orderNumber is null)
        {
            return null;
        }
        var lines = c.TryGetProperty("shipmentLines", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(l =>
            {
                var barcode = l.TryGetProperty("product", out var p) && p.ValueKind == JsonValueKind.Object ? Str(p, "barcode") : null;
                return new NormalizedReturnLine(Str(l, "productId"), barcode, Int(l, "quantity") ?? 1);
            }).ToList()
            : [];
        var reason = c.TryGetProperty("shipmentLines", out var l2) && l2.ValueKind == JsonValueKind.Array
            ? l2.EnumerateArray().Select(l => l.TryGetProperty("customerReason", out var cr) && cr.ValueKind == JsonValueKind.Object ? Str(cr, "description") : null)
                .FirstOrDefault(x => x is not null)
            : null;
        return new NormalizedReturn(OrderNo(orderNumber, Str(c, "shipmentNumber")), Str(c, "id"),
            ParseDate(Str(c, "claimDate")) ?? DateTime.UtcNow, lines, reason);
    }

    // ------------------------------------------------------------------ Satıcı Bilgileri (ADR-025)

    /// <summary>Kaynak notu: Beymen bu listeleri servis olarak değil, dokümanda sabit tablo olarak veriyor.</summary>
    internal const string ReferenceNote = "Kaynak: partner-docs.beymen.com/en (Cargo Companies; Problem Reporting of Return → SellerReasonType), 2026-11-18 okundu.";

    /// <summary>Kaynak: "Cargo Companies" tablosu (Code / Description), dokümandaki sırayla.</summary>
    internal static readonly IReadOnlyList<ChannelReferenceItem> CargoCompanies =
    [
        new("BexVizyon", "VİZYON KURYE HİZMETLERİ ULUSLARARASI TAŞIMACILIK BİLGİ SİSTEMLERİ TURİZM SAN.TİC.LTD.ŞTİ."),
        new("Mng", "MNG KARGO YURTİÇİ VE YURTDIŞI TAŞ. A.Ş"),
        new("AKN", "AKIN NAKLİYAT TURZ İNŞ.SAN.TİC.LTD.ŞTİ"),
        new("UD", "EKOL LOJİSTİK A.Ş"),
        new("YRTICI", "YURTICI KARGO SERVISI A.S"),
        new("BYMExprANK", "BİR GÜNDE KARGO LOJİSTİK DAĞITIM NAKLİYAT ORMAN ÜRÜNLERİ GIDA SANAYİ VE TİCARET LİMİTED ŞİRKET"),
        new("BYMExprIZM", "BİR GÜNDE KARGO LOJİSTİK DAĞITIM NAKLİYAT ORMAN ÜRÜNLERİ GIDA SANAYİ VE TİCARET LİMİTED ŞİRKET"),
        new("YRTICIPartner", "YURTICI KARGO SERVISI A.S"),
        new("BexTaskin", "TAŞKIN LOJİSTİK HİZMETLERİ TİCARET LİMİTED ŞİRKETİ"),
        new("BexBirgun", "BİR GÜNDE KARGO LOJİSTİK DAĞITIM NAKLİYAT ORMAN ÜRÜNLERİ GIDA SANAYİ VE TİCARET LİMİTED ŞİRKET"),
        new("HepsiJet", "D FAST DAGITIM HIZMETLERI VE LOJISTIK ANONIM SIRKETI"),
        new("BYMExpress", "TAŞKIN LOJİSTİK HİZMETLERİ TİCARET LİMİTED ŞİRKETİ"),
        new("KolGel", "Ekol Ekspres Kargo A.Ş."),
        new("ups", "UPS KARGO"),
        new("Aras", "Aras Kargo"),
        new("ptt", "PTT Kargo"),
        new("kolaygelsin", "Kolay Gelsin"),
        new("Sürat", "Sürat Kargo"),
        new("Borusan", "Borusan Lojistik"),
        new("ty", "Trendyol Express"),
        new("Ceva", "Ceva Lojistik"),
        new("Kargoist", "Kargo İst"),
        new("Horoz", "Horoz Lojistik"),
        new("Ekol", "Ekol Lojistik"),
        new("DHL", "DHL"),
        new("tnt", "TNT"),
        new("Nettrack", "Nettrack"),
        new("Scotty", "Scotty"),
        new("Kargom Sende", "Kargom Sende"),
        new("Jetizz", "Jetizz"),
        new("BoltKargo", "Bolt Kargo"),
        new("AGT", "AGT Kurye")
    ];

    /// <summary>
    /// Kaynak: "Problem Reporting of Return (Disputed Return)" → <c>PATCH /claims/{id}/dispute</c>, <c>reasonType</c>
    /// (<c>SellerReasonType</c> enum). Dokümana göre <c>images</c> PackageIsEmpty dışındaki tüm tiplerde zorunlu.
    /// Kod = enum adı; Veri = sayısal değer ve görsel zorunluluğu. Adlar dokümandaki İngilizce açıklamaların Türkçe çevirisidir.
    /// </summary>
    internal static readonly IReadOnlyList<ChannelReferenceItem> ClaimRejectReasons =
    [
        Reason("ReceivedProductNotMine", 0, "Gelen ürün bana ait değil"),
        Reason("ProductIsDefectiveOrDamaged", 1, "Gelen ürün kusurlu/hasarlı"),
        Reason("ProductQuantityIsMissing", 2, "Gelen ürün adedi eksik"),
        Reason("ProductIsWrong", 3, "Gelen ürün yanlış"),
        Reason("ProductUsedOrOpenedPacking", 4, "Gelen ürün kullanılmış/ambalajı açılmış"),
        Reason("ProductIsFake", 5, "Gelen ürün sahte"),
        Reason("ProductPartIsMissing", 6, "Gelen ürünün parçası/aksesuarı eksik"),
        Reason("ProductIsNotDefective", 7, "Gönderdiğim ürün kusurlu değil"),
        Reason("ProductIsNotWrong", 8, "Gönderdiğim ürün yanlış değil"),
        Reason("PackageIsEmpty", 9, "İade paketi boş geldi"),
        Reason("PackageNotFound", 10, "İade paketi bana ulaşmadı"),
        Reason("ExcessProductReceived", 11, "Müşteri fazla gönderdiğim ürünü iade etti")
    ];

    private static ChannelReferenceItem Reason(string code, int value, string name) =>
        new(code, name, $$"""{"deger":{{value}},"gorselZorunlu":{{(code == "PackageIsEmpty" ? "false" : "true")}}}""");

    /// <summary>Beymen referansları dokümandaki sabit listelerdir; kanala çağrı yapılmaz.</summary>
    public Task<ChannelReferenceList> FetchReferenceAsync(ChannelReferenceType type, CancellationToken ct) => Task.FromResult(type switch
    {
        ChannelReferenceType.CargoProvider => new ChannelReferenceList(CargoCompanies, ReferenceSource.Documentation, ReferenceNote),
        ChannelReferenceType.ClaimRejectReason => new ChannelReferenceList(ClaimRejectReasons, ReferenceSource.Documentation, ReferenceNote),
        _ => throw new NotSupportedException($"Beymen için '{type}' referans tipi desteklenmiyor.")
    });

    // ------------------------------------------------------------------ Kapsam dışı

    public Task<BatchResult> PushPriceAsync(IReadOnlyList<PriceUpdate> updates, CancellationToken ct) =>
        throw new NotSupportedException("Fiyat gönderimi kapsam dışı: fiyatlar Beymen Partner Portal'dan girilir (ADR-015).");

    /// <summary>
    /// Sipariş durumu bildirimi (ADR-026). Kaynak (partner-docs.beymen.com/en, 2026-11-18 okundu):
    /// - Hazırlanıyor: "Order Processing (Picking Status Submission)" <c>PUT /orders/{id}/status/picking</c> (gövdesiz;
    ///   New Order → Ready to Ship). <c>{id}</c> = sipariş listesindeki <c>id</c> (GUID).
    /// - Kargoda (kendi kargo): "Manual Progression Of Order Statuses" <c>PUT /orders/{id}/shipment/status</c>
    ///   <c>{trackingNumber (zorunlu), trackingUrl, cargoCompanyCode (zorunlu, Cargo Companies kodu), status: 4 (Shipped)}</c> → 202.
    /// - Kargoda (Beymen anlaşmalı kargo): bildirim yok.
    /// // TODO: doğrulanmalı — picking yanıt kodu ve sipariş zaten Ready to Ship iken tekrar çağrının sonucu.
    /// </summary>
    public async Task UpdateOrderStatusAsync(OrderStatusUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        var id = string.IsNullOrWhiteSpace(update.ChannelPackageId)
            ? throw new ArgumentException($"Beymen sipariş kimliği (id) bulunamadı ({update.ChannelOrderNo}).", nameof(update))
            : Uri.EscapeDataString(update.ChannelPackageId.Trim());
        var creds = await CredentialsAsync(ct);
        switch (update.Status)
        {
            case OrderStatus.Hazirlaniyor:
            {
                using var _ = await SendAsync(creds, HttpMethod.Put, $"/orders/{id}/status/picking", null, ct);
                return;
            }
            case OrderStatus.Kargoda when update.OwnCargo:
            {
                var cargo = update.Cargo;
                if (string.IsNullOrWhiteSpace(cargo?.TrackingNumber) || string.IsNullOrWhiteSpace(cargo.Company))
                {
                    throw new ArgumentException("Beymen kargo bildirimi için takip numarası ve kargo firması kodu zorunludur.", nameof(update));
                }
                var body = new { trackingNumber = cargo.TrackingNumber.Trim(), trackingUrl = cargo.TrackingUrl, cargoCompanyCode = cargo.Company.Trim(), status = 4 };
                using var _ = await SendAsync(creds, HttpMethod.Put, $"/orders/{id}/shipment/status", body, ct);
                return;
            }
            default:
                throw new NotSupportedException($"Beymen'e '{update.Status}' durumu bildirilmez.");
        }
    }

    public OrderNotificationRule DescribeOrderNotification(OrderStatus target, bool ownCargo) => target switch
    {
        OrderStatus.Hazirlaniyor => new(true, false, "Picking bildirilir (New Order → Ready to Ship).", RequiresPackageId: true),
        OrderStatus.Kargoda when !ownCargo => OrderNotificationRule.NotRequiredBecause(
            "Beymen anlaşmalı kargo: kargoya verilince Beymen durumu kendisi günceller."),
        OrderStatus.Kargoda => new(true, false, "Kargo bilgisiyle Shipped (4) bildirilir; kargo firması Beymen kargo kodlarından seçilmeli.",
            RequiresPackageId: true, RequiresTrackingNumber: true, RequiresCargoCompany: true),
        _ => OrderNotificationRule.NotSupportedBecause("Bu durum Beymen'e bildirilmez.")
    };

    // ------------------------------------------------------------------ HTTP ve JSON

    /// <summary>
    /// Tarih parametreleri dokümanda yalnızca "DateTime" olarak geçiyor. // TODO: doğrulanmalı — ISO 8601 (UTC) gönderiliyor.
    /// </summary>
    internal static string Date(DateTime utc) =>
        Uri.EscapeDataString(DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture));

    private async Task<List<JsonElement>> PageAsync(BeymenCredentials creds, string path, string query, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        for (var page = 0; ; page++)
        {
            using var doc = await SendAsync(creds, HttpMethod.Get, $"{path}?{query}&page={page}&size={PageSize}", null, ct);
            if (doc is null)
            {
                return all;
            }
            var items = Items(doc.RootElement).Select(e => e.Clone()).ToList();
            all.AddRange(items);
            var pageCount = Int(doc.RootElement, "pageCount");
            if (items.Count < PageSize || (pageCount is { } pc && page + 1 >= pc))
            {
                return all;
            }
        }
    }

    private async Task<BeymenCredentials> CredentialsAsync(CancellationToken ct)
    {
        var json = await credentials.GetJsonAsync(Code, ct)
                   ?? throw new ChannelApiException(Code, "Beymen API bilgileri girilmemiş (Ayarlar → Kanallar).");
        return BeymenCredentials.Parse(json);
    }

    private async Task<JsonDocument?> SendAsync(BeymenCredentials creds, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, creds.BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{creds.ApiKey}:{creds.ApiPassword}")));
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
            throw new ChannelApiException(Code, "Beymen'e ulaşılamadı.", transient: true, inner: ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ChannelApiException(Code, "Beymen zamanında yanıt vermedi.", transient: true, inner: ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw Translate(response.StatusCode, text);
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            try
            {
                return JsonDocument.Parse(text);
            }
            catch (JsonException ex)
            {
                throw new ChannelApiException(Code, "Beymen yanıtı okunamadı.", (int)response.StatusCode, inner: ex);
            }
        }
    }

    internal static ChannelApiException Translate(HttpStatusCode status, string body)
    {
        var code = (int)status;
        var detail = ExtractMessage(body);
        var message = status switch
        {
            HttpStatusCode.Unauthorized => "Beymen API bilgileri hatalı (API Key / API Password).",
            HttpStatusCode.Forbidden => "Beymen isteği reddetti (yetki).",
            HttpStatusCode.NotFound => "Beymen kaydı bulunamadı.",
            HttpStatusCode.TooManyRequests => "Beymen istek sınırı aşıldı; daha sonra yeniden denenecek.",
            _ when code >= 500 => $"Beymen geçici bir hata verdi (HTTP {code}).",
            _ when detail?.Contains("matching is incomplete", StringComparison.OrdinalIgnoreCase) == true => "The matching is incomplete.",
            _ => detail is null ? $"Beymen isteği reddetti (HTTP {code})." : $"Beymen isteği reddetti: {detail}"
        };
        return new ChannelApiException(Code, message, code, transient: status is HttpStatusCode.TooManyRequests || code >= 500);
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
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "message", "detail", "title", "error" })
                {
                    if (Str(doc.RootElement, name) is { } m)
                    {
                        return m.Length <= 300 ? m : m[..300] + "…";
                    }
                }
            }
            return null;
        }
        catch (JsonException)
        {
            return body.Length <= 300 ? body : body[..300] + "…";
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array ? root.EnumerateArray()
        : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray()
        : [];

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null
            }
            : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static decimal? Dec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    private static DateTime? ParseDate(string? s) =>
        s is not null && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private sealed record PriceStockItem(string ProductId, int Quantity);
}
