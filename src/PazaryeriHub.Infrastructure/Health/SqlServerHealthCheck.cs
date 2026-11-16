using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.Health;

/// <summary>
/// SQL Server erişimini ve zorunlu veritabanı ayarlarını (RCSI, snapshot isolation) doğrular.
/// Bağlantı yoksa Unhealthy (503); bağlantı var ama ayarlar kapalıysa Degraded döner.
/// </summary>
public sealed class SqlServerHealthCheck(SqlConnectionFactory connectionFactory) : IHealthCheck
{
    private const string Sql = """
        SELECT CAST(is_read_committed_snapshot_on AS bit),
               CAST(CASE WHEN snapshot_isolation_state = 1 THEN 1 ELSE 0 END AS bit),
               CAST(SERVERPROPERTY('ProductMajorVersion') AS int)
        FROM sys.databases WHERE name = DB_NAME();
        """;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = connectionFactory.Create();
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(Sql, connection) { CommandTimeout = 5 };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("Veritabanı bilgisi okunamadı.");
            }

            var rcsi = reader.GetBoolean(0);
            var snapshot = reader.GetBoolean(1);
            var data = new Dictionary<string, object>
            {
                ["readCommittedSnapshot"] = rcsi,
                ["allowSnapshotIsolation"] = snapshot,
                ["sqlServerMajorVersion"] = reader.IsDBNull(2) ? 0 : reader.GetInt32(2)
            };

            return rcsi && snapshot
                ? HealthCheckResult.Healthy("SQL Server erişilebilir.", data)
                : HealthCheckResult.Degraded("SQL Server erişilebilir ancak READ_COMMITTED_SNAPSHOT / ALLOW_SNAPSHOT_ISOLATION kapalı.", data: data);
        }
        catch (SqlException ex)
        {
            // Bağlantı dizesi veya gizli bilgi yanıta yazılmaz; yalnızca SQL hata numarası.
            return HealthCheckResult.Unhealthy($"SQL Server'a bağlanılamadı (SQL hata {ex.Number}).");
        }
        catch (OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("SQL Server kontrolü zaman aşımına uğradı.");
        }
        catch (InvalidOperationException ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message);
        }
    }
}
