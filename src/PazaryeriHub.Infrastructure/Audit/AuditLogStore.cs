using Dapper;
using Microsoft.Extensions.Logging;
using PazaryeriHub.Application.Audit;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.Audit;

/// <summary>İşlem geçmişi (ADR-045): dbo.AuditLog. Yazma hatası asıl işlemi bozmaz.</summary>
public sealed class AuditLogStore(SqlConnectionFactory connectionFactory, ILogger<AuditLogStore> logger) : IAuditLog
{
    private const int MaxPageSize = 200;

    public async Task WriteAsync(AuditRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            await using var connection = connectionFactory.Create();
            await connection.OpenAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT dbo.AuditLog (Kullanici, Rol, Tip, Metot, Yol, Durum, IpAdresi, SureMs)
                VALUES (@user, @role, @type, @method, @path, @status, @ip, @duration)
                """, new
                {
                    user = Clip(record.UserName, 100),
                    role = Clip(record.Role, 20),
                    type = record.Type,
                    method = Clip(record.Method, 10),
                    path = Clip(record.Path, 400),
                    status = record.Status,
                    ip = Clip(record.IpAddress, 45),
                    duration = record.DurationMs
                }, cancellationToken: ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "İşlem geçmişi kaydı yazılamadı ({Tip} {Metot} {Yol}).", record.Type, record.Method, record.Path);
        }
    }

    public async Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var v = new Validator();
        v.Must(query.Type is null || AuditTypes.All.Contains(query.Type), "type", $"Tip şunlardan biri olmalı: {string.Join(", ", AuditTypes.All)}.");
        v.Must(query.Page >= 1, "page", "Sayfa 1 veya büyük olmalı.");
        v.Must(query.PageSize is >= 1 and <= MaxPageSize, "pageSize", $"Sayfa boyutu 1-{MaxPageSize} olmalı.");
        v.ThrowIfInvalid();

        var where = new List<string>();
        var p = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(query.UserName)) { where.Add("Kullanici = @user"); p.Add("user", query.UserName.Trim()); }
        if (query.Type is not null) { where.Add("Tip = @type"); p.Add("type", query.Type); }
        if (query.FromUtc is { } from) { where.Add("Tarih >= @from"); p.Add("from", from); }
        if (query.ToUtc is { } to) { where.Add("Tarih < @to"); p.Add("to", to); }
        if (!string.IsNullOrWhiteSpace(query.PathContains))
        {
            where.Add("Yol LIKE @path ESCAPE '!'");
            p.Add("path", "%" + query.PathContains.Trim().Replace("!", "!!").Replace("%", "!%").Replace("_", "!_").Replace("[", "![") + "%");
        }
        if (query.OnlyErrors) { where.Add("Durum >= 400"); }
        var filter = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);
        p.Add("skip", (query.Page - 1) * query.PageSize);
        p.Add("take", query.PageSize);

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition($"""
            SELECT COUNT(*) FROM dbo.AuditLog {filter};
            SELECT Id, Tarih AS AtUtc, Kullanici AS UserName, Rol AS Role, Tip AS Type, Metot AS Method, Yol AS Path, Durum AS Status,
                   IpAdresi AS IpAddress, SureMs AS DurationMs
              FROM dbo.AuditLog {filter}
             ORDER BY Tarih DESC, Id DESC
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
            """, p, cancellationToken: ct));
        var total = await grid.ReadSingleAsync<int>();
        var items = (await grid.ReadAsync<AuditRow>())
            .Select(r => new AuditEntry(r.Id, DateTime.SpecifyKind(r.AtUtc, DateTimeKind.Utc), r.UserName, r.Role, r.Type, r.Method, r.Path,
                r.Status, r.IpAddress, r.DurationMs))
            .ToList();
        return new AuditPage(items, total, query.Page, query.PageSize);
    }

    private static string? Clip(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];

    private sealed class AuditRow
    {
        public long Id { get; init; }
        public DateTime AtUtc { get; init; }
        public string? UserName { get; init; }
        public string? Role { get; init; }
        public string Type { get; init; } = "";
        public string? Method { get; init; }
        public string? Path { get; init; }
        public int? Status { get; init; }
        public string? IpAddress { get; init; }
        public int? DurationMs { get; init; }
    }
}
