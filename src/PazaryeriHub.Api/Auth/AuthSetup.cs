using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using PazaryeriHub.Application.Audit;
using PazaryeriHub.Application.Auth;

namespace PazaryeriHub.Api.Auth;

public sealed record LoginRequest(string UserName, string Password);

public sealed record SetPasswordRequest(string Password);

/// <summary>
/// Panel kimlik doğrulaması: HttpOnly + SameSite=Strict çerez (panel API ile aynı kökenden servis edilir).
/// API isteklerinde giriş sayfasına yönlendirme yerine 401/403 döner. Rol → yetki eşlemesi <see cref="Policies"/>.
/// </summary>
public static class AuthSetup
{
    public const string CookieName = "PazaryeriHub.Oturum";

    public static IServiceCollection AddPanelAuth(this IServiceCollection services)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                options.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                // Açık oturum her istekte doğrulanır (ADR-045): silinen/pasife alınan kullanıcının çerezi geçersiz olur,
                // rol değişikliği beklemeden uygulanır.
                options.Events.OnValidatePrincipal = ValidateSessionAsync;
                options.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Read, p => p.RequireRole(Roles.All))
            .AddPolicy(Policies.StockWrite, p => p.RequireRole(Roles.Admin, Roles.Warehouse))
            .AddPolicy(Policies.OrderWrite, p => p.RequireRole(Roles.Admin, Roles.Warehouse))
            .AddPolicy(Policies.CatalogWrite, p => p.RequireRole(Roles.Admin))
            .AddPolicy(Policies.Admin, p => p.RequireRole(Roles.Admin))
            .AddPolicy(Policies.QuestionAnswer, p => p.RequireRole(Roles.Admin, Roles.Support));

        return services;
    }

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Oturum");

        auth.MapPost("/login", async (HttpContext ctx, IUserService users, LoginRequest request, CancellationToken ct) =>
        {
            var result = await users.ValidateAsync(request.UserName, request.Password, ct);
            var audit = ctx.RequestServices.GetRequiredService<IAuditLog>();
            if (result.User is null)
            {
                // Denenen kullanıcı adı yazılır (parola asla)
                await audit.WriteAsync(new AuditRecord(AuditTypes.LoginFailed, request.UserName?.Trim(), null, "POST", "/api/auth/login",
                    StatusCodes.Status401Unauthorized, ClientIp(ctx), null), CancellationToken.None);
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Giriş yapılamadı.",
                    detail: result.LockedOut
                        ? "Çok sayıda hatalı deneme nedeniyle hesap geçici olarak kilitlendi. Birkaç dakika sonra tekrar deneyin."
                        : "Kullanıcı adı veya parola hatalı.");
            }

            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, result.User.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, result.User.UserName),
                new Claim("fullName", result.User.FullName),
                new Claim(ClaimTypes.Role, result.User.Role)
            ], CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            await audit.WriteAsync(new AuditRecord(AuditTypes.Login, result.User.UserName, result.User.Role, "POST", "/api/auth/login",
                StatusCodes.Status200OK, ClientIp(ctx), null), CancellationToken.None);
            return Results.Ok(Me(identity));
        }).AllowAnonymous();

        auth.MapPost("/logout", async (HttpContext ctx, IAuditLog audit) =>
        {
            if (ctx.User.Identity?.IsAuthenticated == true)
            {
                await audit.WriteAsync(new AuditRecord(AuditTypes.Logout, ctx.User.Identity.Name, ctx.User.FindFirst(ClaimTypes.Role)?.Value,
                    "POST", "/api/auth/logout", StatusCodes.Status204NoContent, ClientIp(ctx), null), CancellationToken.None);
            }
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).AllowAnonymous();

        auth.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(Me(user.Identity as ClaimsIdentity)))
            .RequireAuthorization(Policies.Read);

        var usersGroup = app.MapGroup("/api/users").WithTags("Kullanıcılar").RequireAuthorization(Policies.Admin);
        usersGroup.MapGet("/", (IUserService s, CancellationToken ct) => s.GetUsersAsync(ct));
        usersGroup.MapPost("/", async (IUserService s, CreateUserRequest request, CancellationToken ct) =>
        {
            var created = await s.CreateAsync(request, ct);
            return Results.Created($"/api/users/{created.Id}", created);
        });
        usersGroup.MapPut("/{id:int}", (IUserService s, ClaimsPrincipal user, int id, UpdateUserRequest request, CancellationToken ct) =>
            s.UpdateAsync(id, request, user.UserId(), ct));
        usersGroup.MapDelete("/{id:int}", async (IUserService s, ClaimsPrincipal user, int id, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, user.UserId(), ct);
            return Results.NoContent();
        });
        usersGroup.MapPost("/{id:int}/password", async (IUserService s, int id, SetPasswordRequest request, CancellationToken ct) =>
        {
            await s.SetPasswordAsync(id, request.Password, ct);
            return Results.NoContent();
        });

        // İşlem geçmişi (ADR-045): yalnızca Yönetici
        app.MapGet("/api/audit", (IAuditLog audit, string? user, string? type, DateTime? from, DateTime? to, string? path, bool? errors,
                int? page, int? pageSize, CancellationToken ct) =>
            audit.QueryAsync(new AuditQuery(user, type, from?.ToUniversalTime(), to?.ToUniversalTime(), path, errors ?? false,
                page ?? 1, pageSize ?? 50), ct))
            .WithTags("İşlem geçmişi").RequireAuthorization(Policies.Admin);

        return app;
    }

    /// <summary>Çerezdeki kullanıcı hâlâ var ve aktif mi; rolü değiştiyse oturumdaki rol güncellenir.</summary>
    private static async Task ValidateSessionAsync(CookieValidatePrincipalContext ctx)
    {
        var id = ctx.Principal?.UserId();
        var role = id is { } userId
            ? await ctx.HttpContext.RequestServices.GetRequiredService<IUserService>().GetActiveRoleAsync(userId, ctx.HttpContext.RequestAborted)
            : null;
        if (role is null)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        if (ctx.Principal!.FindFirst(ClaimTypes.Role)?.Value != role)
        {
            var identity = new ClaimsIdentity(ctx.Principal.Claims.Where(c => c.Type != ClaimTypes.Role), ctx.Principal.Identity!.AuthenticationType);
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
            ctx.ReplacePrincipal(new ClaimsPrincipal(identity));
            ctx.ShouldRenew = true;
        }
    }

    /// <summary>Oturumdaki kullanıcının Id'si.</summary>
    public static int? UserId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : null;

    public static string? ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();

    /// <summary>İşlemi yapan kullanıcı (stok hareketlerine "Kullanici" olarak yazılır).</summary>
    public static string? UserName(this ClaimsPrincipal user) => user.Identity?.Name;

    private static object Me(ClaimsIdentity? identity) => new
    {
        userName = identity?.Name,
        fullName = identity?.FindFirst("fullName")?.Value,
        role = identity?.FindFirst(ClaimTypes.Role)?.Value
    };
}
