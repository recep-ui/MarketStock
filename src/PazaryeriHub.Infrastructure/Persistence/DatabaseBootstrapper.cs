using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PazaryeriHub.Infrastructure.Persistence;

/// <summary>
/// Geliştirme ortamında veritabanını hazırlar: yoksa oluşturur, READ_COMMITTED_SNAPSHOT ve
/// ALLOW_SNAPSHOT_ISOLATION ayarlarını açar. Şema (tablolar) burada değil, Aşama 1'de DbUp script'leriyle kurulur.
/// Hata uygulamayı düşürmez; loglanır ve /health durumu 503 olarak kalır.
/// </summary>
public sealed class DatabaseBootstrapper(
    SqlConnectionFactory connectionFactory,
    IOptions<DatabaseOptions> options,
    ILogger<DatabaseBootstrapper> logger)
{
    public async Task<bool> TryEnsureDatabaseAsync(CancellationToken ct)
    {
        if (!options.Value.AutoCreate)
        {
            logger.LogInformation("Veritabanı otomatik oluşturma kapalı (Database:AutoCreate=false).");
            return false;
        }

        string databaseName;
        try
        {
            databaseName = connectionFactory.DatabaseName;
        }
        catch (Exception ex)
        {
            logger.LogError("Veritabanı hazırlığı atlandı: {Neden}", ex.Message);
            return false;
        }

        if (string.IsNullOrWhiteSpace(databaseName) || databaseName.Equals("master", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError("Bağlantı dizesinde 'Initial Catalog' (veritabanı adı) belirtilmeli ve 'master' olmamalı.");
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.BootstrapTimeoutSeconds));

        try
        {
            await using var connection = connectionFactory.CreateForMaster();
            await connection.OpenAsync(timeout.Token);

            // Veritabanı adı parametre olarak geçilir; dinamik SQL içinde QUOTENAME ile korunur.
            const string sql = """
                DECLARE @q nvarchar(300) = QUOTENAME(@DbName);
                IF DB_ID(@DbName) IS NULL
                    EXEC (N'CREATE DATABASE ' + @q + N' COLLATE Turkish_CI_AS;');
                IF EXISTS (SELECT 1 FROM sys.databases WHERE name = @DbName AND is_read_committed_snapshot_on = 0)
                    EXEC (N'ALTER DATABASE ' + @q + N' SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;');
                IF EXISTS (SELECT 1 FROM sys.databases WHERE name = @DbName AND snapshot_isolation_state = 0)
                    EXEC (N'ALTER DATABASE ' + @q + N' SET ALLOW_SNAPSHOT_ISOLATION ON;');
                """;

            await using var command = new SqlCommand(sql, connection) { CommandTimeout = options.Value.BootstrapTimeoutSeconds };
            command.Parameters.Add(new SqlParameter("@DbName", System.Data.SqlDbType.NVarChar, 128) { Value = databaseName });
            await command.ExecuteNonQueryAsync(timeout.Token);

            logger.LogInformation("Veritabanı hazır: {Veritabani} (RCSI ve snapshot isolation açık).", databaseName);
            return true;
        }
        catch (SqlException ex) when (ex.Number is 262 or 229 or 297 or 5011 or 15247)
        {
            logger.LogError(
                "Veritabanı {Veritabani} oluşturulamadı / ayarlanamadı: yetki yetersiz (SQL hata {HataNo}). " +
                "Bir sysadmin 'db/provision' altındaki script'leri çalıştırmalı veya kullanıcıya dbcreator rolü vermeli.",
                databaseName, ex.Number);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Veritabanı {Veritabani} hazırlanırken hata oluştu.", databaseName);
            return false;
        }
    }
}
