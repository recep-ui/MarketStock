using Microsoft.Extensions.FileProviders;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.Panel;

namespace PazaryeriHub.Api.Endpoints;

/// <summary>Panel ekranları için okuma uçları ve derlenmiş panelin (panel/dist) servis edilmesi.</summary>
public static class PanelEndpoints
{
    public static IEndpointRouteBuilder MapPanelEndpoints(this IEndpointRouteBuilder app)
    {
        var panel = app.MapGroup("/api").RequireAuthorization(Policies.Read).WithTags("Panel");
        panel.MapGet("/dashboard", (IPanelQueryService s, CancellationToken ct) => s.GetDashboardAsync(ct));
        panel.MapGet("/stock-overview", (IPanelQueryService s, string? search, bool? onlyDeviations, bool? lowStock, int? page, int? pageSize, CancellationToken ct) =>
            s.GetStockOverviewAsync(search, onlyDeviations ?? false, lowStock ?? false, page, pageSize, ct));
        panel.MapGet("/stock-movements", (IPanelQueryService s, string? sku, string? type, int? warehouseId, DateTime? from, DateTime? to,
                string? sourceType, int? page, int? pageSize, CancellationToken ct) =>
            s.GetMovementsAsync(new MovementQuery(sku, type, warehouseId, from, to, sourceType, page, pageSize), ct));
        panel.MapGet("/settings", (IPanelQueryService s, CancellationToken ct) => s.GetSettingsAsync(ct));
        panel.MapPut("/settings", (IPanelQueryService s, AppSettingsDto settings, CancellationToken ct) => s.UpdateSettingsAsync(settings, ct))
            .RequireAuthorization(Policies.Admin);
        return app;
    }

    /// <summary>
    /// Derlenmiş panel varsa (Panel:DistPath, varsayılan ../../panel/dist) statik dosyalar ve SPA yönlendirmesi.
    /// /api, /health ve /hangfire panel tarafından yakalanmaz. Panel yoksa false döner.
    /// </summary>
    public static bool UsePanelStaticFiles(this WebApplication app)
    {
        var configured = app.Configuration["Panel:DistPath"] ?? Path.Combine("..", "..", "panel", "dist");
        var path = Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(app.Environment.ContentRootPath, configured));
        if (!File.Exists(Path.Combine(path, "index.html")))
        {
            return false;
        }

        var files = new PhysicalFileProvider(path);
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        app.MapFallback(async context =>
        {
            var requestPath = context.Request.Path;
            if (requestPath.StartsWithSegments("/api") || requestPath.StartsWithSegments("/health") || requestPath.StartsWithSegments("/hangfire"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.SendFileAsync(files.GetFileInfo("index.html"));
        });
        app.Logger.LogInformation("Panel servis ediliyor: {Klasor}", path);
        return true;
    }
}
