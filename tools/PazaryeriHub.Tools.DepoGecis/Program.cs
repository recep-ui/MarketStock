// Depo Excel'i → ortak SKU geçiş aracı (ADR-059).
// Kullanım:
//   dotnet run --project tools/PazaryeriHub.Tools.DepoGecis -- deneme --onay <Depo-Ortak-SKU-Onay.xlsx> (--test | --kurulu) [--rapor <dosya.txt>]
//   dotnet run --project tools/PazaryeriHub.Tools.DepoGecis -- uygula  --onay <...xlsx> (--test | --kurulu) [--rapor <dosya.txt>]
// --test   : PazaryeriHub_Test veritabanı (test projesinin user-secrets'ı ya da PAZARYERIHUB_TEST_DB)
// --kurulu : kurulu sistem (C:\ProgramData\PazaryeriHub\ayarlar.json); uygulamadan önce veritabanı yedeği alınmalı.
// Bağlantı dizesi ve kimlik bilgisi yazdırılmaz.
using System.Globalization;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PazaryeriHub.Application.DepotMigration;
using PazaryeriHub.Infrastructure;
using PazaryeriHub.Infrastructure.DepotMigration;
using PazaryeriHub.Infrastructure.Persistence;

Console.OutputEncoding = Encoding.UTF8;
var step = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "deneme";
string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
if (step is not ("deneme" or "uygula") || Arg("--onay") is not { } onayPath || args.Contains("--test") == args.Contains("--kurulu"))
{
    Console.WriteLine("Kullanım: (deneme | uygula) --onay <dosya.xlsx> (--test | --kurulu) [--rapor <dosya.txt>]");
    return 1;
}

string connectionString;
string? keysPath = null;
if (args.Contains("--test"))
{
    connectionString = TestConnectionString() ?? throw new InvalidOperationException("Test veritabanı bağlantısı yok.");
    if (new SqlConnectionStringBuilder(connectionString).InitialCatalog != "PazaryeriHub_Test")
    {
        Console.WriteLine("Güvenlik: --test yalnızca PazaryeriHub_Test veritabanında çalışır.");
        return 2;
    }
}
else
{
    using var cfg = JsonDocument.Parse(File.ReadAllText(@"C:\ProgramData\PazaryeriHub\ayarlar.json"));
    connectionString = cfg.RootElement.GetProperty("ConnectionStrings").GetProperty("PazaryeriHub").GetString()!;
    keysPath = cfg.RootElement.TryGetProperty("DataProtection", out var dp) && dp.TryGetProperty("KeysPath", out var kp) ? kp.GetString() : null;
}

var (rows, assignments, readWarnings) = ReadApproval(onayPath);
Console.WriteLine($"Onay dosyası: {rows.Count} depo satırı, {assignments.Count} atama ({assignments.Count(a => a.ListingId is not null)} ilan, "
    + $"{assignments.Count(a => a.UnmatchedId is not null)} eşleşmeyen).");

var services = new ServiceCollection();
services.AddLogging();
var dataProtection = services.AddDataProtection().SetApplicationName("PazaryeriHub");
if (keysPath is not null && OperatingSystem.IsWindows())
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath)).ProtectKeysWithDpapi(protectToLocalMachine: true);
}
services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?> { [$"ConnectionStrings:{DatabaseOptions.ConnectionStringName}"] = connectionString })
    .Build());
services.AddPazaryeriHubInfrastructure();
await using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var result = await scope.ServiceProvider.GetRequiredService<DepotMigrationService>()
    .RunAsync(rows, assignments, apply: step == "uygula", user: "depo-gecis", CancellationToken.None);

var plan = result.Plan;
var report = new StringBuilder();
report.AppendLine(CultureInfo.InvariantCulture, $"Depo → ortak SKU geçişi ({(result.Applied ? "UYGULANDI" : "DENEME")}, {DateTime.Now:yyyy-MM-dd HH:mm})");
report.AppendLine(CultureInfo.InvariantCulture, $"Yeni SKU: {plan.NewSkuCount} (toplam stok {plan.NewStockTotal}), mevcut koda denk gelen: {plan.Skus.Count - plan.NewSkuCount}");
report.AppendLine(CultureInfo.InvariantCulture, $"Taşınacak ilan: {plan.Moves.Count}, bağlanacak eşleşmeyen: {plan.Links.Count}, sıfırlanacak eski SKU: {plan.ZeroOldSkus.Count} (toplam {plan.ZeroOldSkus.Sum(z => z.Physical)} adet)");
if (result.Applied)
{
    report.AppendLine(CultureInfo.InvariantCulture, $"Yapılan: SKU {result.SkusCreated}, stok sayımı {result.StockCounted}, ilan {result.ListingsMoved}, eşleşmeyen {result.UnmatchedLinked}, eski SKU sıfırlama {result.OldSkusZeroed}");
}
void Section(string title, IEnumerable<string> lines)
{
    var list = lines.ToList();
    if (list.Count == 0) return;
    report.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"== {title} ({list.Count})");
    foreach (var l in list) report.AppendLine("  " + l);
}
Section("HATALAR (uygulanmaz)", plan.Errors);
Section("Uygulama hataları", result.Failures);
Section("Uyarılar", readWarnings.Concat(plan.Warnings));
Section("Sıfırlanacak eski SKU'lar", plan.ZeroOldSkus.Select(z => $"SKU {z.SkuId}: {z.Physical} → 0"));
Section("Taşınacak ilanlar", plan.Moves.Select(m => $"ilan {m.ListingId}: SKU {m.FromSkuId} → {m.Row.SkuCode} (depo satırı {m.Row.Row})"));
Section("Bağlanacak eşleşmeyenler", plan.Links.Select(l => $"eşleşmeyen {l.UnmatchedId} → {l.Row.SkuCode} (depo satırı {l.Row.Row})"));

var text = report.ToString();
Console.WriteLine(string.Join(Environment.NewLine, text.Split(Environment.NewLine).Take(25)));
if (Arg("--rapor") is { } reportPath)
{
    File.WriteAllText(reportPath, text, new UTF8Encoding(true));
    Console.WriteLine($"Rapor: {reportPath}");
}
return plan.Errors.Count > 0 || result.Failures.Count > 0 ? 3 : 0;

static (List<DepotRow> Rows, List<DepotAssignment> Assignments, List<string> Warnings) ReadApproval(string path)
{
    using var wb = new XLWorkbook(path);
    List<string> warnings = [];
    var depot = wb.Worksheet("Depo satırları");
    var dh = Headers(depot);
    var rows = new List<DepotRow>();
    foreach (var r in depot.RowsUsed().Skip(1))
    {
        if (r.Cell(dh["Depo satırı"]).GetValue<string>() is not { Length: > 0 } rowText) continue;
        var qtyText = r.Cell(dh["Serbest Miktar"]).GetValue<string>();
        rows.Add(new DepotRow(
            int.Parse(rowText, CultureInfo.InvariantCulture),
            r.Cell(dh["Model"]).GetValue<string>().Trim(),
            r.Cell(dh["Varyant"]).GetValue<string>(),
            r.Cell(dh["Ürün Cinsi"]).GetValue<string>(),
            r.Cell(dh["Beden"]).GetValue<string>(),
            string.IsNullOrWhiteSpace(qtyText) ? 0 : (int)decimal.Parse(qtyText, CultureInfo.InvariantCulture),
            r.Cell(dh["Ortak SKU kodu"]).GetValue<string>().Trim()));
    }

    var channel = wb.Worksheet("Kanal ürünleri");
    var ch = Headers(channel);
    var assignments = new List<DepotAssignment>();
    foreach (var r in channel.RowsUsed().Skip(1))
    {
        var chosen = r.Cell(ch["Onaylanan depo satırı"]).GetValue<string>().Trim();
        if (chosen.Length == 0 || chosen.Equals("YOK", StringComparison.OrdinalIgnoreCase)) continue;
        if (!int.TryParse(chosen, NumberStyles.Integer, CultureInfo.InvariantCulture, out var depotRow))
        {
            warnings.Add($"Kanal ürünleri satır {r.RowNumber()}: '{chosen}' depo satır numarası değil; atlandı.");
            continue;
        }
        var listing = r.Cell(ch["İlan Id"]).GetValue<string>();
        var unmatched = r.Cell(ch["Eşleşmeyen Id"]).GetValue<string>();
        if (int.TryParse(listing, out var lid)) assignments.Add(new DepotAssignment(depotRow, lid, null));
        else if (int.TryParse(unmatched, out var uid)) assignments.Add(new DepotAssignment(depotRow, null, uid));
        else warnings.Add($"Kanal ürünleri satır {r.RowNumber()}: ilan/eşleşmeyen kimliği yok; atlandı.");
    }
    return (rows, assignments, warnings);
}

static Dictionary<string, int> Headers(IXLWorksheet ws) =>
    ws.Row(1).CellsUsed().ToDictionary(c => c.GetValue<string>().Trim(), c => c.Address.ColumnNumber, StringComparer.Ordinal);

static string? TestConnectionString()
{
    if (Environment.GetEnvironmentVariable("PAZARYERIHUB_TEST_DB") is { Length: > 0 } env) return env;
    var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "UserSecrets", "pazaryerihub-tests-integration-4b1e9a2c", "secrets.json");
    if (!File.Exists(file)) return null;
    using var doc = JsonDocument.Parse(File.ReadAllText(file));
    return doc.RootElement.TryGetProperty("ConnectionStrings:PazaryeriHubTest", out var v) ? v.GetString() : null;
}
