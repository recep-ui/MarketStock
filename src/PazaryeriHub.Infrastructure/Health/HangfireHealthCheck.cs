using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PazaryeriHub.Infrastructure.Health;

/// <summary>
/// Hangfire depolamasına erişilebildiğini ve son 1 dakika içinde nabız (heartbeat) gönderen
/// en az bir Hangfire sunucusu olduğunu doğrular.
/// </summary>
public sealed class HangfireHealthCheck(IServiceProvider serviceProvider) : IHealthCheck
{
    private static readonly TimeSpan HeartbeatTolerance = TimeSpan.FromMinutes(1);

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var storage = serviceProvider.GetRequiredService<JobStorage>();
            var servers = storage.GetMonitoringApi().Servers();
            var alive = servers.Count(s => s.Heartbeat is { } hb && DateTime.UtcNow - hb < HeartbeatTolerance);

            var data = new Dictionary<string, object> { ["activeServers"] = alive };
            return Task.FromResult(alive > 0
                ? HealthCheckResult.Healthy("Hangfire çalışıyor.", data)
                : HealthCheckResult.Unhealthy("Aktif Hangfire sunucusu yok.", data: data));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy($"Hangfire depolamasına erişilemedi ({ex.GetType().Name})."));
        }
    }
}
