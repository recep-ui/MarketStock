using Dapper;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Feedback;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.Feedback;

/// <summary>Öneri ve istekler (ADR-064): dbo.Oneri. Yazan, oturumdaki kullanıcının AppUser kaydından alınır.</summary>
public sealed class FeedbackService(SqlConnectionFactory connectionFactory) : IFeedbackService
{
    private const int MaxPageSize = 200;

    private static readonly string[] ColumnList =
    [
        "Id", "Metin AS Text", "KullaniciAdi AS UserName", "AdSoyad AS FullName", "Durum AS Status", "Olusma AS CreatedAtUtc",
        "DurumDegistiren AS StatusChangedBy", "DurumTarihi AS StatusChangedAtUtc"
    ];

    private static string Columns(string? alias = null) =>
        string.Join(", ", ColumnList.Select(c => alias is null ? c : $"{alias}.{c}"));

    private static string StatusError => $"Durum şunlardan biri olmalı: {string.Join(", ", FeedbackStatus.All)}.";

    public async Task<FeedbackPage> ListAsync(string? status, int? page, int? pageSize, CancellationToken ct)
    {
        var p = page ?? 1;
        var size = pageSize ?? 50;
        var state = Text.Clean(status)?.ToUpperInvariant();
        var v = new Validator();
        v.Must(state is null || FeedbackStatus.All.Contains(state), "status", StatusError);
        v.Must(p >= 1, "page", "Sayfa 1 veya büyük olmalı.");
        v.Must(size is >= 1 and <= MaxPageSize, "pageSize", $"Sayfa boyutu 1-{MaxPageSize} olmalı.");
        v.ThrowIfInvalid();

        var filter = state is null ? "" : "WHERE Durum = @state";
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition($"""
            SELECT COUNT(*) FROM dbo.Oneri {filter};
            SELECT {Columns()} FROM dbo.Oneri {filter}
             ORDER BY Olusma DESC, Id DESC
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
            """, new { state, skip = (p - 1) * size, take = size }, cancellationToken: ct));
        var total = await grid.ReadSingleAsync<int>();
        var items = (await grid.ReadAsync<Row>()).Select(ToNote).ToList();
        return new FeedbackPage(items, total, p, size);
    }

    public async Task<FeedbackNote> CreateAsync(int userId, FeedbackInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = new Validator();
        v.Required("text", input.Text, FeedbackLimits.Text);
        v.ThrowIfInvalid();

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        // Ad soyad o anki AppUser kaydından alınır (oturum çerezindeki eski ad değil)
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition($"""
            INSERT dbo.Oneri (Metin, KullaniciAdi, AdSoyad)
            OUTPUT {Columns("inserted")}
            SELECT @text, u.KullaniciAdi, u.AdSoyad FROM dbo.AppUser u WHERE u.Id = @userId AND u.Aktif = 1;
            """, new { text = input.Text!.Trim(), userId }, cancellationToken: ct));
        return row is null ? throw new NotFoundException("Oturumdaki kullanıcı bulunamadı.") : ToNote(row);
    }

    public async Task<FeedbackNote> SetStatusAsync(int id, FeedbackStatusInput input, string? changedBy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var state = Text.Clean(input.Status)?.ToUpperInvariant();
        var v = new Validator();
        v.Must(state is not null && FeedbackStatus.All.Contains(state), "status", StatusError);
        v.ThrowIfInvalid();

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition($"""
            UPDATE dbo.Oneri
               SET Durum = @state,
                   DurumDegistiren = CASE WHEN @state = 'YENI' THEN NULL ELSE @changedBy END,
                   DurumTarihi = CASE WHEN @state = 'YENI' THEN NULL ELSE SYSUTCDATETIME() END
            OUTPUT {Columns("inserted")}
             WHERE Id = @id;
            """, new { id, state, changedBy }, cancellationToken: ct));
        return row is null ? throw new NotFoundException($"Not bulunamadı (Id={id}).") : ToNote(row);
    }

    private static FeedbackNote ToNote(Row r) => new(r.Id, r.Text, r.UserName, r.FullName, r.Status,
        DateTime.SpecifyKind(r.CreatedAtUtc, DateTimeKind.Utc), r.StatusChangedBy,
        r.StatusChangedAtUtc is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null);

    private sealed class Row
    {
        public int Id { get; init; }
        public string Text { get; init; } = "";
        public string UserName { get; init; } = "";
        public string FullName { get; init; } = "";
        public string Status { get; init; } = "";
        public DateTime CreatedAtUtc { get; init; }
        public string? StatusChangedBy { get; init; }
        public DateTime? StatusChangedAtUtc { get; init; }
    }
}
