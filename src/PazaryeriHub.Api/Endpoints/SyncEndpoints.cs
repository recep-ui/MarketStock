using Hangfire;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Application.Sync;
using PazaryeriHub.Infrastructure.Sync;

namespace PazaryeriHub.Api.Endpoints;

/// <summary>
/// Senkron Merkezi ve mutabakat uç noktaları. Dış (kanal) çağrısı gerektiren işlemler doğrudan yapılmaz,
/// Hangfire job'ı olarak kuyruğa alınır (ilke 5). // TODO (Aşama 5): yetkilendirme.
/// </summary>
public static class SyncEndpoints
{
    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var sync = app.MapGroup("/api/sync").WithTags("Senkron");
        sync.MapGet("/summary", (ISyncService s, CancellationToken ct) => s.GetSummaryAsync(ct));
        sync.MapGet("/queue", (ISyncService s, int? channelId, string? status, int? page, int? pageSize, CancellationToken ct) =>
            s.SearchQueueAsync(channelId, status, page, pageSize, ct));
        sync.MapPost("/queue/{id:long}/retry", (ISyncService s, long id, CancellationToken ct) => s.RetryAsync(id, ct));
        sync.MapPost("/channels/{channelId:int}/refresh", async (ISyncService s, int channelId, CancellationToken ct) =>
        {
            await s.RequestChannelRefreshAsync(channelId, ct);
            return Results.Accepted();
        });

        sync.MapGet("/reconciliation", (ISyncService s, int? channelId, bool? onlyDeviations, CancellationToken ct) =>
            s.GetReconciliationAsync(channelId, onlyDeviations ?? false, ct));
        sync.MapPost("/reconciliation/{channelCode}/run", (IBackgroundJobClient jobs, string channelCode) =>
        {
            var code = channelCode.ToUpperInvariant();
            if (!ChannelCodes.All.Contains(code))
            {
                return Results.Problem(statusCode: 404, title: "Bilinmeyen kanal.", detail: $"'{channelCode}' geçersiz.");
            }
            var jobId = jobs.Enqueue<ReconciliationJob>(job => job.RunAsync(code, CancellationToken.None));
            return Results.Accepted(value: new { jobId });
        });

        return app;
    }
}
