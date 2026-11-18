using System.Security.Claims;
using PazaryeriHub.Api.Auth;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Application.Questions;

namespace PazaryeriHub.Api.Endpoints;

/// <summary>
/// Müşteri soruları (ADR-029). Okuma tüm roller (müşteri kişisel verisi saklanmaz); cevap SoruCevap yetkisi (Yönetici + Destek);
/// kanaldan elle çekme Yönetici. Cevap kanala doğrudan gitmez: SORU_CEVAP kanal komutu olarak KanalKomutGonder'den gider.
/// </summary>
public static class QuestionEndpoints
{
    public static IEndpointRouteBuilder MapQuestionEndpoints(this IEndpointRouteBuilder app)
    {
        var questions = app.MapGroup("/api/questions").WithTags("Soru-Cevap").RequireAuthorization(Policies.Read);
        questions.MapGet("/", (IQuestionService s, int? channelId, string? state, string? search, int? page, int? pageSize, CancellationToken ct) =>
            s.SearchAsync(channelId, state, search, page, pageSize, ct));
        questions.MapGet("/{id:long}", (IQuestionService s, long id, CancellationToken ct) => s.GetAsync(id, ct));
        questions.MapPost("/{id:long}/answer", (IQuestionService s, ClaimsPrincipal user, long id, QuestionAnswerInput input, CancellationToken ct) =>
            s.AnswerAsync(id, input, user.UserName(), ct))
            .RequireAuthorization(Policies.QuestionAnswer);
        questions.MapPost("/fetch/{channelCode}", (Hangfire.IBackgroundJobClient jobs, string channelCode) =>
        {
            var code = channelCode.ToUpperInvariant();
            if (!ChannelCodes.All.Contains(code))
            {
                return Results.Problem(statusCode: 404, title: "Bilinmeyen kanal.", detail: $"'{channelCode}' geçersiz.");
            }
            var jobId = Hangfire.BackgroundJobClientExtensions.Enqueue<PazaryeriHub.Infrastructure.Questions.QuestionFetchJob>(jobs,
                job => job.RunAsync(code, false, CancellationToken.None));
            return Results.Accepted(value: new { jobId });
        }).RequireAuthorization(Policies.Admin);
        return app;
    }
}
