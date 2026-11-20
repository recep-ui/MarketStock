using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace PazaryeriHub.Adapters.Hepsiburada;

/// <summary>Bir SIT çağrısının raporlanabilir sonucu. Gizli bilgi (Authorization) içermez.</summary>
public sealed record SitCall(string Step, string Method, string Url, string? RequestBody, int Status, string ResponseBody, long ElapsedMs)
{
    public bool Success => Status is >= 200 and < 300;

    /// <summary>Ham yanıtta adı <paramref name="name"/> olan ilk değeri (büyük/küçük harf duyarsız, iç içe) bulur; yoksa null.</summary>
    public string? Find(string name) => FindValue(ResponseBody, name);

    public static string? FindValue(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Search(doc.RootElement, name);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Search(JsonElement e, string name)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                        && p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                    {
                        return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
                    }
                }
                foreach (var p in e.EnumerateObject())
                {
                    if (Search(p.Value, name) is { } found)
                    {
                        return found;
                    }
                }
                return null;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                {
                    if (Search(item, name) is { } found)
                    {
                        return found;
                    }
                }
                return null;
            default:
                return null;
        }
    }
}

public sealed record SitFastListingItem(string MerchantSku, string ProductName, string? Barcode, int? Stock = null, decimal? Price = null);

public sealed record SitInventoryItem(string HepsiburadaSku, string? MerchantSku, decimal Price, int AvailableStock);

/// <param name="DeliveryOptionId">Dokümanda opsiyonel; verilmezse gönderilmez.</param>
/// <param name="CargoCompanyId">Dokümanda opsiyonel; verilmezse gönderilmez. Hepsiburada desteğinin örnek isteğinde (2026-11-20)
/// <c>CargoCompanyId: 21</c> ve <c>DeliveryOptionId: 1</c> var; test servisinin "ShippingMethodId cannot be empty" hatası bu alanlar
/// olmadan alınıyordu (ADR-037).</param>
public sealed record SitTestOrderLine(
    string HepsiburadaSku, string? MerchantSku, int Quantity, decimal UnitPrice, int? DeliveryOptionId = null, int? CargoCompanyId = null);

/// <summary>
/// Hepsiburada <b>test ortamı (SIT)</b> doğrulama istemcisi (ADR-037): canlı API bilgisi almak için Hepsiburada'nın istediği
/// "Test Süreci Adımları"ndaki uçlar. Kaynak: developers.hepsiburada.com (2026-11-19, uygulama içi tarayıcıyla okundu):
/// <list type="bullet">
/// <item>Katalog: Hızlı Ürün Yükleme <c>POST mpop-sit…/product/api/products/fastlisting</c>, Ürün Durumu Sorgulama
/// <c>GET …/products/status/{trackingId}?version&amp;page&amp;size</c>; tam akış (2026-11-20): kategoriler
/// <c>GET …/api/categories/get-all-categories</c>, özellikler <c>…/categories/{id}/attributes</c>, değerler
/// <c>…/categories/{id}/attribute/{attributeId}/values</c>, Ürün Bilgisi Gönderme <c>POST …/products/import?version=1</c>
/// (multipart <c>file</c>), Eşleşen Statü Onay / Red <c>POST …/products/approve-prematch</c> / <c>reject-prematch</c>.</item>
/// <item>Listeleme: Listing Bilgilerini Sorgulama, Envanter Güncelleme (+ sorgulama), Activate / Deactivate (<c>…/sku/{sku}/activate</c>),
/// Toplu Kilit Kaldırma (<c>{hbSkuList}</c>). Stok ve fiyat yüklemesi mevcut <see cref="HepsiburadaAdapter"/> üzerinden yapılır.</item>
/// <item>Sipariş: Test Siparişi Oluşturma <c>POST oms-stub-external-sit…/orders/merchantId/{merchantId}</c> (yalnızca TEST ortamında),
/// Ödemesi Tamamlanmış Siparişler, Aynı Pakete Konulabilecek Kalemler, Paket listesi. Paketleme mevcut adaptörle yapılır.</item>
/// </list>
/// Yanıt şemaları dokümanda gösterilmiyor ("Execute request to see response"): istemci yanıtı yorumlamadan <see cref="SitCall"/> olarak
/// döndürür, hata durumunda istisna atmaz. Kimlik bilgisi Prod ise oluşturulamaz; her istek adresi <c>-sit.hepsiburada.com</c> ile bitmeli.
/// Activate/Deactivate'teki {sku}: dokümanda türü yazmıyor; HBSKU ile SIT'te 200 döndü (2026-11-19).
/// </summary>
public sealed class HepsiburadaSitClient
{
    public const string ProductBaseUrl = "https://mpop-sit.hepsiburada.com/product";
    public const string ListingBaseUrl = "https://listing-external-sit.hepsiburada.com";
    public const string OrderBaseUrl = "https://oms-external-sit.hepsiburada.com";
    public const string OrderStubBaseUrl = "https://oms-stub-external-sit.hepsiburada.com";

    private static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions PascalCase = new() { PropertyNamingPolicy = null };

    private readonly HttpClient _http;
    private readonly HepsiburadaCredentials _creds;

    public HepsiburadaSitClient(HttpClient http, HepsiburadaCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentials);
        if (credentials.Environment != HepsiburadaCredentials.Test)
        {
            throw new InvalidOperationException(
                "Hepsiburada doğrulama istemcisi yalnızca test (SIT) ortamı bilgisiyle çalışır; kanal ayarlarında ortam 'Test' olmalı.");
        }
        _http = http;
        _creds = credentials;
    }

    public string MerchantId => _creds.MerchantId;

    // ------------------------------------------------------------------ Katalog

    public Task<SitCall> FastListingAsync(IReadOnlyList<SitFastListingItem> items, CancellationToken ct) =>
        SendAsync("Hızlı ürün yükleme", HttpMethod.Post, $"{ProductBaseUrl}/api/products/fastlisting",
            items.Select(i => new
            {
                merchant = _creds.MerchantId,
                merchantSku = i.MerchantSku,
                productName = i.ProductName,
                barcode = i.Barcode,
                // Dokümanda stock ve price "string"
                stock = i.Stock?.ToString(CultureInfo.InvariantCulture),
                price = i.Price?.ToString(CultureInfo.InvariantCulture)
            }).ToList(), CamelCase, ct);

    /// <summary>Kategori Bilgilerini Alma: yalnızca uç (leaf), aktif ve kullanılabilir kategoriler.</summary>
    public Task<SitCall> CategoriesAsync(int page, int size, CancellationToken ct) =>
        SendAsync("Kategori bilgilerini alma", HttpMethod.Get,
            $"{ProductBaseUrl}/api/categories/get-all-categories?leaf=true&status=ACTIVE&available=true&page={page}&size={size}", null, null, ct);

    public Task<SitCall> CategoryAttributesAsync(long categoryId, CancellationToken ct) =>
        SendAsync("Kategori özelliklerini alma", HttpMethod.Get, $"{ProductBaseUrl}/api/categories/{categoryId}/attributes", null, null, ct);

    public Task<SitCall> AttributeValuesAsync(long categoryId, string attributeId, int page, CancellationToken ct) =>
        SendAsync("Kategori özellik değerlerini alma", HttpMethod.Get,
            $"{ProductBaseUrl}/api/categories/{categoryId}/attribute/{Uri.EscapeDataString(attributeId)}/values?page={page}&size=1000", null, null, ct);

    /// <summary>
    /// Ürün Bilgisi Gönderme: ürün JSON'u bir dosya olarak multipart/form-data içinde <c>file</c> alanıyla gönderilir
    /// (dokümandaki örnek dosya adı <c>integrator.json</c>). Yanıttaki trackingId ile <see cref="ProductStatusAsync"/>.
    /// </summary>
    public Task<SitCall> ProductImportAsync(string productsJson, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productsJson);
        // Standart form biçimi: tırnaksız boundary ve yalnızca name/filename (.NET varsayılanı boundary'yi tırnaklar ve filename*
        // ekler; SIT ilk denemede "File not found" döndü, 2026-11-20).
        var boundary = "----PazaryeriHub" + Guid.NewGuid().ToString("N");
        var form = new MultipartFormDataContent(boundary);
        form.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=" + boundary);
        // curl -F file=@integrator.json ile aynı: parça türü application/octet-stream, içerik UTF-8 bayt
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(productsJson));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        file.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"file\"", FileName = "\"integrator.json\"" };
        form.Add(file);
        return SendContentAsync("Ürün bilgisi gönderme", HttpMethod.Post, $"{ProductBaseUrl}/api/products/import?version=1", form,
            "multipart/form-data; file=integrator.json; " + productsJson, ct);
    }

    public Task<SitCall> ApprovePreMatchAsync(IReadOnlyList<string> merchantSkus, CancellationToken ct) =>
        SendAsync("Eşleşen statü onay", HttpMethod.Post, $"{ProductBaseUrl}/api/products/approve-prematch",
            new[] { new { merchant = _creds.MerchantId, merchantSkuList = merchantSkus } }, CamelCase, ct);

    public Task<SitCall> RejectPreMatchAsync(IReadOnlyList<string> merchantSkus, CancellationToken ct) =>
        SendAsync("Eşleşen statü red", HttpMethod.Post, $"{ProductBaseUrl}/api/products/reject-prematch",
            new[] { new { merchant = _creds.MerchantId, merchantSkuList = merchantSkus } }, CamelCase, ct);

    public Task<SitCall> ProductStatusAsync(string trackingId, CancellationToken ct) =>
        SendAsync("Ürün durumu sorgulama", HttpMethod.Get,
            $"{ProductBaseUrl}/api/products/status/{Uri.EscapeDataString(trackingId)}?version=1&page=0&size=1000", null, null, ct);

    // ------------------------------------------------------------------ Listeleme

    public Task<SitCall> ListingsAsync(CancellationToken ct) =>
        SendAsync("Listing bilgilerini sorgulama", HttpMethod.Get, $"{Listings}?offset=0&limit=100", null, null, ct);

    public Task<SitCall> InventoryUploadAsync(IReadOnlyList<SitInventoryItem> items, CancellationToken ct) =>
        SendAsync("Listing envanter güncelleme", HttpMethod.Post, $"{Listings}/inventory-uploads",
            items.Select(i => new { hepsiburadaSku = i.HepsiburadaSku, merchantSku = i.MerchantSku, price = i.Price, availableStock = i.AvailableStock }).ToList(),
            CamelCase, ct);

    public Task<SitCall> InventoryUploadStatusAsync(string inventoryUploadId, CancellationToken ct) =>
        SendAsync("Listing envanter güncelleme sorgulama", HttpMethod.Get,
            $"{Listings}/inventory-uploads/id/{Uri.EscapeDataString(inventoryUploadId)}", null, null, ct);

    public Task<SitCall> ActivateAsync(string sku, CancellationToken ct) =>
        SendAsync("Listing activate", HttpMethod.Post, $"{Listings}/sku/{Uri.EscapeDataString(sku)}/activate", new { }, CamelCase, ct);

    public Task<SitCall> DeactivateAsync(string sku, CancellationToken ct) =>
        SendAsync("Listing deactivate", HttpMethod.Post, $"{Listings}/sku/{Uri.EscapeDataString(sku)}/deactivate", new { }, CamelCase, ct);

    public Task<SitCall> BulkUnlockAsync(IReadOnlyList<string> hbSkus, CancellationToken ct) =>
        SendAsync("Toplu kilit kaldırma", HttpMethod.Post, $"{Listings}/bulk-unlock", new { hbSkuList = hbSkus }, CamelCase, ct);

    // ------------------------------------------------------------------ Sipariş

    /// <summary>
    /// Test siparişi (yalnızca TEST ortamında). Müşteri/adres alanları dokümanda isteğe bağlı; gerçek kişi verisi GÖNDERİLMEZ,
    /// yalnızca açıkça "test" olan ad ve şehir/semt gönderilir (telefon, e-posta yok).
    /// </summary>
    /// <param name="orderNumber">Dokümanda opsiyonel; verilmezse gönderilmez. Test servisi boş sipariş numarasını reddediyor
    /// ("'Order Number' must not be empty" / "Order Number is not valid"). // TODO: doğrulanmalı — geçerli biçim dokümanda yok.</param>
    /// <param name="orderDate">Destek örneğindeki biçim: saat dilimsiz <c>yyyy-MM-ddTHH:mm:ss</c>; verilmezse gönderilmez.</param>
    public Task<SitCall> CreateTestOrderAsync(SitTestOrderLine line, CancellationToken ct, string? orderNumber = null, string? orderDate = null)
    {
        ArgumentNullException.ThrowIfNull(line);
        var item = new Dictionary<string, object?>
        {
            ["MerchantId"] = _creds.MerchantId,
            ["Sku"] = line.HepsiburadaSku,
            ["MerchantSku"] = line.MerchantSku,
            ["Quantity"] = line.Quantity,
            ["Price"] = new { Amount = line.UnitPrice, Currency = "TRY" },
            ["TotalPrice"] = new { Amount = line.UnitPrice * line.Quantity, Currency = "TRY" }
        };
        if (line.DeliveryOptionId is { } option)
        {
            item["DeliveryOptionId"] = option;
        }
        if (line.CargoCompanyId is { } cargo)
        {
            item["CargoCompanyId"] = cargo;
        }
        var body = new Dictionary<string, object?>
        {
            ["Customer"] = new { Name = "PazaryeriHub Test Müşteri" },
            ["DeliveryAddress"] = new { Name = "PazaryeriHub Test", City = "İstanbul", District = "Test", AddressDetail = "Test adresi", CountryCode = "TR" },
            ["LineItems"] = new[] { item }
        };
        if (orderNumber is not null)
        {
            body["OrderNumber"] = orderNumber;
        }
        if (orderDate is not null)
        {
            body["OrderDate"] = orderDate;
        }
        return SendAsync("Test siparişi oluşturma", HttpMethod.Post, $"{OrderStubBaseUrl}/orders/merchantId/{_creds.MerchantId}", body, PascalCase, ct);
    }

    public Task<SitCall> PaidOrdersAsync(CancellationToken ct) =>
        SendAsync("Ödemesi tamamlanmış siparişleri listeleme", HttpMethod.Get, $"{OrderBaseUrl}/orders/merchantid/{_creds.MerchantId}?offset=0&limit=100", null, null, ct);

    public Task<SitCall> PackageableWithAsync(string lineItemId, CancellationToken ct) =>
        SendAsync("Aynı pakete konulabilecek kalemleri listeleme", HttpMethod.Get,
            $"{OrderBaseUrl}/lineitems/merchantid/{_creds.MerchantId}/packageablewith/lineitemid/{Uri.EscapeDataString(lineItemId)}", null, null, ct);

    public Task<SitCall> PackagesAsync(CancellationToken ct) =>
        SendAsync("Satıcıya ait paket bilgilerini listeleme", HttpMethod.Get, $"{OrderBaseUrl}/packages/merchantid/{_creds.MerchantId}?offset=0&limit=50", null, null, ct);

    // ------------------------------------------------------------------ Gönderim

    private string Listings => $"{ListingBaseUrl}/listings/merchantid/{_creds.MerchantId}";

    private Task<SitCall> SendAsync(string step, HttpMethod method, string url, object? body, JsonSerializerOptions? options, CancellationToken ct)
    {
        string? requestBody = body is null ? null : JsonSerializer.Serialize(body, options ?? CamelCase);
        var content = requestBody is null ? null : new StringContent(requestBody, Encoding.UTF8, "application/json");
        return SendContentAsync(step, method, url, content, requestBody, ct);
    }

    private async Task<SitCall> SendContentAsync(string step, HttpMethod method, string url, HttpContent? content, string? requestBody, CancellationToken ct)
    {
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.EndsWith("-sit.hepsiburada.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Güvenlik: yalnızca Hepsiburada test (SIT) adreslerine istek gönderilir ({uri.Host}).");
        }

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_creds.UserName}:{_creds.Password}")));
        request.Headers.TryAddWithoutValidation("User-Agent", _creds.EffectiveUserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = content;

        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await _http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            return new SitCall(step, method.Method, url, requestBody, (int)response.StatusCode, text, watch.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            return new SitCall(step, method.Method, url, requestBody, 0, $"Bağlantı hatası: {ex.Message}", watch.ElapsedMilliseconds);
        }
    }
}
