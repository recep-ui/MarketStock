using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Panel;
using PazaryeriHub.Infrastructure.Persistence;
using PazaryeriHub.Infrastructure.Sync;

namespace PazaryeriHub.Infrastructure.Panel;

/// <summary>Panel ekranlarının okuma sorguları (Dapper).</summary>
public sealed class PanelQueryService(SqlConnectionFactory connectionFactory, SyncStore syncStore, TimeProvider clock) : IPanelQueryService
{
    public const string CriticalStockKey = "KritikStokEsigi";
    private static readonly TimeZoneInfo Turkey = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    public async Task<DashboardDto> GetDashboardAsync(CancellationToken ct)
    {
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, Turkey);
        var day = DateOnly.FromDateTime(nowLocal);
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), Turkey);
        var toUtc = fromUtc.AddDays(1);

        await using var connection = await OpenAsync(ct);
        var threshold = await ThresholdAsync(connection, ct);
        using var multi = await connection.QueryMultipleAsync(new CommandDefinition("""
            SELECT c.Kod AS ChannelCode, COUNT(o.Id) AS Orders, ISNULL(SUM(o.Toplam), 0) AS Revenue
              FROM dbo.Channel c
              LEFT JOIN dbo.[Order] o ON o.ChannelId = c.Id AND o.SiparisTarihi >= @fromUtc AND o.SiparisTarihi < @toUtc AND o.Durum <> 'Iptal'
             GROUP BY c.Id, c.Kod ORDER BY c.Id;

            SELECT COUNT(*) FROM dbo.[Order] WHERE StokYetersiz = 1 AND Durum IN ('Yeni', 'Onaylandi', 'Hazirlaniyor');

            SELECT TOP (20) s.Id AS SkuId, s.SkuKodu AS SkuCode, p.Ad AS ProductName,
                   SUM(b.Fiziksel) AS Physical, SUM(b.Rezerve) AS Reserved, SUM(b.Satilabilir) AS Available
              FROM dbo.Sku s JOIN dbo.Product p ON p.Id = s.ProductId JOIN dbo.StockBalance b ON b.SkuId = s.Id
             GROUP BY s.Id, s.SkuKodu, p.Ad
            HAVING SUM(b.Satilabilir) <= @threshold
             ORDER BY SUM(b.Satilabilir), s.SkuKodu;

            SELECT SUM(CASE WHEN Durum = 'HATA' THEN 1 ELSE 0 END), SUM(CASE WHEN Durum = 'BEKLIYOR' THEN 1 ELSE 0 END) FROM dbo.SyncQueue;

            SELECT c.Kod AS ChannelCode, j.IsTipi AS JobType, j.SonBasarili AS LastSuccessUtc, j.SonDeneme AS LastAttemptUtc,
                   j.ArdisikHata AS ConsecutiveFailures, j.SonHata AS LastError
              FROM dbo.ChannelJobState j JOIN dbo.Channel c ON c.Id = j.ChannelId ORDER BY c.Id, j.IsTipi;
            """, new { fromUtc, toUtc, threshold }, cancellationToken: ct));

        var byChannel = (await multi.ReadAsync<ChannelRevenue>()).ToList();
        var insufficient = await multi.ReadSingleAsync<int>();
        var critical = (await multi.ReadAsync<CriticalStockItem>()).ToList();
        var (errors, pending) = await multi.ReadSingleAsync<(int?, int?)>();
        var jobs = (await multi.ReadAsync<JobStateItem>()).Select(j => j with
        {
            LastSuccessUtc = Utc(j.LastSuccessUtc),
            LastAttemptUtc = Utc(j.LastAttemptUtc)
        }).ToList();

        return new DashboardDto(day, byChannel.Sum(c => c.Orders), byChannel.Sum(c => c.Revenue), byChannel,
            insufficient, threshold, critical, errors ?? 0, pending ?? 0, jobs);
    }

    public async Task<PagedResult<StockOverviewRow>> GetStockOverviewAsync(string? search, bool onlyDeviations, bool lowStock, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        await using var connection = await OpenAsync(ct);
        var threshold = await ThresholdAsync(connection, ct);
        var term = Text.Clean(search);

        // Sapma filtresi hesaplanan değere bağlı olduğundan önce aday SKU'lar (son mutabakatı olanlar) bellekte süzülür.
        var skus = (await connection.QueryAsync<SkuRow>(new CommandDefinition("""
            SELECT s.Id AS SkuId, s.SkuKodu AS SkuCode, p.Ad AS ProductName,
                   ISNULL(SUM(b.Fiziksel), 0) AS Physical, ISNULL(SUM(b.Rezerve), 0) AS Reserved,
                   ISNULL(SUM(b.GuvenlikStogu), 0) AS SafetyStock, ISNULL(SUM(b.Satilabilir), 0) AS Available
              FROM dbo.Sku s
              JOIN dbo.Product p ON p.Id = s.ProductId
              LEFT JOIN dbo.StockBalance b ON b.SkuId = s.Id
             WHERE (@term IS NULL OR s.SkuKodu LIKE @prefix ESCAPE '\' OR s.Barkod = @term OR p.Ad LIKE @contains)
               AND (@onlyDeviations = 0 OR EXISTS (SELECT 1 FROM dbo.ChannelListing l JOIN dbo.ChannelStockSnapshot x ON x.ListingId = l.Id WHERE l.SkuId = s.Id))
             GROUP BY s.Id, s.SkuKodu, p.Ad
            HAVING (@lowStock = 0 OR ISNULL(SUM(b.Satilabilir), 0) <= @threshold)
             ORDER BY s.SkuKodu
            """, new
            {
                term,
                prefix = term is null ? null : Escape(term) + "%",
                contains = term is null ? null : "%" + Escape(term) + "%",
                onlyDeviations,
                lowStock,
                threshold
            }, cancellationToken: ct))).ToList();

        List<SkuRow> pageRows;
        int total;
        List<StockOverviewRow> result;
        if (onlyDeviations)
        {
            var all = await BuildRowsAsync(connection, skus, ct);
            var filtered = all.Where(r => r.HasDeviation).ToList();
            total = filtered.Count;
            result = filtered.Skip((p - 1) * size).Take(size).ToList();
        }
        else
        {
            total = skus.Count;
            pageRows = skus.Skip((p - 1) * size).Take(size).ToList();
            result = await BuildRowsAsync(connection, pageRows, ct);
        }
        return new PagedResult<StockOverviewRow>(result, total, p, size);
    }

    public async Task<PagedResult<StockMovementItem>> GetMovementsAsync(MovementQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (p, size) = Paging.Normalize(query.Page, query.PageSize);
        var args = new
        {
            sku = Text.Clean(query.Sku) is { } s ? Escape(s) + "%" : null,
            type = Text.Clean(query.Type)?.ToUpperInvariant(),
            warehouseId = query.WarehouseId,
            from = query.FromUtc,
            to = query.ToUtc,
            sourceType = Text.Clean(query.SourceType)?.ToUpperInvariant(),
            skip = (p - 1) * size,
            take = size
        };
        const string where = """
            WHERE (@sku IS NULL OR s.SkuKodu LIKE @sku ESCAPE '\')
              AND (@type IS NULL OR m.Tip = @type)
              AND (@warehouseId IS NULL OR m.WarehouseId = @warehouseId)
              AND (@from IS NULL OR m.Tarih >= @from)
              AND (@to IS NULL OR m.Tarih < @to)
              AND (@sourceType IS NULL OR m.KaynakTip = @sourceType)
            """;

        await using var connection = await OpenAsync(ct);
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM dbo.StockMovement m JOIN dbo.Sku s ON s.Id = m.SkuId {where}", args, cancellationToken: ct));
        var items = (await connection.QueryAsync<StockMovementItem>(new CommandDefinition($"""
            SELECT m.Id, m.Tarih AS AtUtc, m.SkuId, s.SkuKodu AS SkuCode, w.Ad AS WarehouseName, m.Tip AS Type, m.Adet AS Quantity,
                   m.KaynakTip AS SourceType, m.KaynakId AS SourceId, m.Kullanici AS [User]
              FROM dbo.StockMovement m JOIN dbo.Sku s ON s.Id = m.SkuId JOIN dbo.Warehouse w ON w.Id = m.WarehouseId
            {where}
             ORDER BY m.Tarih DESC, m.Id DESC
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY
            """, args, cancellationToken: ct))).Select(i => i with { AtUtc = DateTime.SpecifyKind(i.AtUtc, DateTimeKind.Utc) }).ToList();
        return new PagedResult<StockMovementItem>(items, total, p, size);
    }

    public async Task<AppSettingsDto> GetSettingsAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return new AppSettingsDto(await ThresholdAsync(connection, ct));
    }

    public async Task<AppSettingsDto> UpdateSettingsAsync(AppSettingsDto settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        new Validator().Must(settings.CriticalStockThreshold is >= 0 and <= 100_000, "criticalStockThreshold", "Eşik 0 ile 100.000 arasında olmalı.").ThrowIfInvalid();

        await using var connection = await OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.AppSetting SET Deger = @value, Guncelleme = SYSUTCDATETIME() WHERE Anahtar = @key;
            IF @@ROWCOUNT = 0 INSERT dbo.AppSetting (Anahtar, Deger) VALUES (@key, @value);
            """, new { key = CriticalStockKey, value = settings.CriticalStockThreshold.ToString(CultureInfo.InvariantCulture) }, cancellationToken: ct));
        return settings;
    }

    private async Task<List<StockOverviewRow>> BuildRowsAsync(SqlConnection connection, IReadOnlyList<SkuRow> skus, CancellationToken ct)
    {
        if (skus.Count == 0)
        {
            return [];
        }

        var listings = (await connection.QueryAsync<ListingRow>(new CommandDefinition("""
            SELECT l.Id AS ListingId, l.SkuId, c.Kod AS ChannelCode, l.Durum AS Status, l.SonGonderilenStok AS LastSent,
                   snap.KanalStok AS ChannelStock, snap.CekilmeZamani AS FetchedAtUtc
              FROM dbo.ChannelListing l
              JOIN dbo.Channel c ON c.Id = l.ChannelId
              OUTER APPLY (SELECT TOP (1) x.KanalStok, x.CekilmeZamani FROM dbo.ChannelStockSnapshot x
                            WHERE x.ListingId = l.Id ORDER BY x.CekilmeZamani DESC, x.Id DESC) AS snap
             WHERE l.SkuId IN @skuIds
             ORDER BY c.Id
            """, new { skuIds = skus.Select(s => s.SkuId).ToList() }, cancellationToken: ct))).ToList();

        var targets = (await syncStore.ComputeTargetsAsync(connection, null, listings.Select(l => l.ListingId).ToList(), ct))
            .ToDictionary(t => t.ListingId, t => t.Target);

        return skus.Select(s => new StockOverviewRow(s.SkuId, s.SkuCode, s.ProductName, s.Physical, s.Reserved, s.SafetyStock, s.Available,
            listings.Where(l => l.SkuId == s.SkuId).Select(l => new ChannelStockCell(
                l.ChannelCode, l.ListingId, l.Status, targets[l.ListingId], l.LastSent, l.ChannelStock, Utc(l.FetchedAtUtc))).ToList())).ToList();
    }

    private static async Task<int> ThresholdAsync(SqlConnection connection, CancellationToken ct)
    {
        var value = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Deger FROM dbo.AppSetting WHERE Anahtar = @key", new { key = CriticalStockKey }, cancellationToken: ct));
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : 3;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    private static DateTime? Utc(DateTime? d) => d is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        return connection;
    }

    private sealed class SkuRow
    {
        public int SkuId { get; init; }
        public string SkuCode { get; init; } = "";
        public string ProductName { get; init; } = "";
        public int Physical { get; init; }
        public int Reserved { get; init; }
        public int SafetyStock { get; init; }
        public int Available { get; init; }
    }

    private sealed class ListingRow
    {
        public int ListingId { get; init; }
        public int SkuId { get; init; }
        public string ChannelCode { get; init; } = "";
        public string Status { get; init; } = "";
        public int? LastSent { get; init; }
        public int? ChannelStock { get; init; }
        public DateTime? FetchedAtUtc { get; init; }
    }
}
