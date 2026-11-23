using Hangfire;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Application.Sync;
using PazaryeriHub.Infrastructure.Sync;

namespace PazaryeriHub.Api.Endpoints;

/// <summary>
/// Senkron Merkezi ve mutabakat uç noktaları. Dış (kanal) çağrısı gerektiren işlemler doğrudan yapılmaz,
/// Hangfire job'ı olarak kuyruğa alınır (ilke 5). Yetki: okuma tüm roller; yeniden dene / yenile / mutabakat Yönetici.
/// </summary>
public static class SyncEndpoints
{
    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var sync = app.MapGroup("/api/sync").WithTags("Senkron").RequireAuthorization(Policies.Read);
        sync.MapGet("/summary", (ISyncService s, CancellationToken ct) => s.GetSummaryAsync(ct));
        sync.MapGet("/queue", (ISyncService s, int? channelId, string? status, int? page, int? pageSize, CancellationToken ct) =>
            s.SearchQueueAsync(channelId, status, page, pageSize, ct));
        sync.MapPost("/queue/{id:long}/retry", (ISyncService s, long id, CancellationToken ct) => s.RetryAsync(id, ct))
            .RequireAuthorization(Policies.Admin);
        sync.MapPost("/channels/{channelId:int}/refresh", async (ISyncService s, int channelId, CancellationToken ct) =>
        {
            await s.RequestChannelRefreshAsync(channelId, ct);
            return Results.Accepted();
        }).RequireAuthorization(Policies.Admin);

        // Sapmayı kanala yaz (ADR-024): defterdeki değer seçilen ilanlara zorla yeniden gönderilir (mevcut hat).
        sync.MapPost("/reconciliation/push", (ISyncService s, PushListingsRequest request, CancellationToken ct) =>
            s.PushListingsAsync(request.ListingIds ?? [], ct)).RequireAuthorization(Policies.Admin);

        sync.MapGet("/reconciliation", (ISyncService s, int? channelId, bool? onlyDeviations, CancellationToken ct) =>
            s.GetReconciliationAsync(channelId, onlyDeviations ?? false, ct));

        // Stok gönderim onayı (ADR-048): "Onaylı" kanalda hiçbir stok kaydı Yönetici onayı olmadan kanala gitmez.
        sync.MapGet("/stock-approvals", (ISyncService s, int? channelId, CancellationToken ct) => s.GetStockApprovalsAsync(channelId, ct));
        sync.MapPost("/stock-approvals/approve", (ISyncService s, System.Security.Claims.ClaimsPrincipal user, StockApprovalRequest request, CancellationToken ct) =>
            s.ApproveStockAsync(request.Items ?? [], user.Identity?.Name, ct)).RequireAuthorization(Policies.Admin);
        sync.MapPost("/stock-approvals/reject", async (ISyncService s, System.Security.Claims.ClaimsPrincipal user, StockRejectRequest request, CancellationToken ct) =>
            Results.Ok(new { rejected = await s.RejectStockAsync(request.QueueIds ?? [], user.Identity?.Name, ct) }))
            .RequireAuthorization(Policies.Admin);
        sync.MapPost("/reconciliation/{channelCode}/run", async (IBackgroundJobClient jobs, IChannelService channels, string channelCode, CancellationToken ct) =>
        {
            var code = channelCode.ToUpperInvariant();
            if (!ChannelCodes.All.Contains(code))
            {
                return Results.Problem(statusCode: 404, title: "Bilinmeyen kanal.", detail: $"'{channelCode}' geçersiz.");
            }
            // Pasif kanalda job hiçbir şey yapmadan çıkar; kullanıcı "çekiliyor" sanmasın diye istek baştan reddedilir.
            var channel = (await channels.GetChannelsAsync(ct)).FirstOrDefault(c => c.Code == code);
            if (channel is { Active: false })
            {
                return Results.Problem(statusCode: 409, title: "Kanal pasif.",
                    detail: $"{channel.Name} kanalı pasif; mutabakat çalıştırılamaz. Ayarlar → Kanallar'dan kanalı aktif edin.");
            }
            var jobId = jobs.Enqueue<ReconciliationJob>(job => job.RunAsync(code, CancellationToken.None));
            return Results.Accepted(value: new { jobId });
        }).RequireAuthorization(Policies.Admin);

        return app;
    }
}
