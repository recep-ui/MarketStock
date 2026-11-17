using System.Text.Json;
using Dapper;
using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PazaryeriHub.Application.ChannelCatalog;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.ChannelCatalog;

/// <summary>
/// KatalogCek (ADR-022): kanal API'sinden ürün kataloğunu sayfa sayfa çeker ve Excel içe alma ile AYNI eşleştirme/yazma
/// yolundan geçirir (<see cref="IChannelCatalogService.ApplyAsync"/>). Panelden elle tetiklenir (zamanlanmış değil).
/// Önizleme modunda hiçbir şey yazılmaz; yalnızca rapor ChannelJobState.SonSonuc'a kaydedilir.
/// </summary>
public sealed class CatalogFetchJob(
    IEnumerable<IChannelAdapter> adapters,
    IChannelCatalogService catalog,
    SqlConnectionFactory connectionFactory,
    TimeProvider clock,
    ILogger<CatalogFetchJob> logger)
{
    public const string JobType = "KATALOG_CEK";

    /// <summary>Güvenlik sınırları: sonsuz sayfalama veya aşırı büyük katalogda durulur.</summary>
    public const int MaxPages = 2_000;
    public const int MaxItems = 50_000;

    /// <summary>Raporda saklanan en fazla satır (panel gösterimi için).</summary>
    public const int StoredRows = 500;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    [AutomaticRetry(Attempts = 0)]
    public async Task<CatalogImportReport?> RunAsync(int channelId, bool validateOnly, bool createMissing, string? user, CancellationToken ct)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var channelCode = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Kod FROM dbo.Channel WHERE Id = @channelId", new { channelId }, cancellationToken: ct))
            ?? throw new InvalidOperationException($"Kanal bulunamadı (Id={channelId}).");
        var adapter = adapters.FirstOrDefault(a => a.ChannelCode == channelCode)
                      ?? throw new InvalidOperationException($"'{channelCode}' için kanal adaptörü kayıtlı değil.");

        var startedAt = clock.GetUtcNow().UtcDateTime;
        await SaveStateAsync(connection, channelId, startedAt, CatalogFetchStates.Running, null, null, ct);

        var entries = new List<ChannelCatalogEntry>();
        var warnings = new List<string>();
        try
        {
            string? cursor = null;
            var pages = 0;
            do
            {
                var page = await adapter.FetchCatalogAsync(cursor, ct);
                entries.AddRange(page.Items);
                cursor = page.NextCursor;
                pages++;
                if (cursor is not null && (pages >= MaxPages || entries.Count >= MaxItems))
                {
                    warnings.Add($"Katalog çok büyük: {pages} sayfa / {entries.Count} öğeden sonra durduruldu; kalan ürünler işlenmedi.");
                    break;
                }
            } while (cursor is not null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = ex is ChannelApiException ? ex.Message : "Kanal kataloğu çekilemedi (ayrıntı logda).";
            await SaveStateAsync(connection, channelId, startedAt, CatalogFetchStates.Failed, message, null, ct);
            logger.LogError(ex, "KatalogCek başarısız: {Kanal}", channelCode);
            throw;
        }

        var (items, errors) = ChannelCatalogEntries.ToItems(entries);
        CatalogImportReport report;
        try
        {
            report = await catalog.ApplyAsync(channelId, items, CatalogImportSources.Api, new CatalogImportOptions(validateOnly, createMissing), user,
                errors, warnings, entries.Count, channelCode + "_API", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SaveStateAsync(connection, channelId, startedAt, CatalogFetchStates.Failed, "Katalog işlenemedi (ayrıntı logda).", null, ct);
            logger.LogError(ex, "KatalogCek işleme hatası: {Kanal}", channelCode);
            throw;
        }

        var stored = report with
        {
            Rows = report.Rows.Take(StoredRows).ToList(),
            RowsTruncated = report.RowsTruncated || report.Rows.Count > StoredRows,
            Errors = report.Errors.Take(200).ToList()
        };
        await SaveStateAsync(connection, channelId, startedAt, CatalogFetchStates.Completed, null, JsonSerializer.Serialize(stored, Json), ct);

        logger.LogInformation("KatalogCek {Kanal}: {Oge} öğe çekildi (önizleme: {Onizleme}).", channelCode, entries.Count, validateOnly);
        return report;
    }

    /// <summary>
    /// ChannelJobState satırı. SonSonuc = {"durum": ..., "rapor": ...}. Başarıda SonBasarili güncellenir.
    /// </summary>
    private static Task SaveStateAsync(SqlConnection connection, int channelId, DateTime at, string state, string? error, string? reportJson, CancellationToken ct)
    {
        var result = reportJson is null
            ? JsonSerializer.Serialize(new { durum = state })
            : $"{{\"durum\":{JsonSerializer.Serialize(state)},\"rapor\":{reportJson}}}";
        var success = state == CatalogFetchStates.Completed;
        return connection.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.ChannelJobState
               SET SonDeneme = @at,
                   SonBasarili = CASE WHEN @success = 1 THEN @at ELSE SonBasarili END,
                   SonHata = @error,
                   ArdisikHata = CASE WHEN @state = 'HATA' THEN ArdisikHata + 1 WHEN @success = 1 THEN 0 ELSE ArdisikHata END,
                   SonSonuc = @result
             WHERE ChannelId = @channelId AND IsTipi = @JobType;
            IF @@ROWCOUNT = 0
                INSERT dbo.ChannelJobState (ChannelId, IsTipi, SonBasarili, SonDeneme, SonHata, ArdisikHata, SonSonuc)
                VALUES (@channelId, @JobType, CASE WHEN @success = 1 THEN @at END, @at, @error, CASE WHEN @state = 'HATA' THEN 1 ELSE 0 END, @result);
            """, new { channelId, JobType, at, success, state, error = error is { Length: > 1000 } ? error[..1000] : error, result }, cancellationToken: ct));
    }

    /// <summary>
    /// İstek kuyruğa alınırken durum CALISIYOR yapılır (panel hemen "çalışıyor" görsün). Zaten çalışıyorsa
    /// (son 30 dk içinde başlamış ve bitmemiş) false döner; ikinci istek kuyruğa alınmaz.
    /// </summary>
    public static async Task<bool> TryMarkQueuedAsync(SqlConnectionFactory connectionFactory, TimeProvider clock, int channelId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var current = await GetStatusAsync(connectionFactory, channelId, ct);
        if (current.State == CatalogFetchStates.Running && current.LastAttemptUtc is { } at && at > now.AddMinutes(-30))
        {
            return false;
        }
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        await SaveStateAsync(connection, channelId, now, CatalogFetchStates.Running, null, null, ct);
        return true;
    }

    /// <summary>Panel için son durum.</summary>
    public static async Task<CatalogFetchStatus> GetStatusAsync(SqlConnectionFactory connectionFactory, int channelId, CancellationToken ct)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<(DateTime? SonDeneme, DateTime? SonBasarili, string? SonHata, string? SonSonuc)?>(
            new CommandDefinition("SELECT SonDeneme, SonBasarili, SonHata, SonSonuc FROM dbo.ChannelJobState WHERE ChannelId = @channelId AND IsTipi = @JobType",
                new { channelId, JobType }, cancellationToken: ct));
        if (row is not { } r)
        {
            return new CatalogFetchStatus(channelId, CatalogFetchStates.None, null, null, null, null);
        }

        var state = CatalogFetchStates.None;
        CatalogImportReport? report = null;
        if (r.SonSonuc is not null)
        {
            using var doc = JsonDocument.Parse(r.SonSonuc);
            state = doc.RootElement.TryGetProperty("durum", out var d) ? d.GetString() ?? state : state;
            if (doc.RootElement.TryGetProperty("rapor", out var rapor))
            {
                report = rapor.Deserialize<CatalogImportReport>(Json);
            }
        }
        return new CatalogFetchStatus(channelId, state, Utc(r.SonDeneme), Utc(r.SonBasarili), r.SonHata, report);
    }

    private static DateTime? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;
}
