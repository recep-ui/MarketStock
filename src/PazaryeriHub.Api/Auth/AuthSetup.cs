using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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
            .AddPolicy(Policies.Admin, p => p.RequireRole(Roles.Admin));

        return services;
    }

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Oturum");

        auth.MapPost("/login", async (HttpContext ctx, IUserService users, LoginRequest request, CancellationToken ct) =>
        {
            var result = await users.ValidateAsync(request.UserName, request.Password, ct);
            if (result.User is null)
            {
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
            return Results.Ok(Me(identity));
        }).AllowAnonymous();

        auth.MapPost("/logout", async (HttpContext ctx) =>
        {
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
        usersGroup.MapPut("/{id:int}", (IUserService s, int id, UpdateUserRequest request, CancellationToken ct) => s.UpdateAsync(id, request, ct));
        usersGroup.MapPost("/{id:int}/password", async (IUserService s, int id, SetPasswordRequest request, CancellationToken ct) =>
        {
            await s.SetPasswordAsync(id, request.Password, ct);
            return Results.NoContent();
        });

        return app;
    }

    /// <summary>İşlemi yapan kullanıcı (stok hareketlerine "Kullanici" olarak yazılır).</summary>
    public static string? UserName(this ClaimsPrincipal user) => user.Identity?.Name;

    private static object Me(ClaimsIdentity? identity) => new
    {
        userName = identity?.Name,
        fullName = identity?.FindFirst("fullName")?.Value,
        role = identity?.FindFirst(ClaimTypes.Role)?.Value
    };
}
