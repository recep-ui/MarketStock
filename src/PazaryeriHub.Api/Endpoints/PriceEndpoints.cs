using System.Security.Claims;
using PazaryeriHub.Api.Auth;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.Pricing;

namespace PazaryeriHub.Api.Endpoints;

/// <summary>
/// Fiyat uç noktaları (ADR-028). Bu uç noktalar kanala çağrı YAPMAZ: değişiklikler outbox → FIYAT kuyruğu → FiyatGonder job'ı
/// yoluyla, FIYAT yeteneği açıksa gider. Yetki: okuma tüm roller; fiyat, kural, ilan fiyatı, eşik ve onay kararları Yönetici.
/// </summary>
public static class PriceEndpoints
{
    public static IEndpointRouteBuilder MapPriceEndpoints(this IEndpointRouteBuilder app)
    {
        var prices = app.MapGroup("/api/prices").WithTags("Fiyat").RequireAuthorization(Policies.Read);

        prices.MapGet("/skus/{skuId:int}", (IPriceService s, int skuId, CancellationToken ct) => s.GetSkuPriceAsync(skuId, ct));
        prices.MapPut("/skus/{skuId:int}", (IPriceService s, ClaimsPrincipal user, int skuId, SkuPriceInput input, CancellationToken ct) =>
            s.SetSkuPriceAsync(skuId, input, user.UserName(), ct)).RequireAuthorization(Policies.Admin);

        prices.MapGet("/channels", (IPriceService s, CancellationToken ct) => s.GetChannelsAsync(ct));
        prices.MapPut("/channels/{channelId:int}/rule", (IPriceService s, ClaimsPrincipal user, int channelId, ChannelPriceRuleDto rule, CancellationToken ct) =>
            s.SetChannelRuleAsync(channelId, rule, user.UserName(), ct)).RequireAuthorization(Policies.Admin);
        prices.MapGet("/channels/{channelId:int}/preview", (IPriceService s, int channelId, bool? onlyChanges, CancellationToken ct) =>
            s.GetPreviewAsync(channelId, onlyChanges ?? false, ct));

        // Ürün bazında kanal fiyatları (ADR-049): aynı SKU'nun her kanaldaki fiyatı yan yana; kanal bazında ayrı elle fiyat.
        prices.MapGet("/skus", (IPriceService s, string? search, int? page, int? pageSize, CancellationToken ct) =>
            s.SearchSkuPricesAsync(search, page, pageSize, ct));
        prices.MapPut("/listings/overrides", (IPriceService s, ClaimsPrincipal user, ListingOverridesRequest request, CancellationToken ct) =>
            s.SetListingOverridesAsync(request.Changes ?? [], user.UserName(), ct)).RequireAuthorization(Policies.Admin);

        // ADR-061: aynı ürünün bir kanaldaki varyantlarına (beden/renk) tek fiyat; mevcut ilan fiyatı yolu, yetenek ve onay kuralları geçerli.
        prices.MapPut("/products/{productId:int}/channels/{channelId:int}/variant-price", (IPriceService s, ClaimsPrincipal user, int productId,
                int channelId, VariantPriceRequest request, CancellationToken ct) =>
            s.SetProductVariantPriceAsync(productId, channelId, request, user.UserName(), ct)).RequireAuthorization(Policies.Admin);

        prices.MapPut("/listings/{listingId:int}/override", (IPriceService s, ClaimsPrincipal user, int listingId, ListingPriceOverrideInput input, CancellationToken ct) =>
            s.SetListingOverrideAsync(listingId, input, user.UserName(), ct)).RequireAuthorization(Policies.Admin);

        prices.MapGet("/approvals", (IPriceService s, string? state, int? channelId, int? page, int? pageSize, CancellationToken ct) =>
            s.SearchApprovalsAsync(state, channelId, page, pageSize, ct));
        prices.MapPost("/approvals/{id:long}/approve", (IPriceService s, ClaimsPrincipal user, long id, CancellationToken ct) =>
            s.ApproveAsync(id, user.UserName(), ct)).RequireAuthorization(Policies.Admin);
        prices.MapPost("/approvals/{id:long}/reject", (IPriceService s, ClaimsPrincipal user, long id, CancellationToken ct) =>
            s.RejectAsync(id, user.UserName(), ct)).RequireAuthorization(Policies.Admin);

        prices.MapGet("/history", (IPriceService s, int? skuId, int? listingId, int? channelId, int? page, int? pageSize, CancellationToken ct) =>
            s.GetHistoryAsync(skuId, listingId, channelId, page, pageSize, ct));

        prices.MapGet("/settings", (IPriceService s, CancellationToken ct) => s.GetSettingsAsync(ct));
        prices.MapPut("/settings", (IPriceService s, ClaimsPrincipal user, PriceSettingsDto settings, CancellationToken ct) =>
            s.SetSettingsAsync(settings, user.UserName(), ct)).RequireAuthorization(Policies.Admin);

        return app;
    }
}
