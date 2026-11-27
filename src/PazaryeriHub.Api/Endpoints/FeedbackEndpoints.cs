using System.Security.Claims;
using PazaryeriHub.Api.Auth;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.Feedback;

namespace PazaryeriHub.Api.Endpoints;

/// <summary>
/// Öneri ve istekler (ADR-064). Tüm roller yazar ve okur; yazan oturumdaki kullanıcıdır (gövdeden alınmaz).
/// Durum (Tamamlandı / Reddedildi / Yeni) yalnızca Yönetici.
/// </summary>
public static class FeedbackEndpoints
{
    public static IEndpointRouteBuilder MapFeedbackEndpoints(this IEndpointRouteBuilder app)
    {
        var feedback = app.MapGroup("/api/feedback").WithTags("Öneri ve istekler").RequireAuthorization(Policies.Read);
        feedback.MapGet("/", (IFeedbackService s, string? status, int? page, int? pageSize, CancellationToken ct) =>
            s.ListAsync(status, page, pageSize, ct));
        feedback.MapPost("/", async (IFeedbackService s, ClaimsPrincipal user, FeedbackInput input, CancellationToken ct) =>
        {
            if (user.UserId() is not { } userId)
            {
                return Results.Unauthorized();
            }
            var note = await s.CreateAsync(userId, input, ct);
            return Results.Created($"/api/feedback/{note.Id}", note);
        });
        feedback.MapPut("/{id:int}/status", (IFeedbackService s, ClaimsPrincipal user, int id, FeedbackStatusInput input, CancellationToken ct) =>
            s.SetStatusAsync(id, input, user.UserName(), ct))
            .RequireAuthorization(Policies.Admin);
        return app;
    }
}
