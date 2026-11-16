using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Excel;

namespace PazaryeriHub.Api.Endpoints;

/// <summary>
/// Kanal, eşleştirme ve Excel uç noktaları.
/// // TODO (Aşama 5): yetkilendirme. Kanal kimlik bilgisi girişi (KimlikSifreli + IDataProtector) Aşama 6'da.
/// </summary>
public static class ChannelEndpoints
{
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static IEndpointRouteBuilder MapChannelEndpoints(this IEndpointRouteBuilder app)
    {
        var channels = app.MapGroup("/api/channels").WithTags("Kanallar");
        channels.MapGet("/", (IChannelService s, CancellationToken ct) => s.GetChannelsAsync(ct));
        channels.MapPut("/{id:int}", (IChannelService s, int id, ChannelUpdate update, CancellationToken ct) => s.UpdateChannelAsync(id, update, ct));

        var listings = app.MapGroup("/api/listings").WithTags("Eşleştirme");
        listings.MapGet("/", (IChannelService s, int? channelId, string? search, int? page, int? pageSize, CancellationToken ct) =>
            s.SearchListingsAsync(channelId, search, page, pageSize, ct));
        listings.MapPut("/", (IChannelService s, ListingInput input, CancellationToken ct) => s.UpsertListingAsync(input, ct));
        listings.MapDelete("/{id:int}", async (IChannelService s, int id, CancellationToken ct) =>
        {
            await s.DeleteListingAsync(id, ct);
            return Results.NoContent();
        });

        var excel = app.MapGroup("/api/excel/{kind}").WithTags("Excel");
        excel.MapGet("/template", (IExcelTransferService s, string kind) =>
            TryParseKind(kind, out var k)
                ? Results.File(s.CreateTemplate(k), XlsxContentType, $"sablon-{kind}.xlsx")
                : UnknownKind(kind));
        excel.MapGet("/export", async (IExcelTransferService s, string kind, CancellationToken ct) =>
            TryParseKind(kind, out var k)
                ? Results.File(await s.ExportAsync(k, ct), XlsxContentType, $"{kind}-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx")
                : UnknownKind(kind));
        excel.MapPost("/import", async (IExcelTransferService s, string kind, IFormFile? file, bool? validateOnly, CancellationToken ct) =>
        {
            if (!TryParseKind(kind, out var k))
            {
                return UnknownKind(kind);
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
            var report = await s.ImportAsync(k, stream, file.FileName, validateOnly ?? false, user: null, ct);
            return report.FileErrors.Count > 0
                ? Results.UnprocessableEntity(report)
                : Results.Ok(report);
        }).DisableAntiforgery();

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
