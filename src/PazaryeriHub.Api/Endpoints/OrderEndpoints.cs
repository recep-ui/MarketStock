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
        orders.MapGet("/{id:long}", async (IOrderService s, ClaimsPrincipal user, long id, CancellationToken ct) =>
            MaskFor(user, await s.GetAsync(id, ct)));
        orders.MapPost("/{id:long}/status", async (IOrderService s, ClaimsPrincipal user, long id, ChangeOrderStatusRequest request, CancellationToken ct) =>
            MaskFor(user, await s.ChangeStatusAsync(id, request.Status, "PANEL", ct)))
            .RequireAuthorization(Policies.OrderWrite);
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
