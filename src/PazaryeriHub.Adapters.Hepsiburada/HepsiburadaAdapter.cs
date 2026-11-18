using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Domain.Orders;

namespace PazaryeriHub.Adapters.Hepsiburada;

/// <summary>
/// Hepsiburada gerçek adaptörü. Kaynak: developers.hepsiburada.com (Listeleme ve Sipariş Entegrasyonu; portal istemci tarafında
/// çizildiği için uygulama içi tarayıcıyla okundu, bkz. docs/kanal-api-notlari.md). Dokümanda açık olmayan noktalar
/// <c>// TODO: doğrulanmalı</c> ile işaretlidir. Kapsam: stok + sonuç, kanal stoğu, sipariş çekme; fiyat gönderimi (ADR-028; KDV doğrulanmadığı için yetenek açılamaz).
/// <para>
/// Eşleştirme anahtarı <b>HBSKU</b>'dur (eşleştirmede "Kanal Ürün Kodu"): ödemesi tamamlanmış sipariş yanıtı yalnızca HBSKU
/// (<c>items.sku</c>) taşır; stok güncelleme de HBSKU kabul eder.
/// </para>
/// </summary>
public sealed class HepsiburadaAdapter(HttpClient http, IChannelCredentialStore credentials, TimeProvider clock, ILogger<HepsiburadaAdapter> logger)
    : IChannelAdapter
{
    public const string Code = "HEPSIBURADA";

    /// <summary>Kaynak: Listeleme Önemli Bilgiler — "Tek seferdeki istek içinde maximum 4000 adet sku güncellemesi".</summary>
    public const int MaxItemsPerRequest = 4000;

    /// <summary>Ödemesi tamamlanmış siparişler: limit en fazla 100; diğer durum listeleri en fazla 50.</summary>
    private const int OpenPageSize = 100;
    private const int StatusPageSize = 50;

    /// <summary>
    /// begindate/enddate biçimi dokümanda yazmıyor. // TODO: doğrulanmalı (biçim ve saat dilimi). Pencere en fazla 24 saat
    /// (dokümana göre aşılırsa enddate yok sayılır), bu yüzden aralık 24 saatlik parçalara bölünür.
    /// </summary>
    internal const string DateFormat = "yyyy-MM-dd HH:mm";

    private static readonly TimeZoneInfo Turkey = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ChannelCode => Code;

    // ------------------------------------------------------------------ Stok

    public async Task<BatchResult> PushStockAsync(IReadOnlyList<StockUpdate> updates, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count > MaxItemsPerRequest)
        {
            throw new ArgumentException($"Hepsiburada tek istekte en fazla {MaxItemsPerRequest} SKU kabul eder.", nameof(updates));
        }

        var body = updates.Select(u => new StockUploadItem(
            u.ChannelProductCode ?? throw new ArgumentException("Hepsiburada stok güncellemesinde HBSKU (Kanal Ürün Kodu) zorunludur.", nameof(updates)),
            Math.Max(0, u.Quantity))).ToList();

        var creds = await CredentialsAsync(ct);
        using var doc = await SendAsync(creds, HttpMethod.Post, $"{creds.ListingBaseUrl}/listings/merchantid/{creds.MerchantId}/stock-uploads", body, ct);

        // TODO: doğrulanmalı — POST yanıt şeması dokümanda gösterilmiyor; sonuç sorgusundaki "id" alanının döndüğü varsayılır.
        var id = doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("id", out var idElement)
            ? idElement.ToString()
            : null;
        return string.IsNullOrWhiteSpace(id)
            ? throw new ChannelApiException(Code, "Hepsiburada stok isteğini aldı ancak takip numarası (id) dönmedi.")
            : new BatchResult(id, Accepted: true);
    }

    /// <summary>
    /// Stok ve fiyat yüklemelerinin sonuç uçları ayrıdır; fiyat yüklemesinin takip numarası <see cref="PriceBatchPrefix"/> ile
    /// işaretlenir (ADR-028) ve <c>price-uploads/id/{id}</c>'den sorgulanır.
    /// </summary>
    public async Task<BatchStatus> CheckBatchAsync(string batchId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batchId);
        var creds = await CredentialsAsync(ct);
        var isPrice = batchId.StartsWith(PriceBatchPrefix, StringComparison.Ordinal);
        var id = isPrice ? batchId[PriceBatchPrefix.Length..] : batchId;
        using var doc = await SendAsync(creds, HttpMethod.Get,
            $"{creds.ListingBaseUrl}/listings/merchantid/{creds.MerchantId}/{(isPrice ? "price-uploads" : "stock-uploads")}/id/{Uri.EscapeDataString(id)}", null, ct);
        return MapUploadStatus(batchId, doc?.RootElement);
    }

    /// <summary>
    /// Dokümandaki örnek yanıt: <c>{"id","status":"Done","createdAt","total","errors":null, ...}</c>.
    /// "Done" dışındaki durumlar beklemede sayılır. // TODO: doğrulanmalı (diğer durum değerleri ve errors[] öğe şeması).
    /// </summary>
    internal static BatchStatus MapUploadStatus(string batchId, JsonElement? root)
    {
        if (root is not { ValueKind: JsonValueKind.Object } r
            || !r.TryGetProperty("status", out var status)
            || !string.Equals(status.GetString(), "Done", StringComparison.OrdinalIgnoreCase))
        {
            return new BatchStatus(batchId, BatchState.Pending, []);
        }

        var errors = new List<BatchItemError>();
        if (r.TryGetProperty("errors", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in list.EnumerateArray())
            {
                var hbSku = Str(e, "hepsiburadaSku");
                var message = Str(e, "message") ?? Str(e, "description") ?? e.ToString();
                errors.Add(new BatchItemError(hbSku, null, $"Hepsiburada: {message}"));
            }
        }

        // Fiyat yüklemesi: eşik dışı fiyat listingi kilitler (MinLock/MaxLock; "Listing Bilgilerini Güncelleme" rehberindeki örnek).
        if (r.TryGetProperty("priceValidations", out var validations) && validations.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in validations.EnumerateArray())
            {
                var hbSku = Str(v, "hepsiburadaSku");
                var message = Str(v, "description") ?? Str(v, "type") ?? v.ToString();
                errors.Add(new BatchItemError(hbSku, null, $"Hepsiburada fiyat kilidi ({Str(v, "type")}): {message}"));
            }
        }

        var total = r.TryGetProperty("total", out var t) && t.TryGetInt32(out var n) ? n : 0;
        var state = errors.Count == 0 ? BatchState.Completed
            : errors.Count >= total || errors.All(e => e.ChannelProductCode is null) ? BatchState.Failed
            : BatchState.PartiallyFailed;
        return new BatchStatus(batchId, state, errors);
    }

    /// <summary>Kanal stoğu (mutabakat): Listing Bilgilerini Sorgulama, hbSkuList filtresiyle.</summary>
    public async Task<IReadOnlyList<ChannelStock>> FetchStockAsync(IEnumerable<string>? skus, CancellationToken ct)
    {
        var creds = await CredentialsAsync(ct);
        var result = new List<ChannelStock>();
        var baseUrl = $"{creds.ListingBaseUrl}/listings/merchantid/{creds.MerchantId}";

        var filters = skus is null ? [null] : skus.Distinct(StringComparer.Ordinal).Chunk(50).Select(c => string.Join(',', c)).ToArray<string?>();
        foreach (var filter in filters)
        {
            for (var offset = 0; ; offset += 100)
            {
                // TODO: doğrulanmalı — hbSkuList'in virgülle ayrılmış liste olarak verildiği varsayılır.
                var url = $"{baseUrl}?offset={offset}&limit=100" + (filter is null ? "" : "&hbSkuList=" + Uri.EscapeDataString(filter));
                using var doc = await SendAsync(creds, HttpMethod.Get, url, null, ct);
                var items = Items(doc?.RootElement, "listings").ToList();
                result.AddRange(items
                    .Select(i => (Sku: Str(i, "hepsiburadaSku"), Stock: Int(i, "availableStock")))
                    .Where(x => x.Sku is not null)
                    .Select(x => new ChannelStock(x.Sku, null, x.Stock ?? 0)));
                if (items.Count < 100)
                {
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Kanal kataloğu: listing sorgusu <c>GET …/listings/merchantid/{merchantId}?offset&amp;limit</c> (FetchStock ile aynı uç).
    /// Doğrulanmış alanlar yalnızca <c>hepsiburadaSku</c> (→ Kanal Ürün Kodu), <c>merchantSku</c> (→ satıcı stok kodu) ve
    /// <c>isSalable</c>; barkod/model/renk/beden bu uçta yok → eşleştirme mevcut eşleştirme ve stok kodu adımlarıyla yapılır.
    /// İmleç: sıradaki offset.
    /// </summary>
    public async Task<ChannelCatalogPage> FetchCatalogAsync(string? cursor, CancellationToken ct)
    {
        const int limit = 100;
        var creds = await CredentialsAsync(ct);
        var offset = cursor is null ? 0 : int.Parse(cursor, CultureInfo.InvariantCulture);
        var url = $"{creds.ListingBaseUrl}/listings/merchantid/{creds.MerchantId}?offset={offset}&limit={limit}";
        using var doc = await SendAsync(creds, HttpMethod.Get, url, null, ct);
        var items = Items(doc?.RootElement, "listings").ToList();
        var entries = items
            .Select(i => (HbSku: Str(i, "hepsiburadaSku"), MerchantSku: Str(i, "merchantSku"), Salable: Prop(i, "isSalable")))
            .Where(x => x.HbSku is not null)
            .Select(x => new ChannelCatalogEntry(
                ChannelProductCode: x.HbSku!.Trim(), Barcode: null, StockCode: x.MerchantSku?.Trim(), ModelCode: null, Color: null, Size: null,
                Name: null, Brand: null, Category: null,
                ChannelStatus: x.Salable?.ValueKind switch { JsonValueKind.True => "Satışta", JsonValueKind.False => "Satışta değil", _ => null },
                Passive: false))
            .ToList();
        var next = items.Count < limit ? null : (offset + limit).ToString(CultureInfo.InvariantCulture);
        return new ChannelCatalogPage(entries, next);
    }

    // ------------------------------------------------------------------ Siparişler

    /// <summary>
    /// Hepsiburada sipariş modeli kalem bazlıdır ve durumlar ayrı uçlardan gelir. Birleştirilen kaynaklar:
    /// ödemesi beklenen (→ Yeni; dokümana göre stok rezerve edilmeli), ödemesi tamamlanmış (Open/Unpacked → Onaylandı),
    /// kargoya verilen (→ Kargoda), teslim edilen (→ Teslim edildi), teslim edilemeyen (→ İade), iptal (→ İptal).
    /// Kargo/teslim/iptal listeleri satır taşımaz; yalnızca kayıtlı siparişin durumunu ilerletir.
    /// Kanal sipariş no = Hepsiburada <c>orderNumber</c>.
    /// </summary>
    public async Task<IReadOnlyList<NormalizedOrder>> FetchOrdersAsync(DateTime sinceUtc, CancellationToken ct)
    {
        var creds = await CredentialsAsync(ct);
        var baseUrl = $"{creds.OrderBaseUrl}/orders/merchantid/{creds.MerchantId}";
        var packagesUrl = $"{creds.OrderBaseUrl}/packages/merchantid/{creds.MerchantId}";
        var now = clock.GetUtcNow().UtcDateTime;
        var result = new List<NormalizedOrder>();

        // Anlık listeler (tarih vermeden tamamı): ödeme bekleyen ve ödemesi tamamlanmış (paketlenecek) kalemler
        var awaiting = await PageAsync(creds, $"{baseUrl}/paymentawaiting", StatusPageSize, null, ct);
        result.AddRange(GroupLines(awaiting, OrderStatus.Yeni));
        var open = await PageAsync(creds, baseUrl, OpenPageSize, null, ct);
        result.AddRange(GroupLines(open, OrderStatus.Onaylandi));

        // Durum listeleri (son 1 ay; 24 saatlik pencerelerle)
        foreach (var (start, end) in Windows(sinceUtc, now))
        {
            var dates = $"begindate={Uri.EscapeDataString(Format(start))}&enddate={Uri.EscapeDataString(Format(end))}";
            result.AddRange(StatusOnly(await PageAsync(creds, $"{packagesUrl}/shipped", StatusPageSize, dates, ct), OrderStatus.Kargoda));
            result.AddRange(StatusOnly(await PageAsync(creds, $"{packagesUrl}/delivered", StatusPageSize, dates, ct), OrderStatus.TeslimEdildi));
            // TODO: doğrulanmalı — teslim edilemeyen ucunun yolu (portal operasyon listesinde var, yol okunmadı); şimdilik çağrılmıyor.
            // TODO: doğrulanmalı — kısmi iptal: iptal listesi kalem bazlıdır; şimdilik iptal gelen sipariş bütünüyle İptal sayılır.
            result.AddRange(StatusOnly(await PageAsync(creds, $"{baseUrl}/cancelled", StatusPageSize, dates, ct), OrderStatus.Iptal));
        }

        logger.LogInformation("Hepsiburada: {Bekleyen} ödeme bekleyen, {Acik} açık kalem; toplam {Adet} sipariş güncellemesi.",
            awaiting.Count, open.Count, result.Count);
        return result;
    }

    /// <summary>Kalem listesini siparişe çevirir (orderNumber'a göre gruplayarak).</summary>
    internal static IEnumerable<NormalizedOrder> GroupLines(IEnumerable<JsonElement> items, OrderStatus status) =>
        items.Select(i => (Item: i, OrderNo: Str(i, "orderNumber")))
            .Where(x => !string.IsNullOrWhiteSpace(x.OrderNo))
            .GroupBy(x => x.OrderNo!, StringComparer.Ordinal)
            .Select(g =>
            {
                var first = g.First().Item;
                var address = first.TryGetProperty("shippingAddress", out var a) && a.ValueKind == JsonValueKind.Object ? a : (JsonElement?)null;
                var lines = g.Select(x => new NormalizedOrderLine(
                    Str(x.Item, "sku"), null, Str(x.Item, "merchantSku"),
                    Int(x.Item, "quantity") ?? 1,
                    Amount(x.Item, "unitPrice") ?? 0,
                    Amount(x.Item, "commission"),
                    ChannelLineId: Str(x.Item, "id"))).ToList();
                var total = g.Sum(x => Amount(x.Item, "totalPrice") ?? (Amount(x.Item, "unitPrice") ?? 0) * (Int(x.Item, "quantity") ?? 1));
                var orderDate = g.Select(x => Date(x.Item, "orderDate")).Where(d => d is not null).DefaultIfEmpty(DateTime.UtcNow).Min()!.Value;
                var cargoName = Str(first, "cargoCompany");
                return new NormalizedOrder(g.Key, orderDate, status, total, lines,
                    new CustomerSummary(Str(first, "customerName"), address is { } ad ? Str(ad, "city") : null, address is { } ad2 ? Str(ad2, "town") : null),
                    cargoName is null ? null : new CargoInfo(cargoName, null, null));
            });

    /// <summary>Satırsız durum güncellemesi (kayıtlı siparişin durumunu ilerletir).</summary>
    internal static IEnumerable<NormalizedOrder> StatusOnly(IEnumerable<JsonElement> items, OrderStatus status) =>
        items.Select(i => (Item: i, OrderNo: Str(i, "orderNumber")))
            .Where(x => !string.IsNullOrWhiteSpace(x.OrderNo))
            .GroupBy(x => x.OrderNo!, StringComparer.Ordinal)
            .Select(g =>
            {
                var barcode = Str(g.First().Item, "barcode");
                return new NormalizedOrder(g.Key, DateTime.UtcNow, status, 0, [], null,
                    barcode is null ? null : new CargoInfo(null, barcode, null));
            });

    internal static IEnumerable<(DateTime Start, DateTime End)> Windows(DateTime sinceUtc, DateTime nowUtc)
    {
        var start = sinceUtc < nowUtc.AddDays(-30) ? nowUtc.AddDays(-30) : sinceUtc; // servis yalnızca son 1 ayı döner
        while (start < nowUtc)
        {
            var end = start.AddHours(24) < nowUtc ? start.AddHours(24) : nowUtc;
            yield return (start, end);
            start = end;
        }
    }

    internal static string Format(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Turkey).ToString(DateFormat, CultureInfo.InvariantCulture);

    private async Task<List<JsonElement>> PageAsync(HepsiburadaCredentials creds, string url, int limit, string? extraQuery, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        for (var offset = 0; ; offset += limit)
        {
            var full = $"{url}?offset={offset}&limit={limit}" + (extraQuery is null ? "" : "&" + extraQuery);
            using var doc = await SendAsync(creds, HttpMethod.Get, full, null, ct);
            var items = Items(doc?.RootElement, "items").Select(e => e.Clone()).ToList();
            all.AddRange(items);
            var totalCount = doc?.RootElement.ValueKind == JsonValueKind.Object ? Int(doc.RootElement, "totalCount") : null;
            if (items.Count < limit || (totalCount is { } tc && offset + limit >= tc))
            {
                return all;
            }
        }
    }

    // ------------------------------------------------------------------ Satıcı Bilgileri (ADR-025)

    /// <summary>
    /// Kargo firmaları: dokümanda <c>GET https://shipping-external-sit.hepsiburada.com/cargoFirms/{merchantId}</c> (Kargo ve
    /// Shipping → Profil Servisleri; "teslimat profili oluşturma aşamasında kullanılır") var, ancak YANIT ŞEMASI dokümanda
    /// gösterilmiyor (2026-11-18). // TODO: doğrulanmalı — örnek yanıt docs/ altına konunca uygulanacak.
    /// İade red gerekçesi: reddetme servisi (<c>POST /claims/number/{claimNumber}/reject</c>) <c>ClaimRejectionReason</c>'ı serbest
    /// metin (string) alıyor; değer listesi veren bir servis veya doküman listesi yok (örnekte yalnızca "PhotoNotClear").
    /// // TODO: doğrulanmalı — geçerli değer listesi.
    /// </summary>
    public Task<ChannelReferenceList> FetchReferenceAsync(ChannelReferenceType type, CancellationToken ct) => type switch
    {
        ChannelReferenceType.CargoProvider => throw new NotSupportedException(
            "Hepsiburada kargo firmaları servisinin yanıt şeması dokümanda yok; doğrulanınca eklenecek."),
        _ => throw new NotSupportedException(
            "Hepsiburada iade red gerekçeleri için değer listesi dokümanda yok (serbest metin); doğrulanınca eklenecek.")
    };

    // ------------------------------------------------------------------ Kapsam dışı / henüz doğrulanmamış

    // ------------------------------------------------------------------ Soru-Cevap (ADR-029)

    /// <summary>
    /// "Satıcıya Sor Entegrasyonu" okundu (2026-11-18): <c>GET https://api-asktoseller-merchant-sit.hepsiburada.com/api/v1.0/issues</c>
    /// (merchantId başlıkta), cevap <c>POST …/issues/{number}/answer</c> multipart (Answer ≤2000).
    /// TODO: doğrulanmalı — liste yanıtının kök şeması (dizi mi, sayfalı nesne mi) ve tarih parametre biçimi dokümanda yok →
    /// planda kararlaştırıldığı gibi doğrulanana kadar desteklenmiyor (mock).
    /// </summary>
    public Task<IReadOnlyList<PazaryeriHub.Application.Questions.NormalizedQuestion>> FetchQuestionsAsync(DateTime sinceUtc, CancellationToken ct) =>
        throw new NotSupportedException("Hepsiburada soru listesi yanıt şeması dokümanda yok; gerçek bir yanıt örneği docs/ altına konunca eklenecek.");

    public Task AnswerQuestionAsync(PazaryeriHub.Application.Questions.QuestionAnswerRequest request, CancellationToken ct) =>
        throw new NotSupportedException("Hepsiburada soru cevabı, soru okuma doğrulanana kadar desteklenmiyor.");

    public PazaryeriHub.Application.Questions.QuestionAnswerRule DescribeQuestionAnswer() =>
        PazaryeriHub.Application.Questions.QuestionAnswerRule.NotSupported("Hepsiburada soru listesi yanıt şeması dokümanda yok.");

    // ------------------------------------------------------------------ Fiyat (ADR-028)

    /// <summary>Fiyat yüklemesi takip numarası öneki (sonuç ucu stoktan ayrı).</summary>
    public const string PriceBatchPrefix = "price:";

    internal const string PriceSource =
        "developers.hepsiburada.com → Listeleme → Listing Fiyat Güncelleme (op=uploadPrice), Listing Fiyat Güncelleme Sorgulama (op=getPriceUpload), " +
        "Listing Bilgilerini Güncelleme rehberi (2026-11-18 okundu)";

    /// <summary>
    /// Uç doğrulandı: <c>POST {listing}/listings/merchantid/{merchantId}/price-uploads</c> gövde <c>[{hepsiburadaSku, price}]</c>,
    /// sonuç <c>GET …/price-uploads/id/{id}</c>. Tek fiyat alanı (liste fiyatı yok). Kategori eşiği dışındaki fiyat listingi kilitler.
    /// TODO: doğrulanmalı — fiyatın KDV dahil olup olmadığı dokümanda YAZMIYOR → VatIncluded = null, yetenek açılamaz.
    /// TODO: doğrulanmalı — POST yanıtındaki takip numarası alanı (stokta olduğu gibi "id" varsayılır).
    /// </summary>
    public PriceUpdateRule DescribePriceUpdate() => new(
        Supported: true, VatIncluded: null, SendsListPrice: false, SamePriceForProductVariants: false,
        "Hepsiburada dokümanı fiyatın KDV dahil mi hariç mi olduğunu belirtmiyor; yalnızca satış fiyatı gönderilir. Eşik dışı fiyat listingi kilitler.",
        PriceSource);

    public async Task<BatchResult> PushPriceAsync(IReadOnlyList<PriceUpdate> updates, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count > MaxItemsPerRequest)
        {
            throw new ArgumentException($"Hepsiburada tek istekte en fazla {MaxItemsPerRequest} SKU kabul eder.", nameof(updates));
        }

        var body = updates.Select(u =>
        {
            var hbSku = u.ChannelProductCode
                ?? throw new ArgumentException("Hepsiburada fiyat güncellemesinde HBSKU (Kanal Ürün Kodu) zorunludur.", nameof(updates));
            return u.SalePrice > 0m
                ? new PriceUploadItem(hbSku, u.SalePrice)
                : throw new ArgumentException($"Hepsiburada: {hbSku} için fiyat 0'dan büyük olmalı (0 listingi satışa kapatır).", nameof(updates));
        }).ToList();

        var creds = await CredentialsAsync(ct);
        using var doc = await SendAsync(creds, HttpMethod.Post, $"{creds.ListingBaseUrl}/listings/merchantid/{creds.MerchantId}/price-uploads", body, ct);
        var id = doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("id", out var idElement)
            ? idElement.ToString()
            : null;
        return string.IsNullOrWhiteSpace(id)
            ? throw new ChannelApiException(Code, "Hepsiburada fiyat isteğini aldı ancak takip numarası (id) dönmedi.")
            : new BatchResult(PriceBatchPrefix + id, Accepted: true);
    }

    internal sealed record PriceUploadItem(string HepsiburadaSku, decimal Price);

    // ------------------------------------------------------------------ Kapsam dışı / henüz doğrulanmamış

    /// <summary>
    /// Sipariş durumu bildirimi (ADR-026). Kaynak (2026-11-18 okundu): Sipariş Yönetimi → Paket Süreçleri → "Kalem veya Kalemleri Paketleme",
    /// <c>POST https://oms-external-sit.hepsiburada.com/packages/merchantid/{merchantId}</c> (canlıda "-sit" yok),
    /// gövde <c>{lineItemRequests:[{id, quantity}]}</c> (kalem id = sipariş listesindeki <c>items[].id</c>).
    /// // TODO: doğrulanmalı — paketleme yanıt şeması (paket numarası) dokümanda gösterilmiyor.
    /// Kargoda: "Mağaza hesabı teslimat statü iletimi (kargoda)" <c>POST …/packages/merchantid/{merchantId}/packagenumber/{packagenumber}/intransit</c>
    /// paket numarası ister; paketleme yanıtı doğrulanmadığı için paket numarası bilinmiyor → desteklenmiyor.
    /// </summary>
    public async Task UpdateOrderStatusAsync(OrderStatusUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Status != OrderStatus.Hazirlaniyor)
        {
            throw new NotSupportedException($"Hepsiburada'ya '{update.Status}' bildirimi doğrulanmadı.");
        }
        if (update.Lines.Count == 0)
        {
            throw new ArgumentException("Hepsiburada paketleme bildirimi için kalem kimlikleri zorunludur.", nameof(update));
        }
        var creds = await CredentialsAsync(ct);
        var body = new
        {
            lineItemRequests = update.Lines.Select(l => new { id = l.ChannelLineId, quantity = l.Quantity }).ToList()
        };
        using var _ = await SendAsync(creds, HttpMethod.Post, $"{creds.OrderBaseUrl}/packages/merchantid/{creds.MerchantId}", body, ct);
    }

    public OrderNotificationRule DescribeOrderNotification(OrderStatus target, bool ownCargo) => target switch
    {
        OrderStatus.Hazirlaniyor => new(true, false, "Kalemler paketlenir (Hepsiburada paketleme servisi).", RequiresLineIds: true),
        OrderStatus.Kargoda when !ownCargo => OrderNotificationRule.NotRequiredBecause(
            "Hepsiburada anlaşmalı kargo: kargoya verilince Hepsiburada durumu kendisi günceller."),
        OrderStatus.Kargoda => OrderNotificationRule.NotSupportedBecause(
            "Kendi kargo ile 'kargoda' bildirimi paket numarası istiyor; paketleme yanıtı dokümanda doğrulanmadığı için desteklenmiyor."),
        _ => OrderNotificationRule.NotSupportedBecause("Bu durum Hepsiburada'ya bildirilmez.")
    };

    /// <summary>
    /// İadeler (ADR-027): "Talep Entegrasyonu → Talep Listeleme" <c>GET oms-external-sit…/claims/merchantId/{merchantId}?beginDate&amp;endDate
    /// (yyyy-MM-dd HH:mm)&amp;offset&amp;limit</c> ve durum bazlı <c>…/status/{status}</c> uçları dokümanda var, ancak YANIT ŞEMASI portalda
    /// gösterilmiyor (yalnızca "Execute request to see response"; 2026-11-18). Webhook modelindeki alanların (claimNumber, status, orderNumber,
    /// lineItemId, merchantSku, quantity, claimDate…) GET yanıtında da aynı olduğu doğrulanmadı.
    /// // TODO: doğrulanmalı — gerçek bir GET /claims yanıt örneği docs/ altına konunca uygulanacak.
    /// Kararlar: onay <c>POST …/claims/number/{claimNumber}/accept</c>, red <c>POST …/claims/number/{claimNumber}/reject</c>
    /// <c>{ClaimRejectionReason, MerchantStatement, Reports?, UploadedReportsUrls?}</c> (dokümanda var); talepler okunamadığı için kullanılmıyor.
    /// </summary>
    public Task<IReadOnlyList<NormalizedReturn>> FetchReturnsAsync(DateTime sinceUtc, CancellationToken ct) =>
        throw new NotSupportedException("Hepsiburada talep listesi yanıt şeması dokümanda yok; örnek yanıt gelince uygulanacak.");

    public Task DecideReturnAsync(ReturnDecisionRequest request, CancellationToken ct) =>
        throw new NotSupportedException("Hepsiburada iade talepleri okunamadığı için karar bildirimi yapılmıyor.");

    public ReturnDecisionRule DescribeReturnDecision(ReturnDecisionKind kind) =>
        ReturnDecisionRule.NotSupportedBecause("Hepsiburada talep listesi yanıt şeması doğrulanmadı; iade kararı panelden verilmeli.");

    // ------------------------------------------------------------------ HTTP ve JSON yardımcıları

    private async Task<HepsiburadaCredentials> CredentialsAsync(CancellationToken ct)
    {
        var json = await credentials.GetJsonAsync(Code, ct)
                   ?? throw new ChannelApiException(Code, "Hepsiburada API bilgileri girilmemiş (Ayarlar → Kanallar).");
        return HepsiburadaCredentials.Parse(json);
    }

    private async Task<JsonDocument?> SendAsync(HepsiburadaCredentials creds, HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{creds.UserName}:{creds.Password}")));
        request.Headers.TryAddWithoutValidation("User-Agent", creds.EffectiveUserAgent);
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
            throw new ChannelApiException(Code, "Hepsiburada'ya ulaşılamadı.", transient: true, inner: ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ChannelApiException(Code, "Hepsiburada zamanında yanıt vermedi.", transient: true, inner: ex);
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
                throw new ChannelApiException(Code, "Hepsiburada yanıtı okunamadı.", (int)response.StatusCode, inner: ex);
            }
        }
    }

    /// <summary>Dokümandaki hata kodları (400/401/404/405/409/429/500) sade Türkçe mesaja çevrilir.</summary>
    internal static ChannelApiException Translate(HttpStatusCode status, string body)
    {
        var code = (int)status;
        var detail = ExtractMessage(body);
        var message = status switch
        {
            HttpStatusCode.Unauthorized => "Hepsiburada API bilgileri hatalı (kullanıcı adı / şifre / servis anahtarı).",
            HttpStatusCode.Forbidden => "Hepsiburada isteği reddetti (yetki veya kayıt bu satıcıya ait değil).",
            HttpStatusCode.NotFound => "Hepsiburada kaydı bulunamadı.",
            HttpStatusCode.TooManyRequests => "Hepsiburada istek sınırı aşıldı; daha sonra yeniden denenecek.",
            HttpStatusCode.Conflict => detail is null ? "Hepsiburada: işlem kaydın mevcut durumuyla çelişiyor." : $"Hepsiburada: {detail}",
            _ when code >= 500 => $"Hepsiburada geçici bir hata verdi (HTTP {code}).",
            _ => detail is null ? $"Hepsiburada isteği reddetti (HTTP {code})." : $"Hepsiburada isteği reddetti: {detail}"
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
                foreach (var name in new[] { "message", "title", "detail", "error" })
                {
                    if (Str(doc.RootElement, name) is { } m)
                    {
                        return m.Length <= 300 ? m : m[..300] + "…";
                    }
                }
            }
        }
        catch (JsonException)
        {
            return body.Length <= 300 ? body : body[..300] + "…";
        }
        return null;
    }

    /// <summary>Kök dizi veya {items|listings|data: [...]} biçimindeki listeyi döndürür (kök şeması her uçta belgelenmemiş).</summary>
    private static IEnumerable<JsonElement> Items(JsonElement? root, string preferred)
    {
        if (root is not { } r)
        {
            return [];
        }
        if (r.ValueKind == JsonValueKind.Array)
        {
            return r.EnumerateArray();
        }
        foreach (var name in new[] { preferred, "items", "listings", "data" })
        {
            if (Prop(r, name) is { ValueKind: JsonValueKind.Array } list)
            {
                return list.EnumerateArray();
            }
        }
        return [];
    }

    /// <summary>Büyük/küçük harf duyarsız özellik (dokümanda alan adları farklı yazımlarla geçiyor: PackageNumber / packageNumber).</summary>
    private static JsonElement? Prop(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (var p in e.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value;
            }
        }
        return null;
    }

    private static string? Str(JsonElement e, string name) => Prop(e, name) switch
    {
        { ValueKind: JsonValueKind.String } s => string.IsNullOrWhiteSpace(s.GetString()) ? null : s.GetString(),
        { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
        _ => null
    };

    private static int? Int(JsonElement e, string name) => Prop(e, name) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var v) ? v : null;

    /// <summary>{"amount": 301.4, "currency": "TRY"} biçimindeki tutar.</summary>
    private static decimal? Amount(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.Object } o && Prop(o, "amount") is { ValueKind: JsonValueKind.Number } a && a.TryGetDecimal(out var v) ? v : null;

    /// <summary>
    /// Dokümandaki örnek "2018-12-21T09:34:47" (saat dilimi belirtilmemiş). Bölge bilgisi yoksa UTC kabul edilir.
    /// // TODO: doğrulanmalı — değer Türkiye saati ise 3 saat kayar (sipariş listesinde gösterim etkilenir, stok etkilenmez).
    /// </summary>
    private static DateTime? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;

    private sealed record StockUploadItem(string HepsiburadaSku, int AvailableStock);
}
