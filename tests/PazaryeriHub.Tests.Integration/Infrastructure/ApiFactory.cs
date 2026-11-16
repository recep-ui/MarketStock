using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace PazaryeriHub.Tests.Integration.Infrastructure;

/// <summary>
/// Api'yi bellek içinde ayağa kaldırır. Bağlantı dizesi dışarıdan verilir; geliştirme ayarları (otomatik
/// veritabanı oluşturma, heartbeat job'ı) testlerde kapatılır.
/// </summary>
public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    /// <summary>Gerçek SQL Server testleri için bağlantı dizesini taşıyan ortam değişkeni.</summary>
    public const string TestDatabaseEnvVar = "PAZARYERIHUB_TEST_DB";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PazaryeriHub"] = connectionString,
                ["Database:AutoCreate"] = "false",
                ["Hangfire:HeartbeatCron"] = "",
                ["Hangfire:SchedulePollingIntervalSeconds"] = "1",
                // Test logları kaynak ağacını kirletmesin diye geçici klasöre yazılır
                ["Serilog:WriteTo:1:Args:path"] = Path.Combine(Path.GetTempPath(), "pazaryerihub-tests", "log-.json")
            });
        });
    }
}
