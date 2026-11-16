using System.Net;
using System.Text.Json;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Health;

public sealed class HealthEndpointTests
{
    // Kapalı bir porta işaret eder: SQL Server'a ulaşılamama durumunu taklit eder.
    private const string UnreachableConnectionString =
        "Data Source=127.0.0.1,1;Initial Catalog=PazaryeriHub;User ID=x;Password=y;Encrypt=True;TrustServerCertificate=True;Connect Timeout=2";

    [Fact]
    public async Task Live_uc_noktasi_bagimliliklardan_bagimsiz_200_doner()
    {
        await using var factory = new ApiFactory(UnreachableConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Veritabanina_ulasilamazsa_health_503_doner_ve_gizli_bilgi_sizdirmaz()
    {
        await using var factory = new ApiFactory(UnreachableConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().NotContain("Password").And.NotContain("User ID");

        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("status").GetString().Should().Be("Unhealthy");
        json.RootElement.GetProperty("checks").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString())
            .Should().BeEquivalentTo(["sqlserver", "hangfire"]);
    }

    [Fact]
    public async Task Gercek_veritabaniyla_health_200_doner()
    {
        var connectionString = Environment.GetEnvironmentVariable(ApiFactory.TestDatabaseEnvVar);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Skip($"{ApiFactory.TestDatabaseEnvVar} ortam değişkeni tanımlı değil; gerçek SQL Server testi atlandı.");
        }

        await using var factory = new ApiFactory(connectionString);
        using var client = factory.CreateClient();

        // Hangfire sunucusunun ilk nabzını göndermesi birkaç saniye sürebilir.
        HttpResponseMessage response = null!;
        string body = "";
        for (var attempt = 0; attempt < 20; attempt++)
        {
            response = await client.GetAsync("/health", TestContext.Current.CancellationToken);
            body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                break;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        }

        response.StatusCode.Should().Be(HttpStatusCode.OK, because: body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("status").GetString().Should().Be("Healthy", because: body);
    }
}
