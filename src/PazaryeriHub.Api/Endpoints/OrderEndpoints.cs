using System.Security.Claims;
using PazaryeriHub.Adapters.Mock;
using PazaryeriHub.Api.Auth;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Domain.Orders;
using PazaryeriHub.Infrastructure.BackgroundJobs;

namespace PazaryeriHub.Api.Endpoints;

public sealed record ChangeOrderStatusRequest(OrderStatus Status);

/// <summary>
/// Sipariş uç noktaları. Yetki: okuma tüm roller; durum değişikliği ve iade depoya giriş Yönetici + Depo.
/// KVKK: Destek rolü müşteri bilgisini maskeli görür (API katmanında).
/// </summary>
public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app, IWebHostEnvironment environment)
    {
        var orders = app.MapGroup("/api/orders").WithTags("Siparişler").RequireAuthorization(Policies.Read);
        orders.MapGet("/", (IOrderService s, int? channelId, OrderStatus? status, DateTime? from, DateTime? to, string? sku,
                bool? insufficientStock, string? search, int? page, int? pageSize, CancellationToken ct) =>
            s.SearchAsync(new OrderQuery(channelId, status, from, to, sku, insufficientStock, search, page, pageSize), ct));
        // Sipariş listesi filtreleriyle satır bazlı Excel (ADR-034). Müşteri bilgisi içermez → tüm rollere açık.
        orders.MapGet("/export", async (PazaryeriHub.Application.Reports.ISalesReportService s, int? channelId, OrderStatus? status,
                DateTime? from, DateTime? to, string? sku, bool? insufficientStock, string? search, CancellationToken ct) =>
            Results.File(await s.ExportOrdersAsync(new OrderQuery(channelId, status, from, to, sku, insufficientStock, search), ct),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"siparisler-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx"));
        orders.MapGet("/{id:long}", async (IOrderService s, ClaimsPrincipal user, long id, CancellationToken ct) =>
            MaskFor(user, await s.GetAsync(id, ct)));
        orders.MapPost("/{id:long}/status", async (IOrderService s, IOrderNotificationService notifications, ClaimsPrincipal user, long id,
                ChangeOrderStatusRequest request, CancellationToken ct) =>
            {
                var detail = await s.ChangeStatusAsync(id, request.Status, "PANEL", ct);
                // OTOMATIK bildirim modundaki kanalda bildirim kendiliğinden kuyruğa alınır (ADR-026); ONAYLA_GONDER'de hiçbir şey olmaz.
                await notifications.OnPanelTransitionAsync(id, request.Status, user.UserName(), ct);
                return MaskFor(user, detail);
            })
            .RequireAuthorization(Policies.OrderWrite);

        // Kanal iade talepleri (ADR-027). Okuma tüm roller (müşteri verisi saklanmaz); depoya giriş Yönetici + Depo; karar ve çekme Yönetici.
        var returns = app.MapGroup("/api/returns").WithTags("İade talepleri").RequireAuthorization(Policies.Read);
        returns.MapGet("/", (IReturnService r, int? channelId, string? search, int? page, int? pageSize, CancellationToken ct) =>
            r.SearchAsync(channelId, search, page, pageSize, ct));
        returns.MapGet("/{id:long}", (IReturnService r, long id, CancellationToken ct) => r.GetAsync(id, ct));
        returns.MapPost("/{id:long}/lines/{lineId:long}/receive", (IReturnService r, ClaimsPrincipal user, long id, long lineId, CancellationToken ct) =>
            r.ReceiveLineAsync(id, lineId, user.UserName(), ct))
            .RequireAuthorization(Policies.StockWrite);
        returns.MapPost("/{id:long}/decision", (IReturnService r, ClaimsPrincipal user, long id, ReturnDecisionInput input, CancellationToken ct) =>
            r.DecideAsync(id, input, user.UserName(), ct))
            .RequireAuthorization(Policies.Admin);
        returns.MapPost("/fetch/{channelCode}", (Hangfire.IBackgroundJobClient jobs, string channelCode) =>
        {
            var code = channelCode.ToUpperInvariant();
            if (!ChannelCodes.All.Contains(code))
            {
                return Results.Problem(statusCode: 404, title: "Bilinmeyen kanal.", detail: $"'{channelCode}' geçersiz.");
            }
            var jobId = Hangfire.BackgroundJobClientExtensions.Enqueue<PazaryeriHub.Infrastructure.Orders.ReturnFetchJob>(jobs,
                job => job.RunAsync(code, false, CancellationToken.None));
            return Results.Accepted(value: new { jobId });
        }).RequireAuthorization(Policies.Admin);

        // Kanaldan sipariş çek (elle; Yönetici): SiparisCek job'ını kuyruğa alır. Yalnızca okuma — kanala yazmaz.
        // Kanaldaki iptaller de bu çekimle (İptal durumu olarak) gelir. Deneme modunda zamanlanmış çekim kapalı olduğundan tek yol budur.
        orders.MapPost("/fetch/{channelCode}", (Hangfire.IBackgroundJobClient jobs, string channelCode) =>
        {
            var code = channelCode.ToUpperInvariant();
            if (!ChannelCodes.All.Contains(code))
            {
                return Results.Problem(statusCode: 404, title: "Bilinmeyen kanal.", detail: $"'{channelCode}' geçersiz.");
            }
            var jobId = Hangfire.BackgroundJobClientExtensions.Enqueue<OrderFetchJob>(jobs, job => job.RunAsync(code, CancellationToken.None));
            return Results.Accepted(value: new { jobId });
        }).RequireAuthorization(Policies.Admin);

        // Kanala sipariş durumu / kargo bildirimi (ADR-026). Liste ve geçmiş herkese açık; bildirim Yönetici + Depo.
        orders.MapGet("/notifications", (IOrderNotificationService n, int? channelId, string? filter, string? search, int? page, int? pageSize, CancellationToken ct) =>
            n.SearchAsync(channelId, filter, search, page, pageSize, ct));
        orders.MapPost("/notifications", (IOrderNotificationService n, ClaimsPrincipal user, OrderNotifyRequest request, CancellationToken ct) =>
            n.NotifyAsync(request, user.UserName(), ct))
            .RequireAuthorization(Policies.OrderWrite);
        orders.MapGet("/{id:long}/notifications", (IOrderNotificationService n, long id, CancellationToken ct) => n.GetForOrderAsync(id, ct));
        orders.MapPost("/{id:long}/lines/{lineId:long}/return-receipt", async (IOrderService s, ClaimsPrincipal user, long id, long lineId, CancellationToken ct) =>
            MaskFor(user, await s.ReceiveReturnLineAsync(id, lineId, user.UserName(), ct)))
            .RequireAuthorization(Policies.StockWrite);

        // Yalnızca geliştirme/test: mock kanala sipariş yerleştirme ve SiparisCek'i hemen çalıştırma.
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            var dev = app.MapGroup("/api/dev").WithTags("Geliştirme").RequireAuthorization(Policies.Admin);
            dev.MapPost("/mock/{channelCode}/orders", (MockChannelStore store, string channelCode, NormalizedOrder order) =>
            {
                var code = channelCode.ToUpperInvariant();
                if (!ChannelCodes.All.Contains(code))
                {
                    return Results.Problem(statusCode: 404, title: "Bilinmeyen kanal.", detail: $"'{channelCode}' geçersiz.");
                }
                store.UpsertOrder(code, order);
                return Results.Accepted();
            });
            dev.MapPost("/jobs/order-fetch/{channelCode}", (OrderFetchJob job, string channelCode, CancellationToken ct) =>
                job.RunAsync(channelCode.ToUpperInvariant(), ct));
        }

        return app;
    }

    /// <summary>Destek rolü yalnızca maskeli müşteri bilgisi görür.</summary>
    private static OrderDetail MaskFor(ClaimsPrincipal user, OrderDetail detail) =>
        user.IsInRole(Roles.Support) ? detail with { Customer = CustomerMasking.Mask(detail.Customer) } : detail;
}
