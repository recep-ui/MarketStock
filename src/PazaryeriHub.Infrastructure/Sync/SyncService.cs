using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Sync;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.Sync;

/// <summary>Senkron Merkezi sorguları, yeniden deneme ve mutabakat raporu.</summary>
public sealed class SyncService(SqlConnectionFactory connectionFactory, SyncStore store) : ISyncService
{
    public async Task<PagedResult<SyncQueueItem>> SearchQueueAsync(int? channelId, string? status, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var st = Text.Clean(status)?.ToUpperInvariant();
        const string where = """
            WHERE (@channelId IS NULL OR l.ChannelId = @channelId) AND (@status IS NULL OR q.Durum = @status)
            """;
        var args = new { channelId, status = st, skip = (p - 1) * size, take = size };

        await using var connection = await OpenAsync(ct);
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM dbo.SyncQueue q JOIN dbo.ChannelListing l ON l.Id = q.ListingId {where}", args, cancellationToken: ct));
        var rows = await connection.QueryAsync<QueueRow>(new CommandDefinition($"""
            SELECT q.Id, q.ListingId, s.SkuKodu, c.Kod AS ChannelCode, q.Tip, q.Durum, q.Deneme, q.Hata, q.BatchId,
                   q.GonderilenDeger, q.Olusma, q.SonDeneme, q.SonrakiDeneme
              FROM dbo.SyncQueue q
              JOIN dbo.ChannelListing l ON l.Id = q.ListingId
              JOIN dbo.Sku s ON s.Id = l.SkuId
              JOIN dbo.Channel c ON c.Id = l.ChannelId
            {where}
             ORDER BY q.Id DESC
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY
            """, args, cancellationToken: ct));
        return new PagedResult<SyncQueueItem>(rows.Select(ToItem).ToList(), total, p, size);
    }

    public async Task<IReadOnlyList<SyncSummaryRow>> GetSummaryAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<SyncSummaryRow>(new CommandDefinition("""
            SELECT c.Kod AS ChannelCode,
                   SUM(CASE WHEN q.Durum = 'BEKLIYOR' AND q.BatchId IS NULL THEN 1 ELSE 0 END) AS Pending,
                   SUM(CASE WHEN q.Durum = 'BEKLIYOR' AND q.BatchId IS NOT NULL THEN 1 ELSE 0 END) AS InFlight,
                   SUM(CASE WHEN q.Durum = 'GONDERILDI' THEN 1 ELSE 0 END) AS Sent,
                   SUM(CASE WHEN q.Durum = 'HATA' THEN 1 ELSE 0 END) AS Error,
                   (SELECT MAX(l2.SonSenkron) FROM dbo.ChannelListing l2 WHERE l2.ChannelId = c.Id) AS LastSentUtc
              FROM dbo.Channel c
              LEFT JOIN dbo.ChannelListing l ON l.ChannelId = c.Id
              LEFT JOIN dbo.SyncQueue q ON q.ListingId = l.Id
             GROUP BY c.Id, c.Kod
             ORDER BY c.Id
            """, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<SyncQueueItem> RetryAsync(long id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<(string Durum, int ListingId, string Tip)?>(new CommandDefinition(
            "SELECT Durum, ListingId, Tip FROM dbo.SyncQueue WHERE Id = @id", new { id }, cancellationToken: ct))
            ?? throw new NotFoundException($"Senkron kaydı bulunamadı (Id={id}).");
        if (row.Durum != SyncStatus.Error)
        {
            throw new ConflictException("Yalnızca HATA durumundaki kayıtlar yeniden denenebilir.");
        }

        try
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE dbo.SyncQueue SET Durum = 'BEKLIYOR', Deneme = 0, SonrakiDeneme = NULL, BatchId = NULL
                 WHERE Id = @id AND Durum = 'HATA'
                """, new { id }, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number == DbErrors.UniqueIndex)
        {
            throw new ConflictException("Bu eşleştirme için zaten bekleyen bir gönderim var; o gönderim en güncel değeri taşıyacak.", ex);
        }

        return await GetItemAsync(connection, id, ct);
    }

    public async Task RequestChannelRefreshAsync(int channelId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var exists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.Channel WHERE Id = @channelId", new { channelId }, cancellationToken: ct));
        if (exists == 0)
        {
            throw new NotFoundException($"Kanal bulunamadı (Id={channelId}).");
        }
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT dbo.Outbox (OlayTipi, Payload) VALUES (@type, @payload)",
            new { type = OutboxEvents.ChannelRefresh, payload = JsonSerializer.Serialize(new { ChannelId = channelId }) }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ReconciliationRow>> GetReconciliationAsync(int? channelId, bool onlyDeviations, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var rows = (await connection.QueryAsync<SnapshotRow>(new CommandDefinition("""
            SELECT l.Id AS ListingId, s.SkuKodu, c.Kod AS ChannelCode, l.KanalUrunKodu, l.KanalBarkod, l.SonGonderilenStok,
                   snap.KanalStok, snap.CekilmeZamani
              FROM dbo.ChannelListing l
              JOIN dbo.Sku s ON s.Id = l.SkuId
              JOIN dbo.Channel c ON c.Id = l.ChannelId
              OUTER APPLY (SELECT TOP (1) x.KanalStok, x.CekilmeZamani FROM dbo.ChannelStockSnapshot x
                            WHERE x.ListingId = l.Id ORDER BY x.CekilmeZamani DESC, x.Id DESC) AS snap
             WHERE l.Durum = 'AKTIF' AND (@channelId IS NULL OR l.ChannelId = @channelId)
             ORDER BY s.SkuKodu, c.Kod
            """, new { channelId }, cancellationToken: ct))).ToList();

        var targets = (await store.ComputeTargetsAsync(connection, null, rows.Select(r => r.ListingId).ToList(), ct)).ToDictionary(t => t.ListingId);
        var report = rows.Select(r => new ReconciliationRow(r.ListingId, r.SkuKodu, r.ChannelCode, r.KanalUrunKodu, r.KanalBarkod,
            targets[r.ListingId].Target, r.SonGonderilenStok, r.KanalStok,
            r.CekilmeZamani is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null)).ToList();

        // Sapma: kanaldaki değer beklenenden farklı veya eşleştirme kanalda hiç görülmemiş (mutabakat çalıştıysa).
        return onlyDeviations ? report.Where(r => r.ChannelStock is null || r.Deviation != 0).ToList() : report;
    }

    private static async Task<SyncQueueItem> GetItemAsync(SqlConnection connection, long id, CancellationToken ct)
    {
        var row = await connection.QuerySingleAsync<QueueRow>(new CommandDefinition("""
            SELECT q.Id, q.ListingId, s.SkuKodu, c.Kod AS ChannelCode, q.Tip, q.Durum, q.Deneme, q.Hata, q.BatchId,
                   q.GonderilenDeger, q.Olusma, q.SonDeneme, q.SonrakiDeneme
              FROM dbo.SyncQueue q JOIN dbo.ChannelListing l ON l.Id = q.ListingId
              JOIN dbo.Sku s ON s.Id = l.SkuId JOIN dbo.Channel c ON c.Id = l.ChannelId
             WHERE q.Id = @id
            """, new { id }, cancellationToken: ct));
        return ToItem(row);
    }

    private static SyncQueueItem ToItem(QueueRow r) => new(r.Id, r.ListingId, r.SkuKodu, r.ChannelCode, r.Tip, r.Durum, r.Deneme, r.Hata,
        r.BatchId, r.GonderilenDeger, Utc(r.Olusma)!.Value, Utc(r.SonDeneme), Utc(r.SonrakiDeneme));

    private static DateTime? Utc(DateTime? d) => d is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        return connection;
    }

    private sealed class QueueRow
    {
        public long Id { get; init; }
        public int ListingId { get; init; }
        public string SkuKodu { get; init; } = "";
        public string ChannelCode { get; init; } = "";
        public string Tip { get; init; } = "";
        public string Durum { get; init; } = "";
        public int Deneme { get; init; }
        public string? Hata { get; init; }
        public string? BatchId { get; init; }
        public int? GonderilenDeger { get; init; }
        public DateTime Olusma { get; init; }
        public DateTime? SonDeneme { get; init; }
        public DateTime? SonrakiDeneme { get; init; }
    }

    private sealed class SnapshotRow
    {
        public int ListingId { get; init; }
        public string SkuKodu { get; init; } = "";
        public string ChannelCode { get; init; } = "";
        public string? KanalUrunKodu { get; init; }
        public string? KanalBarkod { get; init; }
        public int? SonGonderilenStok { get; init; }
        public int? KanalStok { get; init; }
        public DateTime? CekilmeZamani { get; init; }
    }
}
