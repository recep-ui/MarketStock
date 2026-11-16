using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace PazaryeriHub.Infrastructure.Persistence;

/// <summary>
/// Bağlantı dizesini yapılandırmadan okur. Değer, servis çözümlendiği anda (tembel) okunur;
/// böylece testlerde yapılandırma geçersiz kılınabilir.
/// </summary>
public sealed class SqlConnectionFactory(IConfiguration configuration)
{
    public string ConnectionString =>
        configuration.GetConnectionString(DatabaseOptions.ConnectionStringName) is { Length: > 0 } cs
            ? cs
            : throw new InvalidOperationException(
                $"'ConnectionStrings:{DatabaseOptions.ConnectionStringName}' tanımlı değil. " +
                "Yerelde 'dotnet user-secrets', konteynerde ortam değişkeni ile verin.");

    /// <summary>Hedef veritabanının adı (Initial Catalog).</summary>
    public string DatabaseName => new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

    public SqlConnection Create() => new(ConnectionString);

    /// <summary>Aynı sunucuda master veritabanına bağlantı (veritabanı oluşturma için).</summary>
    public SqlConnection CreateForMaster()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        return new SqlConnection(builder.ConnectionString);
    }
}
