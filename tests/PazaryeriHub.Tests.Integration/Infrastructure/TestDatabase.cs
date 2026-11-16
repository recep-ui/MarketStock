using Dapper;
using DotNet.Testcontainers.Builders;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PazaryeriHub.Application.Stock;
using PazaryeriHub.Infrastructure.Database;
using PazaryeriHub.Infrastructure.Persistence;
using PazaryeriHub.Infrastructure.Stock;
using Testcontainers.MsSql;

namespace PazaryeriHub.Tests.Integration.Infrastructure;

/// <summary>
/// Gerçek SQL Server'a karşı çalışan testler için paylaşılan veritabanı. Bağlantı şu sırayla aranır:
/// <list type="number">
/// <item><c>PAZARYERIHUB_TEST_DB</c> ortam değişkeni</item>
/// <item>Test projesinin user-secrets'ı: <c>ConnectionStrings:PazaryeriHubTest</c></item>
/// <item>Testcontainers ile MSSQL konteyneri (Docker çalışıyorsa)</item>
/// </list>
/// Hiçbiri yoksa testler ATLANIR (geçti sayılmaz). Güvenlik: veritabanı adı "Test" içermiyorsa testler çalışmaz;
/// testler veri ekler ve gerçek veritabanını kirletmemelidir.
/// Her test kendi benzersiz SKU'sunu oluşturduğu için testler arasında temizlik gerekmez.
/// </summary>
public sealed class TestDatabase : IAsyncLifetime
{
    public const string EnvVar = "PAZARYERIHUB_TEST_DB";
    public const string UserSecretsKey = "ConnectionStrings:PazaryeriHubTest";
    private const string ContainerImage = "mcr.microsoft.com/mssql/server:2025-latest";

    private MsSqlContainer? _container;

    public string? ConnectionString { get; private set; }
    public string? SkipReason { get; private set; }
    public string Source { get; private set; } = "";
    public int DefaultWarehouseId { get; private set; }

    /// <summary>İlk migration çalıştırmasında yürütülen script'ler (yeniden çalıştırma testi için).</summary>
    public IReadOnlyList<string> InitialMigrationScripts { get; private set; } = [];

    public IStockLedger Ledger { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var configured = ResolveConfiguredConnectionString(out var source);
        if (configured is not null)
        {
            ConnectionString = configured;
            Source = source;
        }
        else
        {
            ConnectionString = await TryStartContainerAsync();
            if (ConnectionString is null)
            {
                return; // SkipReason ayarlandı
            }
            Source = "Testcontainers";
        }

        var databaseName = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;
        if (!databaseName.Contains("Test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Entegrasyon testleri yalnızca adında 'Test' geçen bir veritabanında çalışır (verilen: '{databaseName}').");
        }

        InitialMigrationScripts = new DatabaseMigrator(NullLogger<DatabaseMigrator>.Instance).Migrate(ConnectionString);

        await using var connection = Open();
        DefaultWarehouseId = await connection.ExecuteScalarAsync<int>("SELECT Id FROM dbo.Warehouse WHERE Ad = N'Ana Depo'");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:PazaryeriHub"] = ConnectionString })
            .Build();
        Ledger = new SqlStockLedger(new SqlConnectionFactory(config));
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Veritabanı yoksa testi gerekçesiyle atlar.</summary>
    public void SkipIfUnavailable()
    {
        if (SkipReason is not null)
        {
            Assert.Skip(SkipReason);
        }
    }

    public SqlConnection Open()
    {
        var connection = new SqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>Bu teste özel ürün + SKU oluşturur; SKU Id'sini döndürür.</summary>
    public async Task<int> CreateSkuAsync(string? barcode = null)
    {
        await using var connection = Open();
        return await connection.ExecuteScalarAsync<int>("""
            DECLARE @p INT;
            INSERT dbo.Product (Ad, Marka) VALUES (N'Test ürünü', N'Test');
            SET @p = SCOPE_IDENTITY();
            INSERT dbo.Sku (ProductId, SkuKodu, Barkod) VALUES (@p, @SkuKodu, @Barkod);
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """, new { SkuKodu = "T-" + Guid.NewGuid().ToString("N"), Barkod = barcode });
    }

    public async Task<BalanceRow?> GetBalanceAsync(int skuId)
    {
        await using var connection = Open();
        return await connection.QuerySingleOrDefaultAsync<BalanceRow>(
            "SELECT Fiziksel, Rezerve, GuvenlikStogu, Satilabilir FROM dbo.StockBalance WHERE SkuId = @skuId AND WarehouseId = @wh",
            new { skuId, wh = DefaultWarehouseId });
    }

    public async Task<int> CountMovementsAsync(int skuId, string? tip = null)
    {
        await using var connection = Open();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.StockMovement WHERE SkuId = @skuId AND (@tip IS NULL OR Tip = @tip)",
            new { skuId, tip });
    }

    public async Task<int> CountOutboxAsync(int skuId, string? tip = null)
    {
        await using var connection = Open();
        return await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.Outbox
            WHERE OlayTipi = 'StokDegisti'
              AND CAST(JSON_VALUE(Payload, '$.SkuId') AS INT) = @skuId
              AND (@tip IS NULL OR JSON_VALUE(Payload, '$.Tip') = @tip)
            """, new { skuId, tip });
    }

    private static string? ResolveConfiguredConnectionString(out string source)
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            source = "ortam değişkeni";
            return fromEnv;
        }

        var config = new ConfigurationBuilder().AddUserSecrets(typeof(TestDatabase).Assembly, optional: true).Build();
        var fromSecrets = config[UserSecretsKey];
        source = "user-secrets";
        return string.IsNullOrWhiteSpace(fromSecrets) ? null : fromSecrets;
    }

    private async Task<string?> TryStartContainerAsync()
    {
        try
        {
            _container = new MsSqlBuilder(ContainerImage).Build();
            await _container.StartAsync();
        }
        catch (Exception ex) when (ex is DockerUnavailableException or InvalidOperationException or TimeoutException)
        {
            SkipReason = $"Test veritabanı yok: {EnvVar} / user-secrets '{UserSecretsKey}' tanımlı değil ve Docker kullanılamıyor ({ex.GetType().Name}).";
            return null;
        }

        // Konteynerde sa (sysadmin) ile test veritabanı oluşturulur ve zorunlu izolasyon ayarları açılır.
        var master = _container.GetConnectionString();
        await using (var connection = new SqlConnection(master))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("""
                IF DB_ID(N'PazaryeriHub_Test') IS NULL CREATE DATABASE PazaryeriHub_Test COLLATE Turkish_CI_AS;
                ALTER DATABASE PazaryeriHub_Test SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
                ALTER DATABASE PazaryeriHub_Test SET ALLOW_SNAPSHOT_ISOLATION ON;
                ALTER DATABASE PazaryeriHub_Test SET AUTO_CLOSE OFF;
                """);
        }

        return new SqlConnectionStringBuilder(master) { InitialCatalog = "PazaryeriHub_Test" }.ConnectionString;
    }
}

public sealed record BalanceRow(int Fiziksel, int Rezerve, int GuvenlikStogu, int Satilabilir);

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<TestDatabase>
{
    public const string Name = "Veritabani";
}
