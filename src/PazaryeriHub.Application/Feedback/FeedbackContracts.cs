namespace PazaryeriHub.Application.Feedback;

/// <summary>Öneri/istek durumları (ADR-064). Durumu yalnızca Yönetici değiştirir.</summary>
public static class FeedbackStatus
{
    public const string New = "YENI";
    public const string Done = "TAMAMLANDI";
    public const string Rejected = "REDDEDILDI";

    public static IReadOnlyList<string> All { get; } = [New, Done, Rejected];
}

public static class FeedbackLimits
{
    public const int Text = 2000;
}

/// <summary>Panelden gönderilen not. Yazan bilgisi gövdeden ALINMAZ, oturumdaki kullanıcıdan alınır.</summary>
public sealed record FeedbackInput(string? Text);

public sealed record FeedbackStatusInput(string? Status);

public sealed record FeedbackNote(int Id, string Text, string UserName, string FullName, string Status, DateTime CreatedAtUtc,
    string? StatusChangedBy, DateTime? StatusChangedAtUtc);

public sealed record FeedbackPage(IReadOnlyList<FeedbackNote> Items, int Total, int Page, int PageSize);

public interface IFeedbackService
{
    Task<FeedbackPage> ListAsync(string? status, int? page, int? pageSize, CancellationToken ct);

    /// <summary>Notu, oturumdaki kullanıcının (Id) güncel kullanıcı adı ve ad soyadıyla kaydeder.</summary>
    Task<FeedbackNote> CreateAsync(int userId, FeedbackInput input, CancellationToken ct);

    Task<FeedbackNote> SetStatusAsync(int id, FeedbackStatusInput input, string? changedBy, CancellationToken ct);
}
