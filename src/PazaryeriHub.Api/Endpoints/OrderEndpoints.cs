using PazaryeriHub.Adapters.Mock;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Domain.Orders;
using PazaryeriHub.Infrastructure.BackgroundJobs;

namespace PazaryeriHub.Api.Endpoints;

public sealed record ChangeOrderStatusRequest(OrderStatus Status);

/// <summary>
/// Sipariş uç noktaları. // TODO (Aşama 5): yetkilendirme; Destek rolü için müşteri bilgisi maskelenmeli.
/// </summary>
public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app, IWebHostEnvironment environment)
    {
        var orders = app.MapGroup("/api/orders").WithTags("Siparişler");
        orders.MapGet("/", (IOrderService s, int? channelId, OrderStatus? status, DateTime? from, DateTime? to, string? sku,
                bool? insufficientStock, string? search, int? page, int? pageSize, CancellationToken ct) =>
            s.SearchAsync(new OrderQuery(channelId, status, from, to, sku, insufficientStock, search, page, pageSize), ct));
        orders.MapGet("/{id:long}", (IOrderService s, long id, CancellationToken ct) => s.GetAsync(id, ct));
        orders.MapPost("/{id:long}/status", (IOrderService s, long id, ChangeOrderStatusRequest request, CancellationToken ct) =>
            s.ChangeStatusAsync(id, request.Status, "PANEL", ct));

        // Yalnızca geliştirme/test: mock kanala sipariş yerleştirme ve SiparisCek'i hemen çalıştırma.
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            var dev = app.MapGroup("/api/dev").WithTags("Geliştirme");
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
}
