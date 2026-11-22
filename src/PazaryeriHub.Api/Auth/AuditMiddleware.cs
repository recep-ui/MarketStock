using System.Diagnostics;
using System.Security.Claims;
using PazaryeriHub.Application.Audit;

namespace PazaryeriHub.Api.Auth;

/// <summary>
/// İşlem geçmişi (ADR-045): /api altındaki her yazma isteğini (GET/HEAD/OPTIONS dışı) kim, ne zaman, hangi sonuçla yaptı diye kaydeder.
/// Giriş/çıkış oturum uçlarında ayrıca yazılır. İstek gövdesi ve sorgu dizesi kaydedilmez (parola, API anahtarı, müşteri verisi olabilir).
/// </summary>
public sealed class AuditMiddleware(RequestDelegate next, IAuditLog audit)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!ShouldRecord(ctx.Request))
        {
            await next(ctx);
            return;
        }

        var watch = Stopwatch.StartNew();
        var status = StatusCodes.Status500InternalServerError;
        try
        {
            await next(ctx);
            status = ctx.Response.StatusCode;
        }
        finally
        {
            var user = ctx.User.Identity?.IsAuthenticated == true ? ctx.User.Identity.Name : null;
            await audit.WriteAsync(new AuditRecord(AuditTypes.Request, user, ctx.User.FindFirst(ClaimTypes.Role)?.Value,
                ctx.Request.Method, ctx.Request.Path.Value, status, AuthSetup.ClientIp(ctx),
                (int)Math.Min(int.MaxValue, watch.ElapsedMilliseconds)), CancellationToken.None);
        }
    }

    internal static bool ShouldRecord(HttpRequest request) =>
        request.Path.StartsWithSegments("/api")
        && !request.Path.StartsWithSegments("/api/auth")
        && !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method);
}
