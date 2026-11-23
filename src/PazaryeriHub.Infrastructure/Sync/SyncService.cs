using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Sync;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.Sync;

/// <summary>Senkron Merkezi sorguları, yeniden deneme ve mutabakat raporu.</summary>
public sealed class SyncService(SqlConnectionFactory connectionFactory, SyncStore store, TimeProvider clock) : ISyncService
{
    public const int MaxPushListings = 5_000;

    public async Task<PushListingsResult> PushListingsAsync(IReadOnlyCollection<int> listingIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(listingIds);
        var ids = listingIds.Distinct().ToList();
        new Validator()
            .Must(ids.Count > 0, "listingIds", "En az bir ilan seçilmeli.")
            .Must(ids.Count <= MaxPushListings, "listingIds", $"Tek seferde en fazla {MaxPushListings:N0} ilan gönderilebilir.")
            .ThrowIfInvalid();

        await using var connection = await OpenAsync(ct);
        var rows = (await connection.QueryAsync<PushCandidate>(new CommandDefinition("""
            SELECT l.Id AS ListingId, c.Kod AS ChannelCode, l.Durum, c.Aktif AS ChannelActive, l.SonSenkron, l.SonGonderilenStok,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.StockBalance b WHERE b.SkuId = l.SkuId) THEN 1 ELSE 0 END AS bit) AS HasBalance
              FROM dbo.ChannelListing l JOIN dbo.Channel c ON c.Id = l.ChannelId
             WHERE l.Id IN (SELECT CAST([value] AS INT) FROM OPENJSON(@ids))
            """, new { ids = JsonSerializer.Serialize(ids) }, cancellationToken: ct))).ToDictionary(r => r.ListingId);
        var targets = (await store.ComputeTargetsAsync(connection, null, rows.Keys.ToList(), ct)).ToDictionary(t => t.ListingId);

        var now = clock.GetUtcNow().UtcDateTime;
        var skipped = new List<PushSkipped>();
        var toQueue = new List<int>();
        foreach (var id in ids)
        {
            if (!rows.TryGetValue(id, out var r))
            {
                skipped.Add(new(id, "İlan bulunamadı."));
                continue;
            }
            var reason = r.Durum != "AKTIF" ? "İlan pasif."
                : !r.ChannelActive ? "Kanal pasif."
                : !r.HasBalance ? "SKU'ya henüz stok girilmemiş; kanala gönderilmez."
                : ChannelResendRules.ChannelsWithWindow.Contains(r.ChannelCode)
                  && r.SonSenkron is { } last && last > now - ChannelResendRules.SameRequestWindow
                  && r.SonGonderilenStok == targets[id].Target
                    ? $"Aynı değer {(int)Math.Ceiling((last + ChannelResendRules.SameRequestWindow - now).TotalMinutes)} dk içinde tekrar gönderilemez (kanal kuralı); sonra tekrar deneyin."
                : null;
            if (reason is null)
            {
                toQueue.Add(id);
            }
            else
            {
                skipped.Add(new(id, reason));
            }
        }

        if (toQueue.Count > 0)
        {
            // EslestirmeDegisti: OutboxIsle bu ilanları hedef aynı olsa bile kuyruğa alır (mevcut hat, debounce ve hız limiti korunur)
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT dbo.Outbox (OlayTipi, Payload)
                SELECT @type, CONCAT(N'{"ListingId":', CAST([value] AS INT), N'}') FROM OPENJSON(@ids)
                """, new { type = OutboxEvents.ListingChanged, ids = JsonSerializer.Serialize(toQueue) }, cancellationToken: ct));
        }
        return new PushListingsResult(toQueue.Count, skipped);
    }

    private sealed class PushCandidate
    {
        public int ListingId { get; init; }
        public string ChannelCode { get; init; } = "";
        public string Durum { get; init; } = "";
        public bool ChannelActive { get; init; }
        public DateTime? SonSenkron { get; init; }
        public int? SonGonderilenStok { get; init; }
        public bool HasBalance { get; init; }
    }

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
                   q.GonderilenDeger, q.Olusma, q.SonDeneme, q.SonrakiDeneme, q.GonderilenSatisFiyati
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
                   (SELECT MAX(l2.SonSenkron) FROM dbo.ChannelListing l2 WHERE l2.ChannelId = c.Id) AS LastSentUtc,
                   SUM(CASE WHEN q.Tip = 'STOK' AND q.Durum = 'BEKLIYOR' AND q.BatchId IS NULL AND q.OnaylananDeger IS NULL
                             AND ISNULL(cap.Durum, 'DENEME') = 'DENEME' THEN 1 ELSE 0 END) AS AwaitingApproval
              FROM dbo.Channel c
              LEFT JOIN dbo.ChannelCapability cap ON cap.ChannelId = c.Id AND cap.Yetenek = 'STOK'
              LEFT JOIN dbo.ChannelListing l ON l.ChannelId = c.Id
              LEFT JOIN dbo.SyncQueue q ON q.ListingId = l.Id
             GROUP BY c.Id, c.Kod, cap.Durum
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

    // ------------------------------------------------------------------ stok gönderim onayı (ADR-048)

    public const int MaxApprovalItems = 5_000;

    /// <summary>Onay bekleyen kayıt: STOK, BEKLIYOR, talep edilmemiş, onaylanmamış ve kanalın STOK yeteneği DENEME ("Onaylı").</summary>
    private const string AwaitingApprovalWhere = """
        q.Tip = 'STOK' AND q.Durum = 'BEKLIYOR' AND q.BatchId IS NULL AND q.OnaylananDeger IS NULL
        AND ISNULL((SELECT cap.Durum FROM dbo.ChannelCapability cap WHERE cap.ChannelId = l.ChannelId AND cap.Yetenek = 'STOK'), 'DENEME') = 'DENEME'
        """;

    public async Task<IReadOnlyList<StockApprovalItem>> GetStockApprovalsAsync(int? channelId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var rows = (await connection.QueryAsync<ApprovalRow>(new CommandDefinition($"""
            SELECT TOP (@max) q.Id AS QueueId, l.Id AS ListingId, l.ChannelId, c.Kod AS ChannelCode, s.Id AS SkuId, s.SkuKodu, p.Ad AS ProductName,
                   l.KanalBarkod, l.KanalUrunKodu, l.KanalVerisi, l.SonGonderilenStok, snap.KanalStok, snap.CekilmeZamani, q.Olusma
              FROM dbo.SyncQueue q
              JOIN dbo.ChannelListing l ON l.Id = q.ListingId
              JOIN dbo.Channel c ON c.Id = l.ChannelId
              JOIN dbo.Sku s ON s.Id = l.SkuId
              JOIN dbo.Product p ON p.Id = s.ProductId
              OUTER APPLY (SELECT TOP (1) x.KanalStok, x.CekilmeZamani FROM dbo.ChannelStockSnapshot x
                            WHERE x.ListingId = l.Id ORDER BY x.CekilmeZamani DESC, x.Id DESC) AS snap
             WHERE {AwaitingApprovalWhere} AND (@channelId IS NULL OR l.ChannelId = @channelId)
             ORDER BY c.Id, s.SkuKodu, q.Id
            """, new { channelId, max = MaxApprovalItems }, cancellationToken: ct))).ToList();

        var targets = (await store.ComputeTargetsAsync(connection, null, rows.Select(r => r.ListingId).Distinct().ToList(), ct)).ToDictionary(t => t.ListingId);
        return rows.Select(r => new StockApprovalItem(r.QueueId, r.ListingId, r.ChannelId, r.ChannelCode, r.SkuId, r.SkuKodu, r.ProductName,
            r.KanalBarkod, r.KanalUrunKodu, PazaryeriHub.Application.Channels.ListingChannelData.Parse(r.KanalVerisi).ImageUrl,
            r.KanalStok, Utc(r.CekilmeZamani), r.SonGonderilenStok, targets[r.ListingId].Target, Utc(r.Olusma)!.Value)).ToList();
    }

    public async Task<StockApprovalResult> ApproveStockAsync(IReadOnlyCollection<StockApprovalDecision> items, string? user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);
        var decisions = items.GroupBy(i => i.QueueId).Select(g => g.Last()).ToList();
        new Validator()
            .Must(decisions.Count > 0, "items", "En az bir kayıt seçilmeli.")
            .Must(decisions.Count <= MaxApprovalItems, "items", $"Tek seferde en fazla {MaxApprovalItems:N0} kayıt onaylanabilir.")
            .ThrowIfInvalid();

        await using var connection = await OpenAsync(ct);
        var rows = (await connection.QueryAsync<(long QueueId, int ListingId)>(new CommandDefinition("""
            SELECT q.Id, q.ListingId FROM dbo.SyncQueue q JOIN dbo.ChannelListing l ON l.Id = q.ListingId
             WHERE q.Id IN (SELECT CAST([value] AS BIGINT) FROM OPENJSON(@ids))
               AND q.Tip = 'STOK' AND q.Durum = 'BEKLIYOR' AND q.BatchId IS NULL
            """, new { ids = JsonSerializer.Serialize(decisions.Select(d => d.QueueId)) }, cancellationToken: ct))).ToDictionary(r => r.QueueId, r => r.ListingId);
        var targets = (await store.ComputeTargetsAsync(connection, null, rows.Values.Distinct().ToList(), ct)).ToDictionary(t => t.ListingId);

        var now = clock.GetUtcNow().UtcDateTime;
        var skipped = new List<StockApprovalSkipped>();
        var approved = 0;
        foreach (var d in decisions)
        {
            if (!rows.TryGetValue(d.QueueId, out var listingId))
            {
                skipped.Add(new(d.QueueId, "Kayıt artık onay beklemiyor (gönderildi, reddedildi ya da gönderimde)."));
                continue;
            }
            var current = targets[listingId].Target;
            if (current != d.Value)
            {
                skipped.Add(new(d.QueueId, $"Stok o arada değişti (şimdi {current}); listeyi yenileyip yeniden onaylayın."));
                continue;
            }
            approved += await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE dbo.SyncQueue SET OnaylananDeger = @value, OnaylayanKullanici = @user, OnayTarihi = @now
                 WHERE Id = @id AND Tip = 'STOK' AND Durum = 'BEKLIYOR' AND BatchId IS NULL
                """, new { id = d.QueueId, value = d.Value, user, now }, cancellationToken: ct));
        }
        return new StockApprovalResult(approved, skipped);
    }

    public async Task<int> RejectStockAsync(IReadOnlyCollection<long> queueIds, string? user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queueIds);
        var ids = queueIds.Distinct().ToList();
        new Validator()
            .Must(ids.Count > 0, "queueIds", "En az bir kayıt seçilmeli.")
            .Must(ids.Count <= MaxApprovalItems, "queueIds", $"Tek seferde en fazla {MaxApprovalItems:N0} kayıt reddedilebilir.")
            .ThrowIfInvalid();

        await using var connection = await OpenAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.SyncQueue SET Durum = 'IPTAL', Hata = CONCAT(N'Yönetici reddetti: ', @user), OnayTarihi = @now, OnaylayanKullanici = @user
             WHERE Id IN (SELECT CAST([value] AS BIGINT) FROM OPENJSON(@ids))
               AND Tip = 'STOK' AND Durum = 'BEKLIYOR' AND BatchId IS NULL
            """, new { ids = JsonSerializer.Serialize(ids), user, now = clock.GetUtcNow().UtcDateTime }, cancellationToken: ct));
    }

    private sealed class ApprovalRow
    {
        public long QueueId { get; init; }
        public int ListingId { get; init; }
        public int ChannelId { get; init; }
        public string ChannelCode { get; init; } = "";
        public int SkuId { get; init; }
        public string SkuKodu { get; init; } = "";
        public string ProductName { get; init; } = "";
        public string? KanalBarkod { get; init; }
        public string? KanalUrunKodu { get; init; }
        public string? KanalVerisi { get; init; }
        public int? SonGonderilenStok { get; init; }
        public int? KanalStok { get; init; }
        public DateTime? CekilmeZamani { get; init; }
        public DateTime Olusma { get; init; }
    }

    private static async Task<SyncQueueItem> GetItemAsync(SqlConnection connection, long id, CancellationToken ct)
    {
        var row = await connection.QuerySingleAsync<QueueRow>(new CommandDefinition("""
            SELECT q.Id, q.ListingId, s.SkuKodu, c.Kod AS ChannelCode, q.Tip, q.Durum, q.Deneme, q.Hata, q.BatchId,
                   q.GonderilenDeger, q.Olusma, q.SonDeneme, q.SonrakiDeneme, q.GonderilenSatisFiyati
              FROM dbo.SyncQueue q JOIN dbo.ChannelListing l ON l.Id = q.ListingId
              JOIN dbo.Sku s ON s.Id = l.SkuId JOIN dbo.Channel c ON c.Id = l.ChannelId
             WHERE q.Id = @id
            """, new { id }, cancellationToken: ct));
        return ToItem(row);
    }

    private static SyncQueueItem ToItem(QueueRow r) => new(r.Id, r.ListingId, r.SkuKodu, r.ChannelCode, r.Tip, r.Durum, r.Deneme, r.Hata,
        r.BatchId, r.GonderilenDeger, Utc(r.Olusma)!.Value, Utc(r.SonDeneme), Utc(r.SonrakiDeneme), r.GonderilenSatisFiyati);

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
        public decimal? GonderilenSatisFiyati { get; init; }
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
