using Microsoft.AspNetCore.Hosting;
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
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
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
                // Testlerde SiparisCek arka planda çalışmaz; testler job'ı doğrudan çağırır.
                ["Siparis:FetchCron"] = "",
                ["Hangfire:SchedulePollingIntervalSeconds"] = "1",
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
