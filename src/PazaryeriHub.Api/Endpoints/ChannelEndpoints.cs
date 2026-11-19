using System.Security.Claims;
using Hangfire;
using PazaryeriHub.Api.Auth;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Excel;

namespace PazaryeriHub.Api.Endpoints;

/// <param name="JobType">SIPARIS_CEK | IADE_CEK | SORU_CEK | KATALOG_CEK | MUTABAKAT | REFERANS_CEK</param>
public sealed record ChannelJobStateDto(string JobType, DateTime? LastSuccessUtc, DateTime? LastAttemptUtc, string? LastError, int ConsecutiveFailures)
{
    /// <summary>Hatanın türü ve ne yapılacağı (ADR-033); iş son çalışmada başarılıysa null.</summary>
    public ChannelErrorInfo? ErrorInfo => ConsecutiveFailures > 0 ? ChannelErrorExplainer.Explain(LastError) : null;
}

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
        // Kanal durum kartları (ADR-033): mod, ortam, yazma kilidi, yetenekler, iş durumları ve genel durum. Gizli bilgi dönmez.
        channels.MapGet("/status", (PazaryeriHub.Api.Channels.ChannelStatusQuery q, CancellationToken ct) => q.GetAsync(ct));
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
                PazaryeriHub.Adapters.Hepsiburada.HepsiburadaAdapter.Code => PazaryeriHub.Adapters.Hepsiburada.HepsiburadaCredentials.Parse(body.GetRawText()).ToJson(),
                PazaryeriHub.Adapters.Beymen.BeymenAdapter.Code => PazaryeriHub.Adapters.Beymen.BeymenCredentials.Parse(body.GetRawText()).ToJson(),
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

        // Satıcı Bilgileri (ADR-025): kanal referans verisi. Okuma tüm roller; yenileme Yönetici (Hangfire job'ı, ilke 5).
        channels.MapGet("/{id:int}/references", async (IChannelService s, IChannelReferenceService refs, int id, string? type, bool? includeInactive, CancellationToken ct) =>
        {
            if ((await s.GetChannelsAsync(ct)).All(c => c.Id != id))
            {
                return Results.Problem(statusCode: 404, title: "Kayıt bulunamadı.", detail: $"Kanal bulunamadı (Id={id}).");
            }
            ChannelReferenceType? parsed = null;
            if (!string.IsNullOrWhiteSpace(type))
            {
                if (!ChannelReferenceTypes.TryParse(type, out var t))
                {
                    return Results.Problem(statusCode: 400, title: "Bilinmeyen tip.",
                        detail: $"'{type}' geçersiz; {ChannelReferenceTypes.CargoProvider} veya {ChannelReferenceTypes.ClaimRejectReason} olmalı.");
                }
                parsed = t;
            }
            return Results.Ok(await refs.GetAsync(id, parsed, includeInactive ?? false, ct));
        });
        channels.MapPost("/{id:int}/references/refresh", async (IChannelService s, IBackgroundJobClient jobs,
            PazaryeriHub.Infrastructure.Persistence.SqlConnectionFactory connections, TimeProvider clock, int id, CancellationToken ct) =>
        {
            var channel = (await s.GetChannelsAsync(ct)).FirstOrDefault(c => c.Id == id);
            if (channel is null)
            {
                return Results.Problem(statusCode: 404, title: "Kayıt bulunamadı.", detail: $"Kanal bulunamadı (Id={id}).");
            }
            if (!await PazaryeriHub.Infrastructure.Channels.ReferenceFetchJob.TryMarkQueuedAsync(connections, clock, id, ct))
            {
                return Results.Problem(statusCode: 409, title: "Zaten çalışıyor.", detail: "Bu kanal için referans çekme işi zaten çalışıyor.");
            }
            var code = channel.Code;
            var jobId = jobs.Enqueue<PazaryeriHub.Infrastructure.Channels.ReferenceFetchJob>(job => job.RunAsync(code, false, CancellationToken.None));
            return Results.Accepted(value: new { jobId });
        }).RequireAuthorization(Policies.Admin);

        // Kanala yazma yetenekleri ve sipariş bildirim modu (ADR-026). Okuma tüm roller; değişiklik Yönetici.
        // Kanal işlerinin son çalışma durumu (SIPARIS_CEK, IADE_CEK, SORU_CEK, KATALOG_CEK, MUTABAKAT, REFERANS_CEK): panelde "son çekim" satırı.
        channels.MapGet("/{id:int}/job-states", async (PazaryeriHub.Infrastructure.Persistence.SqlConnectionFactory connections, int id, CancellationToken ct) =>
        {
            await using var connection = connections.Create();
            await connection.OpenAsync(ct);
            var rows = await Dapper.SqlMapper.QueryAsync<(string IsTipi, DateTime? SonBasarili, DateTime? SonDeneme, string? SonHata, int ArdisikHata)>(connection,
                new Dapper.CommandDefinition("SELECT IsTipi, SonBasarili, SonDeneme, SonHata, ArdisikHata FROM dbo.ChannelJobState WHERE ChannelId = @id",
                    new { id }, cancellationToken: ct));
            static DateTime? Utc(DateTime? v) => v is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;
            return rows.Select(r => new ChannelJobStateDto(r.IsTipi, Utc(r.SonBasarili), Utc(r.SonDeneme), r.SonHata, r.ArdisikHata)).ToList();
        });
        channels.MapGet("/{id:int}/capabilities", (IChannelCapabilityService s, int id, CancellationToken ct) => s.GetAsync(id, ct));
        channels.MapPut("/{id:int}/capabilities/{capability}", (IChannelCapabilityService s, ClaimsPrincipal user, int id, string capability,
                ChannelCapabilityUpdate update, CancellationToken ct) => s.SetAsync(id, capability, update.State, user.UserName(), ct))
            .RequireAuthorization(Policies.Admin);
        channels.MapGet("/{id:int}/notification-rules", (PazaryeriHub.Application.Orders.IOrderNotificationService n, int id, CancellationToken ct) =>
            n.GetRulesAsync(id, ct));
        channels.MapGet("/{id:int}/notification-mode", async (PazaryeriHub.Application.Orders.IOrderNotificationService n, int id, CancellationToken ct) =>
            new { mode = await n.GetModeAsync(id, ct) });
        channels.MapPut("/{id:int}/notification-mode", async (PazaryeriHub.Application.Orders.IOrderNotificationService n, ClaimsPrincipal user, int id,
                NotificationModeUpdate update, CancellationToken ct) =>
            {
                await n.SetModeAsync(id, update.Mode, user.UserName(), ct);
                return Results.NoContent();
            })
            .RequireAuthorization(Policies.Admin);

        // Kanal işlemleri (komut kuyruğu): liste herkese açık; onay / yeniden dene / iptal Yönetici.
        var commands = app.MapGroup("/api/channel-commands").WithTags("Kanal işlemleri").RequireAuthorization(Policies.Read);
        commands.MapGet("/", (IChannelCommandService s, int? channelId, string? type, string? state, string? targetKey, int? page, int? pageSize, CancellationToken ct) =>
            s.SearchAsync(new ChannelCommandQuery(channelId, type, state, targetKey, page, pageSize), ct));
        commands.MapPost("/{id:long}/approve", (IChannelCommandService s, ClaimsPrincipal user, long id, CancellationToken ct) => s.ApproveAsync(id, user.UserName(), ct))
            .RequireAuthorization(Policies.Admin);
        commands.MapPost("/{id:long}/retry", (IChannelCommandService s, ClaimsPrincipal user, long id, CancellationToken ct) => s.RetryAsync(id, user.UserName(), ct))
            .RequireAuthorization(Policies.Admin);
        commands.MapPost("/{id:long}/cancel", (IChannelCommandService s, ClaimsPrincipal user, long id, CancellationToken ct) => s.CancelAsync(id, user.UserName(), ct))
            .RequireAuthorization(Policies.Admin);

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
            case "prices": kind = ExcelKind.Prices; return true;
            default: kind = default; return false;
        }
    }

    private static IResult UnknownKind(string kind) =>
        Results.Problem(statusCode: 404, title: "Bilinmeyen tür.", detail: $"'{kind}' geçersiz; products, stock veya listings olmalı.");
}

public sealed record NotificationModeUpdate(string Mode);
