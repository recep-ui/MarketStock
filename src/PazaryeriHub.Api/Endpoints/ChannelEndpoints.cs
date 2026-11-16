using System.Security.Claims;
using PazaryeriHub.Api.Auth;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Excel;

namespace PazaryeriHub.Api.Endpoints;

/// <summary>
/// Kanal, eşleştirme ve Excel uç noktaları.
/// Yetki: okuma tüm roller; kanal ayarı, eşleştirme ve ürün içe aktarma Yönetici; stok içe aktarma Yönetici + Depo.
/// Kanal kimlik bilgisi girişi (KimlikSifreli + IDataProtector) Aşama 6'da.
/// </summary>
public static class ChannelEndpoints
{
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static IEndpointRouteBuilder MapChannelEndpoints(this IEndpointRouteBuilder app)
    {
        var channels = app.MapGroup("/api/channels").WithTags("Kanallar").RequireAuthorization(Policies.Read);
        channels.MapGet("/", (IChannelService s, CancellationToken ct) => s.GetChannelsAsync(ct));
        channels.MapPut("/{id:int}", (IChannelService s, int id, ChannelUpdate update, CancellationToken ct) => s.UpdateChannelAsync(id, update, ct))
            .RequireAuthorization(Policies.Admin);

        // Kanal API kimlik bilgileri: yalnızca yazılır/silinir, hiçbir yanıtta geri dönmez (ChannelDto.HasCredentials).
        channels.MapPut("/{id:int}/credentials", async (IChannelService s, IChannelCredentialStore store, int id, System.Text.Json.JsonElement body, CancellationToken ct) =>
        {
            var channel = (await s.GetChannelsAsync(ct)).FirstOrDefault(c => c.Id == id);
            if (channel is null)
            {
                return Results.Problem(statusCode: 404, title: "Kayıt bulunamadı.", detail: $"Kanal bulunamadı (Id={id}).");
            }
            var normalized = channel.Code switch
            {
                PazaryeriHub.Adapters.Trendyol.TrendyolAdapter.Code => PazaryeriHub.Adapters.Trendyol.TrendyolCredentials.Parse(body.GetRawText()).ToJson(),
                _ => null
            };
            if (normalized is null)
            {
                return Results.Problem(statusCode: 400, title: "Desteklenmiyor.", detail: $"{channel.Name} için gerçek adaptör henüz yok; API bilgisi girilemez.");
            }
            await store.SaveJsonAsync(channel.Code, normalized, ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);
        channels.MapDelete("/{id:int}/credentials", async (IChannelService s, IChannelCredentialStore store, int id, CancellationToken ct) =>
        {
            var channel = (await s.GetChannelsAsync(ct)).FirstOrDefault(c => c.Id == id);
            if (channel is null)
            {
                return Results.Problem(statusCode: 404, title: "Kayıt bulunamadı.", detail: $"Kanal bulunamadı (Id={id}).");
            }
            await store.ClearAsync(channel.Code, ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);

        var listings = app.MapGroup("/api/listings").WithTags("Eşleştirme").RequireAuthorization(Policies.Read);
        listings.MapGet("/", (IChannelService s, int? channelId, string? search, int? page, int? pageSize, CancellationToken ct) =>
            s.SearchListingsAsync(channelId, search, page, pageSize, ct));
        listings.MapPut("/", (IChannelService s, ListingInput input, CancellationToken ct) => s.UpsertListingAsync(input, ct))
            .RequireAuthorization(Policies.CatalogWrite);
        listings.MapDelete("/{id:int}", async (IChannelService s, int id, CancellationToken ct) =>
        {
            await s.DeleteListingAsync(id, ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.CatalogWrite);

        var excel = app.MapGroup("/api/excel/{kind}").WithTags("Excel").RequireAuthorization(Policies.Read);
        excel.MapGet("/template", (IExcelTransferService s, string kind) =>
            TryParseKind(kind, out var k)
                ? Results.File(s.CreateTemplate(k), XlsxContentType, $"sablon-{kind}.xlsx")
                : UnknownKind(kind));
        excel.MapGet("/export", async (IExcelTransferService s, string kind, CancellationToken ct) =>
            TryParseKind(kind, out var k)
                ? Results.File(await s.ExportAsync(k, ct), XlsxContentType, $"{kind}-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx")
                : UnknownKind(kind));
        excel.MapPost("/import", async (IExcelTransferService s, ClaimsPrincipal user, string kind, IFormFile? file, bool? validateOnly, CancellationToken ct) =>
        {
            if (!TryParseKind(kind, out var k))
            {
                return UnknownKind(kind);
            }
            if (k != ExcelKind.Stock && !user.IsInRole(Roles.Admin))
            {
                return Results.Problem(statusCode: 403, title: "Yetkiniz yok.", detail: "Ürün ve eşleştirme içe aktarma yalnızca Yönetici rolüne açıktır.");
            }
            if (file is null || file.Length == 0)
            {
                return Results.Problem(statusCode: 400, title: "Dosya yok.", detail: "'file' alanında bir .xlsx dosyası gönderin.");
            }
            if (file.Length > ExcelLayouts.MaxFileBytes)
            {
                return Results.Problem(statusCode: 413, title: "Dosya çok büyük.", detail: $"En fazla {ExcelLayouts.MaxFileBytes / 1024 / 1024} MB.");
            }
            if (!Path.GetExtension(file.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem(statusCode: 400, title: "Desteklenmeyen dosya.", detail: "Yalnızca .xlsx dosyaları kabul edilir.");
            }

            await using var stream = file.OpenReadStream();
            var report = await s.ImportAsync(k, stream, file.FileName, validateOnly ?? false, user.UserName(), ct);
            return report.FileErrors.Count > 0
                ? Results.UnprocessableEntity(report)
                : Results.Ok(report);
        }).DisableAntiforgery().RequireAuthorization(Policies.StockWrite);

        return app;
    }

    private static bool TryParseKind(string value, out ExcelKind kind)
    {
        switch (value.ToLowerInvariant())
        {
            case "products": kind = ExcelKind.Products; return true;
            case "stock": kind = ExcelKind.Stock; return true;
            case "listings": kind = ExcelKind.Listings; return true;
            default: kind = default; return false;
        }
    }

    private static IResult UnknownKind(string kind) =>
        Results.Problem(statusCode: 404, title: "Bilinmeyen tür.", detail: $"'{kind}' geçersiz; products, stock veya listings olmalı.");
}
