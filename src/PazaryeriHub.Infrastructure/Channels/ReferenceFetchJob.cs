using System.Text.Json;
using Dapper;
using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.Channels;

/// <summary>
/// ReferansCek (ADR-025): kanalın kargo firması ve iade red gerekçesi listelerini çeker ve <c>ChannelReference</c>'a yazar.
/// Salt okumadır (kanala yazma yok). Panelden elle veya haftalık tetiklenir (ilke 5: dış çağrı yalnızca Hangfire job'ında).
/// - İkinci çekim yeni kayıt açmaz (UNIQUE ChannelId + Tip + KanalKimlik; MERGE).
/// - Kanalda artık bulunmayan kayıt silinmez, Aktif = 0 olur. Kanal BOŞ liste dönerse mevcut kayıtlar korunur
///   (geçici bir kanal hatası listeyi silmesin).
/// - Kanal tipi desteklemiyorsa (NotSupportedException) sonuç DESTEKLENMIYOR olur; mevcut kayıtlara dokunulmaz ve hata sayılmaz.
/// </summary>
public sealed class ReferenceFetchJob(
    IEnumerable<IChannelAdapter> adapters,
    SqlConnectionFactory connectionFactory,
    TimeProvider clock,
    ILogger<ReferenceFetchJob> logger)
{
    public const string JobType = "REFERANS_CEK";

    public static string RecurringJobId(string channelCode) => "referans-cek-" + channelCode.ToLowerInvariant();

    public static class Outcomes
    {
        public const string Updated = "GUNCELLENDI";
        public const string NotSupported = "DESTEKLENMIYOR";
        public const string Failed = "HATA";
    }

    public static class States
    {
        public const string Running = "CALISIYOR";
        public const string Completed = "TAMAMLANDI";
        public const string Failed = "HATA";
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="scheduled">Zamanlanmış çalışmada pasif kanal atlanır; elle tetiklemede pasif kanal da çekilir (salt okuma).</param>
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 0)]
    public async Task<IReadOnlyList<ChannelReferenceTypeResult>> RunAsync(string channelCode, bool scheduled, CancellationToken ct)
    {
        var adapter = adapters.FirstOrDefault(a => a.ChannelCode == channelCode)
                      ?? throw new InvalidOperationException($"'{channelCode}' için kanal adaptörü kayıtlı değil.");
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var channel = await connection.QuerySingleOrDefaultAsync<(int Id, bool Aktif)?>(new CommandDefinition(
            "SELECT Id, Aktif FROM dbo.Channel WHERE Kod = @channelCode", new { channelCode }, cancellationToken: ct))
            ?? throw new InvalidOperationException($"Kanal bulunamadı ({channelCode}).");
        if (scheduled && !channel.Aktif)
        {
            return [];
        }

        var startedAt = clock.GetUtcNow().UtcDateTime;
        var results = new List<ChannelReferenceTypeResult>();
        foreach (var type in ChannelReferenceTypes.All)
        {
            var dbType = ChannelReferenceTypes.ToDb(type);
            try
            {
                var list = await adapter.FetchReferenceAsync(type, ct);
                var (count, deactivated) = await UpsertAsync(connection, channel.Id, dbType, list, startedAt, ct);
                results.Add(new ChannelReferenceTypeResult(dbType, Outcomes.Updated, count, deactivated,
                    ChannelReferenceTypes.SourceToDb(list.Source),
                    list.Items.Count == 0 ? "Kanal boş liste döndü; mevcut kayıtlar korundu." : list.Note));
            }
            catch (NotSupportedException ex)
            {
                results.Add(new ChannelReferenceTypeResult(dbType, Outcomes.NotSupported, 0, 0, null, ex.Message));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var message = ex is ChannelApiException ? ex.Message : "Referans verisi çekilemedi (ayrıntı logda).";
                logger.LogError(ex, "ReferansCek başarısız: {Kanal} {Tip}", channelCode, dbType);
                results.Add(new ChannelReferenceTypeResult(dbType, Outcomes.Failed, 0, 0, null, message));
            }
        }

        var failed = results.Where(r => r.Outcome == Outcomes.Failed).ToList();
        await SaveStateAsync(connection, channel.Id, startedAt, failed.Count == 0 ? States.Completed : States.Failed,
            failed.Count == 0 ? null : string.Join(" ", failed.Select(f => $"{f.Type}: {f.Message}")), results, ct);
        logger.LogInformation("ReferansCek {Kanal}: {Sonuc}", channelCode,
            string.Join(", ", results.Select(r => $"{r.Type}={r.Outcome}({r.Count})")));
        return results;
    }

    private static async Task<(int Count, int Deactivated)> UpsertAsync(
        SqlConnection connection, int channelId, string tip, ChannelReferenceList list, DateTime now, CancellationToken ct)
    {
        // Aynı kod iki kez gelirse ilki alınır (MERGE kaynağında tekrar olamaz).
        var items = list.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.Code))
            .GroupBy(i => i.Code.Trim(), StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(i => new
            {
                code = Clip(i.Code.Trim(), 100),
                name = Clip(string.IsNullOrWhiteSpace(i.Name) ? i.Code.Trim() : i.Name.Trim(), 400),
                data = i.DataJson
            })
            .ToList();
        if (items.Count == 0)
        {
            return (0, 0);
        }

        var json = JsonSerializer.Serialize(items);
        var kaynak = ChannelReferenceTypes.SourceToDb(list.Source);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            MERGE dbo.ChannelReference WITH (HOLDLOCK) AS t
            USING (SELECT Kod COLLATE Latin1_General_100_BIN2 AS Kod, Ad, Veri
                     FROM OPENJSON(@json) WITH (Kod NVARCHAR(100) '$.code', Ad NVARCHAR(400) '$.name', Veri NVARCHAR(MAX) '$.data')) AS s
               ON t.ChannelId = @channelId AND t.Tip = @tip AND t.KanalKimlik = s.Kod
            WHEN MATCHED THEN
                UPDATE SET Ad = s.Ad, Veri = s.Veri, Kaynak = @kaynak, CekilmeZamani = @now, Aktif = 1
            WHEN NOT MATCHED BY TARGET THEN
                INSERT (ChannelId, Tip, KanalKimlik, Ad, Veri, Kaynak, CekilmeZamani, Aktif)
                VALUES (@channelId, @tip, s.Kod, s.Ad, s.Veri, @kaynak, @now, 1);
            """, new { json, channelId, tip, kaynak, now }, tx, cancellationToken: ct));
        var deactivated = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.ChannelReference SET Aktif = 0, CekilmeZamani = @now
             WHERE ChannelId = @channelId AND Tip = @tip AND Aktif = 1
               AND KanalKimlik NOT IN (SELECT Kod COLLATE Latin1_General_100_BIN2 FROM OPENJSON(@json) WITH (Kod NVARCHAR(100) '$.code'));
            """, new { json, channelId, tip, now }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return (items.Count, deactivated);
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];

    private static Task SaveStateAsync(SqlConnection connection, int channelId, DateTime at, string state, string? error,
        IReadOnlyList<ChannelReferenceTypeResult>? results, CancellationToken ct)
    {
        var result = JsonSerializer.Serialize(new { durum = state, sonuclar = results }, Json);
        var success = state == States.Completed;
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
            """, new { channelId, JobType, at, success, state, error = error is { Length: > 1000 } ? error[..1000] : error, result },
            cancellationToken: ct));
    }

    /// <summary>İstek kuyruğa alınırken durum CALISIYOR yapılır; son 10 dk içinde başlamış bitmemiş iş varsa false.</summary>
    public static async Task<bool> TryMarkQueuedAsync(SqlConnectionFactory connectionFactory, TimeProvider clock, int channelId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<(DateTime? SonDeneme, string? SonSonuc)?>(new CommandDefinition(
            "SELECT SonDeneme, SonSonuc FROM dbo.ChannelJobState WHERE ChannelId = @channelId AND IsTipi = @JobType",
            new { channelId, JobType }, cancellationToken: ct));
        if (row is { } r && ReadState(r.SonSonuc) == States.Running && r.SonDeneme is { } at && at > now.AddMinutes(-10))
        {
            return false;
        }
        await SaveStateAsync(connection, channelId, now, States.Running, null, null, ct);
        return true;
    }

    internal static string? ReadState(string? sonSonuc)
    {
        if (sonSonuc is null)
        {
            return null;
        }
        using var doc = JsonDocument.Parse(sonSonuc);
        return doc.RootElement.TryGetProperty("durum", out var d) ? d.GetString() : null;
    }

    internal static IReadOnlyList<ChannelReferenceTypeResult> ReadResults(string? sonSonuc)
    {
        if (sonSonuc is null)
        {
            return [];
        }
        using var doc = JsonDocument.Parse(sonSonuc);
        return doc.RootElement.TryGetProperty("sonuclar", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.Deserialize<List<ChannelReferenceTypeResult>>(Json) ?? []
            : [];
    }
}

/// <summary>Panel/API: referans listesi ve son çekme durumu (salt okuma).</summary>
public sealed class ChannelReferenceService(SqlConnectionFactory connectionFactory) : IChannelReferenceService
{
    public async Task<ChannelReferenceView> GetAsync(int channelId, ChannelReferenceType? type, bool includeInactive, CancellationToken ct)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var tip = type is { } t ? ChannelReferenceTypes.ToDb(t) : null;
        var rows = await connection.QueryAsync<Row>(new CommandDefinition("""
            SELECT Id, Tip, KanalKimlik, Ad, Veri, Kaynak, CekilmeZamani, Aktif
              FROM dbo.ChannelReference
             WHERE ChannelId = @channelId AND (@tip IS NULL OR Tip = @tip) AND (@includeInactive = 1 OR Aktif = 1)
             ORDER BY Tip, Aktif DESC, Ad
            """, new { channelId, tip, includeInactive }, cancellationToken: ct));
        var state = await connection.QuerySingleOrDefaultAsync<(DateTime? SonDeneme, DateTime? SonBasarili, string? SonHata, string? SonSonuc)?>(
            new CommandDefinition("SELECT SonDeneme, SonBasarili, SonHata, SonSonuc FROM dbo.ChannelJobState WHERE ChannelId = @channelId AND IsTipi = @JobType",
                new { channelId, ReferenceFetchJob.JobType }, cancellationToken: ct));

        var status = state is { } s
            ? new ChannelReferenceStatus(channelId, Utc(s.SonDeneme), Utc(s.SonBasarili), s.SonHata, ReferenceFetchJob.ReadResults(s.SonSonuc))
            : new ChannelReferenceStatus(channelId, null, null, null, []);
        return new ChannelReferenceView(
            rows.Select(r => new ChannelReferenceDto(r.Id, r.Tip, r.KanalKimlik, r.Ad, r.Veri, r.Kaynak,
                DateTime.SpecifyKind(r.CekilmeZamani, DateTimeKind.Utc), r.Aktif)).ToList(),
            status);
    }

    private static DateTime? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private sealed class Row
    {
        public int Id { get; init; }
        public string Tip { get; init; } = "";
        public string KanalKimlik { get; init; } = "";
        public string Ad { get; init; } = "";
        public string? Veri { get; init; }
        public string Kaynak { get; init; } = "";
        public DateTime CekilmeZamani { get; init; }
        public bool Aktif { get; init; }
    }
}
