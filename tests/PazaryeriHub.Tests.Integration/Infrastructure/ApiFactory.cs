using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using PazaryeriHub.Application.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace PazaryeriHub.Tests.Integration.Infrastructure;

/// <summary>
/// Api'yi bellek içinde ayağa kaldırır. Bağlantı dizesi dışarıdan verilir; geliştirme ayarları (otomatik
/// veritabanı oluşturma, tekrarlayan job zamanlamaları) testlerde kapatılır.
/// </summary>
/// <param name="overrides">Varsayılan test ayarlarının üzerine yazılacak değerler (ör. uçtan uca testte job zamanlamalarını açmak).</param>
public sealed class ApiFactory(string connectionString, IReadOnlyDictionary<string, string?>? overrides = null) : WebApplicationFactory<Program>
{
    /// <summary>Her rol için bir test kullanıcısı: test-yonetici, test-depo, test-muhasebe, test-destek.</summary>
    public static readonly IReadOnlyDictionary<string, string> UsersByRole = new Dictionary<string, string>
    {
        [Roles.Admin] = "test-yonetici",
        [Roles.Warehouse] = "test-depo",
        [Roles.Accounting] = "test-muhasebe",
        [Roles.Support] = "test-destek"
    };

    /// <summary>
    /// Test kullanıcılarının parolası: test süreci başına bir kez rastgele üretilir, hiçbir yere yazılmaz.
    /// Tüm ApiFactory örnekleri aynı parolayı kullanır; aksi halde her yeni örnek tohumlarken diğerinin parolasını ezer.
    /// </summary>
    private static readonly string SharedPassword = "Tst!" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));

    public string Password => SharedPassword;

    /// <summary>Verilen rolle giriş yapmış (çerezli) istemci.</summary>
    public async Task<HttpClient> CreateClientAsAsync(string role)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName = UsersByRole[role], password = Password });
        response.EnsureSuccessStatusCode();
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Program'ın servis kaydı sırasında (Build öncesi) okuduğu ayarlar (ör. Kanallar:{KOD}:Mod) ancak UseSetting ile erken görünür.
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PazaryeriHub"] = connectionString,
                ["Database:AutoCreate"] = "false",
                // Arka plan Hangfire sunucusu gerçek job'ları almasın: tekrarlayan job'lar "default" kuyruğuna düşer,
                // test sunucusu yalnızca boş bir kuyruğu dinler. Testler job'ları doğrudan çağırır.
                ["Hangfire:Queues:0"] = "testler-bos",
                ["Senkron:OutboxCron"] = "",
                ["Senkron:PushCron"] = "",
                ["Senkron:BatchCheckCron"] = "",
                ["Senkron:ReconciliationCron"] = "",
                ["Senkron:ReferenceCron"] = "",
                ["Senkron:CommandCron"] = "",
                ["Senkron:PricePushCron"] = "",
                ["Senkron:QuestionFetchCron"] = "",
                // Testlerde SiparisCek arka planda çalışmaz; testler job'ı doğrudan çağırır.
                ["Siparis:FetchCron"] = "",
                ["Siparis:ReturnFetchCron"] = "",
                ["Hangfire:SchedulePollingIntervalSeconds"] = "1",
                // Tohum test kullanıcıları (Testing ortamında parola her açılışta güncellenir)
                ["Kimlik:SeedUsers:0:UserName"] = UsersByRole[Roles.Admin], ["Kimlik:SeedUsers:0:Role"] = Roles.Admin, ["Kimlik:SeedUsers:0:Password"] = Password,
                ["Kimlik:SeedUsers:1:UserName"] = UsersByRole[Roles.Warehouse], ["Kimlik:SeedUsers:1:Role"] = Roles.Warehouse, ["Kimlik:SeedUsers:1:Password"] = Password,
                ["Kimlik:SeedUsers:2:UserName"] = UsersByRole[Roles.Accounting], ["Kimlik:SeedUsers:2:Role"] = Roles.Accounting, ["Kimlik:SeedUsers:2:Password"] = Password,
                ["Kimlik:SeedUsers:3:UserName"] = UsersByRole[Roles.Support], ["Kimlik:SeedUsers:3:Role"] = Roles.Support, ["Kimlik:SeedUsers:3:Password"] = Password,
                // Test logları kaynak ağacını kirletmesin diye geçici klasöre yazılır
                ["Serilog:WriteTo:1:Args:path"] = Path.Combine(Path.GetTempPath(), "pazaryerihub-tests", "log-.json")
            });
            if (overrides is not null)
            {
                config.AddInMemoryCollection(overrides);
            }
        });
    }
}
