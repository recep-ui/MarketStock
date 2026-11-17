using System.Security.Claims;
using PazaryeriHub.Api.Auth;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.ChannelCatalog;
using PazaryeriHub.Application.Excel;
using Hangfire;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Infrastructure.ChannelCatalog;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Api.Endpoints;

/// <summary>
/// Kanal kataloğu içe alma (ADR-020): kanal ürün Excel'i yükleme (önizleme/uygulama) ve eşleşmeyen kanal ürünleri.
/// Yetki: içe alma Yönetici; eşleşmeyenleri görme tüm roller; bağlama / yok sayma Yönetici (katalog yazma).
/// </summary>
public static class ChannelCatalogEndpoints
{
    public static IEndpointRouteBuilder MapChannelCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/channels/{id:int}/catalog/import", async (IChannelCatalogService s, ClaimsPrincipal user, int id, IFormFile? file,
                bool? validateOnly, bool? createMissing, CancellationToken ct) =>
            {
                if (file is null || file.Length == 0)
                {
                    return Results.Problem(statusCode: 400, title: "Dosya yok.", detail: "'file' alanında kanal panelinden indirilen .xlsx dosyasını gönderin.");
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
                // Varsayılan önizlemedir: yazmak için validateOnly=false açıkça gönderilmeli.
                var report = await s.ImportFileAsync(id, stream, file.FileName,
                    new CatalogImportOptions(validateOnly ?? true, createMissing ?? false), user.UserName(), ct);
                return report.FileErrors.Count > 0 ? Results.UnprocessableEntity(report) : Results.Ok(report);
            })
            .WithTags("Kanal kataloğu").DisableAntiforgery().RequireAuthorization(Policies.Admin);

        // B) Kanal API'sinden katalog çekme (ADR-022): Hangfire job'ı (dış çağrılar yalnızca job'dan, ilke 5).
        app.MapPost("/api/channels/{id:int}/catalog/fetch", async (IChannelService channels, IBackgroundJobClient jobs, SqlConnectionFactory connections,
                TimeProvider clock, ClaimsPrincipal user, int id, bool? validateOnly, bool? createMissing, CancellationToken ct) =>
            {
                if ((await channels.GetChannelsAsync(ct)).All(c => c.Id != id))
                {
                    return Results.Problem(statusCode: 404, title: "Kayıt bulunamadı.", detail: $"Kanal bulunamadı (Id={id}).");
                }
                if (!await CatalogFetchJob.TryMarkQueuedAsync(connections, clock, id, ct))
                {
                    return Results.Problem(statusCode: 409, title: "Zaten çalışıyor.", detail: "Bu kanal için katalog çekme işi zaten çalışıyor.");
                }
                var userName = user.UserName();
                var jobId = jobs.Enqueue<CatalogFetchJob>(job => job.RunAsync(id, validateOnly ?? true, createMissing ?? false, userName, CancellationToken.None));
                return Results.Accepted(value: new { jobId });
            })
            .WithTags("Kanal kataloğu").RequireAuthorization(Policies.Admin);
        app.MapGet("/api/channels/{id:int}/catalog/fetch-status", (SqlConnectionFactory connections, int id, CancellationToken ct) =>
                CatalogFetchJob.GetStatusAsync(connections, id, ct))
            .WithTags("Kanal kataloğu").RequireAuthorization(Policies.Read);

        var unmatched = app.MapGroup("/api/channel-catalog/unmatched").WithTags("Kanal kataloğu").RequireAuthorization(Policies.Read);
        unmatched.MapGet("/", (IChannelCatalogService s, int? channelId, string? status, string? search, int? page, int? pageSize, CancellationToken ct) =>
            s.SearchUnmatchedAsync(channelId, status, search, page, pageSize, ct));
        unmatched.MapPost("/{id:int}/link", (IChannelCatalogService s, ClaimsPrincipal user, int id, LinkUnmatchedRequest request, CancellationToken ct) =>
            s.LinkUnmatchedAsync(id, request.SkuId, user.UserName(), ct)).RequireAuthorization(Policies.CatalogWrite);
        unmatched.MapPost("/{id:int}/ignore", async (IChannelCatalogService s, ClaimsPrincipal user, int id, CancellationToken ct) =>
        {
            await s.IgnoreUnmatchedAsync(id, user.UserName(), ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.CatalogWrite);

        return app;
    }
}
