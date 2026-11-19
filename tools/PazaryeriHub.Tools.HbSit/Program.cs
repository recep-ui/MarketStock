using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PazaryeriHub.Adapters.Hepsiburada;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Domain.Orders;
using PazaryeriHub.Infrastructure.Channels;
using PazaryeriHub.Infrastructure.Persistence;
using PazaryeriHub.Tools.HbSit;

// Hepsiburada test ortamı (SIT) doğrulama aracı (ADR-037).
// Kullanım: dotnet run --project tools/PazaryeriHub.Tools.HbSit -- <kontrol|katalog|listeleme|siparis|hepsi> [--evet] [--hbsku HBSKU]
//  - Kimlik bilgisi panelde girilen (PazaryeriHub_Test veritabanındaki, şifreli) Hepsiburada bilgisidir; ortamı "Test" olmalı.
//  - Yalnızca *-sit.hepsiburada.com adreslerine istek gider (SitRecorder). Veritabanına hiçbir şey yazılmaz.
//  - Her yazma adımı ne göndereceğini gösterip onay ister (--evet ile sorulmaz). Rapor: hb-sit-rapor/<zaman>/
Console.OutputEncoding = Encoding.UTF8;
var step = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))?.ToLowerInvariant() ?? "kontrol";
var autoYes = args.Contains("--evet");
var hbSkuArg = args.SkipWhile(a => a != "--hbsku").Skip(1).FirstOrDefault();
string[] known = ["kontrol", "katalog", "listeleme", "siparis", "hepsi"];
if (!known.Contains(step))
{
    Console.WriteLine($"Bilinmeyen adım '{step}'. Adımlar: {string.Join(", ", known)}");
    return 1;
}

var root = FindRoot();
var connectionString = TestConnectionString()
    ?? throw new InvalidOperationException("Test veritabanı bağlantısı yok (PAZARYERIHUB_TEST_DB veya test projesinin user-secrets'ı).");
if (new SqlConnectionStringBuilder(connectionString).InitialCatalog != "PazaryeriHub_Test")
{
    Console.WriteLine("Güvenlik: araç yalnızca PazaryeriHub_Test veritabanındaki kanal bilgisini okur.");
    return 2;
}

// Kimlik bilgisi: panelin kullandığı şifreli depo (aynı Windows kullanıcısı + "PazaryeriHub" uygulama adı → aynı anahtarlar)
var services = new ServiceCollection();
services.AddLogging();
services.AddDataProtection().SetApplicationName("PazaryeriHub");
services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?> { [$"ConnectionStrings:{DatabaseOptions.ConnectionStringName}"] = connectionString })
    .Build());
services.AddSingleton<SqlConnectionFactory>();
services.AddSingleton<ChannelCredentialStore>();
await using var provider = services.BuildServiceProvider();
var credsJson = await provider.GetRequiredService<ChannelCredentialStore>().GetJsonAsync(HepsiburadaAdapter.Code, CancellationToken.None);
if (credsJson is null)
{
    Console.WriteLine("Hepsiburada API bilgisi girilmemiş (panel → Ayarlar → Kanallar → Hepsiburada).");
    return 2;
}
var creds = HepsiburadaCredentials.Parse(credsJson);
if (creds.Environment != HepsiburadaCredentials.Test)
{
    Console.WriteLine("Güvenlik: Hepsiburada kanalının ortamı 'Canlı'. Bu araç yalnızca test (SIT) ortamında çalışır; panelde ortamı 'Test' yapın.");
    return 2;
}

var recorder = new SitRecorder();
using var http = new HttpClient(recorder) { Timeout = TimeSpan.FromSeconds(60) };
var sit = new HepsiburadaSitClient(http, creds);
var adapter = new HepsiburadaAdapter(http, new FixedCredentials(credsJson), TimeProvider.System, NullLogger<HepsiburadaAdapter>.Instance);
var ct = CancellationToken.None;
var findings = new List<(string Area, string Item, string Value)>();

var reportDir = Path.Combine(root, "hb-sit-rapor", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + step);
Directory.CreateDirectory(reportDir);
Console.WriteLine($"Hepsiburada TEST (SIT) doğrulama — adım: {step}; merchantId: {creds.MerchantId}");
Console.WriteLine($"Rapor klasörü: {reportDir}");

var exitCode = 0;
try
{
    var listing = await ChooseListingAsync();
    if (step is "katalog" or "hepsi") await CatalogAsync();
    if (step is "listeleme" or "hepsi") await ListingAsync(listing);
    if (step is "siparis" or "hepsi") await OrderAsync(listing);
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Console.WriteLine($"HATA: {ex.Message}");
    findings.Add(("Genel", "Hata", ex.Message));
    exitCode = 3;
}
finally
{
    WriteReport();
}
return exitCode;

// ------------------------------------------------------------------ Adımlar

async Task<ListingInfo?> ChooseListingAsync()
{
    recorder.CurrentStep = "Listing bilgilerini sorgulama";
    var call = await sit.ListingsAsync(ct);
    Show(call.Step, call.Status, call.ResponseBody);
    var listings = ParseListings(call.ResponseBody);
    findings.Add(("Listeleme", "Listing sorgulama", $"HTTP {call.Status}, {listings.Count} listing"));
    if (listings.Count == 0)
    {
        Console.WriteLine("Test envanterinde listing bulunamadı.");
        return null;
    }
    var chosen = hbSkuArg is null ? listings[0] : listings.FirstOrDefault(l => l.HbSku == hbSkuArg)
        ?? throw new InvalidOperationException($"--hbsku {hbSkuArg} test envanterinde yok.");
    Console.WriteLine($"Kullanılacak test listing'i: HBSKU {chosen.HbSku}, satıcı SKU {chosen.MerchantSku ?? "—"}, stok {chosen.Stock?.ToString(CultureInfo.InvariantCulture) ?? "—"}, fiyat {chosen.Price?.ToString(CultureInfo.InvariantCulture) ?? "—"}");
    return chosen;
}

async Task CatalogAsync()
{
    // Dokümandaki test barkodları ("Bu methodu test etmek için sadece ilettiğimiz barkodları kullanmanız gerekmektedir")
    string[] barcodes = ["7541828790114", "7541828790155", "7541828790080"];
    var items = barcodes.Select(b => new SitFastListingItem($"PH-SIT-{b}", $"PazaryeriHub SIT test ürünü {b}", b, Stock: 1, Price: 100m)).ToList();
    if (!Confirm("Hızlı ürün yükleme (katalog)", JsonSerializer.Serialize(items))) return;

    recorder.CurrentStep = "Hızlı ürün yükleme";
    var upload = await sit.FastListingAsync(items, ct);
    Show(upload.Step, upload.Status, upload.ResponseBody);
    var trackingId = upload.Find("trackingId");
    findings.Add(("Katalog", "Hızlı ürün yükleme", $"HTTP {upload.Status}"));
    findings.Add(("Katalog", "trackingId", trackingId ?? "YANITTA BULUNAMADI (ham yanıta bakın)"));
    if (trackingId is null) return;

    recorder.CurrentStep = "Ürün durumu sorgulama";
    for (var i = 0; i < 6; i++)
    {
        var status = await sit.ProductStatusAsync(trackingId, ct);
        Show(status.Step + $" #{i + 1}", status.Status, status.ResponseBody);
        if (!status.Success) break;
        if (i < 5) await Task.Delay(TimeSpan.FromSeconds(5), ct);
    }
    findings.Add(("Katalog", "Ürün durumu sorgulama", "yanıtlar raporda"));
}

async Task ListingAsync(ListingInfo? l)
{
    if (l is null) return;
    var stock = l.Stock is > 0 ? l.Stock.Value : 5;
    var price = l.Price is > 0 ? l.Price.Value : 100m;

    if (Confirm("Listing envanter güncelleme", $"HBSKU {l.HbSku}: stok {stock}, fiyat {price}"))
    {
        recorder.CurrentStep = "Listing envanter güncelleme";
        var inv = await sit.InventoryUploadAsync([new SitInventoryItem(l.HbSku, l.MerchantSku, price, stock)], ct);
        Show(inv.Step, inv.Status, inv.ResponseBody);
        var id = inv.Find("id");
        findings.Add(("Listeleme", "Envanter güncelleme", $"HTTP {inv.Status}, id {id ?? "—"}"));
        if (id is not null)
        {
            recorder.CurrentStep = "Listing envanter güncelleme sorgulama";
            for (var i = 0; i < 4; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                var s = await sit.InventoryUploadStatusAsync(id, ct);
                Show(s.Step + $" #{i + 1}", s.Status, s.ResponseBody);
                if (s.Find("status") is { } st && st.Equals("Done", StringComparison.OrdinalIgnoreCase)) break;
            }
        }
    }

    if (Confirm("Listing stok güncelleme (uygulamanın adaptörüyle)", $"HBSKU {l.HbSku}: stok {stock}"))
    {
        recorder.CurrentStep = "Listing stok güncelleme";
        await BatchAsync("Stok güncelleme", () => adapter.PushStockAsync([new StockUpdate(l.HbSku, null, stock)], ct));
    }

    if (Confirm("Listing fiyat güncelleme (uygulamanın adaptörüyle)", $"HBSKU {l.HbSku}: fiyat {price}"))
    {
        recorder.CurrentStep = "Listing fiyat güncelleme";
        await BatchAsync("Fiyat güncelleme", () => adapter.PushPriceAsync([new PriceUpdate(l.HbSku, null, price, price)], ct));
    }

    if (Confirm("Listing deactivate + activate", $"HBSKU {l.HbSku} önce satışa kapatılır, sonra yeniden açılır"))
    {
        recorder.CurrentStep = "Listing deactivate";
        var off = await sit.DeactivateAsync(l.HbSku, ct);
        Show(off.Step, off.Status, off.ResponseBody);
        recorder.CurrentStep = "Listing activate";
        var on = await sit.ActivateAsync(l.HbSku, ct);
        Show(on.Step, on.Status, on.ResponseBody);
        findings.Add(("Listeleme", "Deactivate / Activate", $"HTTP {off.Status} / {on.Status}"));
    }

    if (Confirm("Toplu kilit kaldırma", $"hbSkuList: [{l.HbSku}]"))
    {
        recorder.CurrentStep = "Toplu kilit kaldırma";
        var unlock = await sit.BulkUnlockAsync([l.HbSku], ct);
        Show(unlock.Step, unlock.Status, unlock.ResponseBody);
        findings.Add(("Listeleme", "Toplu kilit kaldırma", $"HTTP {unlock.Status}"));
    }
}

async Task BatchAsync(string name, Func<Task<BatchResult>> push)
{
    try
    {
        var result = await push();
        findings.Add(("Listeleme", name, $"takip no {result.BatchId}"));
        Console.WriteLine($"{name}: takip no {result.BatchId}");
        recorder.CurrentStep = name + " sorgulama";
        for (var i = 0; i < 5 && result.BatchId is not null; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            var status = await adapter.CheckBatchAsync(result.BatchId, ct);
            Console.WriteLine($"{name} sorgulama #{i + 1}: {status.State}{(status.Errors.Count > 0 ? " — " + string.Join("; ", status.Errors.Select(e => e.Message)) : "")}");
            if (status.State != BatchState.Pending)
            {
                findings.Add(("Listeleme", name + " sonucu", status.State.ToString()));
                break;
            }
        }
    }
    catch (ChannelApiException ex)
    {
        Console.WriteLine($"{name} başarısız: {ex.Message}");
        findings.Add(("Listeleme", name, "HATA: " + ex.Message));
    }
}

async Task OrderAsync(ListingInfo? l)
{
    if (l is null) return;
    var price = l.Price is > 0 ? l.Price.Value : 100m;
    if (!Confirm("Test siparişi oluşturma", $"HBSKU {l.HbSku}, 1 adet, {price} TRY; müşteri: 'PazaryeriHub Test Müşteri' (gerçek kişi verisi yok)")) return;

    var startedUtc = DateTime.UtcNow;
    recorder.CurrentStep = "Test siparişi oluşturma";
    // Test servisi sipariş numarası istiyor (dokümanda opsiyonel); 10 haneli sayısal numara kabul edildi (SIT, 2026-11-19). ShippingMethodId: ADR-037 açık soru.
    var testOrderNo = "9" + (DateTime.UtcNow.Ticks % 1_000_000_000).ToString("D9", CultureInfo.InvariantCulture);
    var created = await sit.CreateTestOrderAsync(new SitTestOrderLine(l.HbSku, l.MerchantSku, 1, price), ct, testOrderNo);
    Show(created.Step, created.Status, created.ResponseBody);
    var orderNumber = created.Find("orderNumber") ?? (created.Success ? testOrderNo : null);
    findings.Add(("Sipariş", "Test siparişi oluşturma", $"HTTP {created.Status}, sipariş no {orderNumber ?? "—"}"));
    if (!created.Success) return;

    // Ödemesi tamamlanmış listede görünmesi beklenir (HB otomatik paketleme açıksa doğrudan paketlere düşer)
    (string Id, string OrderNumber, int Quantity)? line = null;
    recorder.CurrentStep = "Ödemesi tamamlanmış siparişleri listeleme";
    for (var i = 0; i < 10 && line is null; i++)
    {
        await Task.Delay(TimeSpan.FromSeconds(6), ct);
        var paid = await sit.PaidOrdersAsync(ct);
        Show(paid.Step + $" #{i + 1}", paid.Status, paid.ResponseBody, brief: true);
        line = FindOrderLine(paid.ResponseBody, l.HbSku, orderNumber);
    }
    findings.Add(("Sipariş", "Ödemesi tamamlanmış siparişlerde", line is null ? "GÖRÜNMEDİ (otomatik paketleme açık olabilir; paket listesine bakın)" : $"kalem {line.Value.Id}, sipariş {line.Value.OrderNumber}"));

    recorder.CurrentStep = "Sipariş çekme (uygulamanın adaptörüyle)";
    try
    {
        var orders = await adapter.FetchOrdersAsync(startedUtc.AddMinutes(-10), ct);
        var mine = orders.Where(o => o.Lines.Any(x => x.ChannelProductCode == l.HbSku)).ToList();
        Console.WriteLine($"Adaptör sipariş çekme: {orders.Count} sipariş, bu listing'i içeren {mine.Count}");
        findings.Add(("Sipariş", "Adaptörle sipariş çekme", $"{orders.Count} sipariş; listing'i içeren {mine.Count}"));
    }
    catch (ChannelApiException ex)
    {
        findings.Add(("Sipariş", "Adaptörle sipariş çekme", "HATA: " + ex.Message));
    }

    if (line is { } found)
    {
        recorder.CurrentStep = "Aynı pakete konulabilecek kalemleri listeleme";
        var packageable = await sit.PackageableWithAsync(found.Id, ct);
        Show(packageable.Step, packageable.Status, packageable.ResponseBody);
        findings.Add(("Sipariş", "Aynı pakete konulabilecek kalemler", $"HTTP {packageable.Status}"));

        if (Confirm("Kalem paketleme (uygulamanın adaptörüyle)", $"lineItemRequests: [{{id: {found.Id}, quantity: {found.Quantity}}}]"))
        {
            recorder.CurrentStep = "Kalem veya kalemleri paketleme";
            try
            {
                await adapter.UpdateOrderStatusAsync(new OrderStatusUpdate(found.OrderNumber, null, OrderStatus.Hazirlaniyor,
                    [new OrderStatusUpdateLine(found.Id, found.Quantity)], null, false), ct);
                var packed = recorder.Calls.Last(c => c.Step == recorder.CurrentStep);
                Show("Kalem paketleme", packed.Status, packed.ResponseBody);
                findings.Add(("Sipariş", "Paketleme", $"HTTP {packed.Status}, paket no {SitCall.FindValue(packed.ResponseBody, "packageNumber") ?? "—"}"));
            }
            catch (ChannelApiException ex)
            {
                Console.WriteLine($"Paketleme başarısız: {ex.Message}");
                findings.Add(("Sipariş", "Paketleme", "HATA: " + ex.Message));
            }
        }
    }

    recorder.CurrentStep = "Satıcıya ait paket bilgilerini listeleme";
    var packages = await sit.PackagesAsync(ct);
    Show(packages.Step, packages.Status, packages.ResponseBody, brief: true);
    findings.Add(("Sipariş", "Paket listesi", $"HTTP {packages.Status}"));
}

// ------------------------------------------------------------------ Yardımcılar

bool Confirm(string what, string detail)
{
    Console.WriteLine();
    Console.WriteLine($"→ {what} (YAZMA, yalnızca test ortamı): {detail}");
    if (autoYes) return true;
    if (Console.IsInputRedirected)
    {
        Console.WriteLine("  Etkileşimsiz çalışma: onay alınamadı, atlandı (--evet ile çalıştırın).");
        findings.Add(("Atlandı", what, "onay yok"));
        return false;
    }
    Console.Write("  Gönderilsin mi? [e/H] ");
    var ok = (Console.ReadLine() ?? "").Trim().StartsWith('e');
    if (!ok) findings.Add(("Atlandı", what, "kullanıcı onaylamadı"));
    return ok;
}

void Show(string title, int status, string body, bool brief = false)
{
    var text = body.Length > (brief ? 300 : 1200) ? body[..(brief ? 300 : 1200)] + " …" : body;
    Console.WriteLine($"[{title}] HTTP {status}: {text}");
}

void WriteReport()
{
    File.WriteAllText(Path.Combine(reportDir, "cagrilar.json"),
        JsonSerializer.Serialize(recorder.Calls, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    var md = new StringBuilder();
    md.AppendLine($"# Hepsiburada test ortamı (SIT) doğrulama raporu — {step}");
    md.AppendLine();
    md.AppendLine($"- Zaman: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (yerel)");
    md.AppendLine($"- Merchant ID: {creds.MerchantId}");
    md.AppendLine("- Yalnızca *-sit.hepsiburada.com adreslerine istek gönderildi; Authorization başlığı rapora yazılmadı.");
    md.AppendLine();
    md.AppendLine("## Özet");
    md.AppendLine();
    md.AppendLine("| Alan | Adım | Sonuç |");
    md.AppendLine("|---|---|---|");
    foreach (var (area, item, value) in findings)
    {
        md.AppendLine($"| {area} | {item} | {value.Replace("|", "/")} |");
    }
    md.AppendLine();
    md.AppendLine("## Çağrılar");
    md.AppendLine();
    md.AppendLine("| Zaman (UTC) | Adım | Metot | Uç | HTTP | Süre (ms) |");
    md.AppendLine("|---|---|---|---|---|---|");
    foreach (var c in recorder.Calls)
    {
        md.AppendLine($"| {c.AtUtc:HH:mm:ss} | {c.Step} | {c.Method} | {c.Url} | {c.Status} | {c.ElapsedMs} |");
    }
    md.AppendLine();
    md.AppendLine("Ham istek ve yanıt gövdeleri: `cagrilar.json`.");
    File.WriteAllText(Path.Combine(reportDir, "rapor.md"), md.ToString());
    Console.WriteLine();
    Console.WriteLine($"Rapor yazıldı: {Path.Combine(reportDir, "rapor.md")}");
}

static List<ListingInfo> ParseListings(string json)
{
    var result = new List<ListingInfo>();
    try
    {
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
            : doc.RootElement.TryGetProperty("listings", out var l) ? l : default;
        if (arr.ValueKind != JsonValueKind.Array) return result;
        foreach (var i in arr.EnumerateArray())
        {
            var hb = Str(i, "hepsiburadaSku");
            if (hb is null) continue;
            result.Add(new ListingInfo(hb, Str(i, "merchantSku"), Num(i, "availableStock") is { } s ? (int)s : null, Num(i, "price")));
        }
    }
    catch (JsonException)
    {
        // boş liste
    }
    return result;
}

static (string Id, string OrderNumber, int Quantity)? FindOrderLine(string json, string hbSku, string? orderNumber)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
            : doc.RootElement.TryGetProperty("items", out var it) ? it : default;
        if (items.ValueKind != JsonValueKind.Array) return null;
        foreach (var i in items.EnumerateArray().Reverse())
        {
            var no = Str(i, "orderNumber");
            if (Str(i, "sku") == hbSku && (orderNumber is null || no == orderNumber) && Str(i, "id") is { } id)
            {
                return (id, no ?? "", Num(i, "quantity") is { } q ? (int)q : 1);
            }
        }
    }
    catch (JsonException)
    {
        // bulunamadı
    }
    return null;
}

static string? Str(JsonElement e, string name)
{
    foreach (var p in e.EnumerateObject())
    {
        if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            return p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Number => p.Value.GetRawText(),
                _ => null
            };
        }
    }
    return null;
}

static decimal? Num(JsonElement e, string name)
{
    foreach (var p in e.EnumerateObject())
    {
        if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDecimal(out var d)) return d;
            if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("amount", out var a) && a.TryGetDecimal(out var amount)) return amount;
            if (p.Value.ValueKind == JsonValueKind.String && decimal.TryParse(p.Value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s)) return s;
        }
    }
    return null;
}

static string FindRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PazaryeriHub.slnx"))) dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

static string? TestConnectionString()
{
    if (Environment.GetEnvironmentVariable("PAZARYERIHUB_TEST_DB") is { Length: > 0 } env) return env;
    var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "UserSecrets", "pazaryerihub-tests-integration-4b1e9a2c", "secrets.json");
    if (!File.Exists(file)) return null;
    using var doc = JsonDocument.Parse(File.ReadAllText(file));
    return doc.RootElement.TryGetProperty("ConnectionStrings:PazaryeriHubTest", out var v) ? v.GetString() : null;
}

namespace PazaryeriHub.Tools.HbSit
{
    public sealed record ListingInfo(string HbSku, string? MerchantSku, int? Stock, decimal? Price);

    /// <summary>Adaptöre paneldeki (çözülmüş) kimlik bilgisini verir; yazma yapmaz.</summary>
    public sealed class FixedCredentials(string json) : IChannelCredentialStore
    {
        public Task<string?> GetJsonAsync(string channelCode, CancellationToken ct) => Task.FromResult<string?>(json);
        public Task SaveJsonAsync(string channelCode, string json, CancellationToken ct) => throw new NotSupportedException();
        public Task ClearAsync(string channelCode, CancellationToken ct) => throw new NotSupportedException();
    }
}
