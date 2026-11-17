using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Stock;
using PazaryeriHub.Application.Sync;
using PazaryeriHub.Infrastructure.Persistence;
using PazaryeriHub.Infrastructure.Sync;

namespace PazaryeriHub.Infrastructure.Stock;

/// <summary>
/// Kanal stoğunu deftere alma (ADR-024). Önizleme son mutabakat görüntülerinden SKU bazlı tablo üretir; uygulama
/// <see cref="IStockLedger.TakeChannelStockAsync"/> ile her satırı kendi transaction'ında SAYIM (KANAL_ALIM) olarak yazar
/// ve ardından SKU'ların tüm aktif ilanlarını mevcut senkron hattından zorla gönderir (tüm kanallarda aynı değer).
/// Tek depo modeli (ADR-015): birden fazla depo varsa özellik kapalıdır.
/// </summary>
public sealed class ChannelIntakeService(
    SqlConnectionFactory connectionFactory,
    SyncStore syncStore,
    IStockLedger ledger,
    ISyncService sync,
    TimeProvider clock,
    ILogger<ChannelIntakeService> logger) : IChannelIntakeService
{
    public const string SourceType = "KANAL_ALIM";
    public const int MaxRows = 5_000;

    public async Task<IntakePreview> PreviewAsync(int sourceChannelId, bool onlyDifferences, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var channel = await ChannelAsync(connection, sourceChannelId, ct);
        var (warehouseId, disabled) = await WarehouseAsync(connection, ct);
        var threshold = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TRY_CAST(Deger AS INT) FROM dbo.AppSetting WHERE Anahtar = @key", new { key = StockSettingKeys.LowStockThreshold }, cancellationToken: ct)) ?? 0;
        var previewedAt = DateTime.SpecifyKind(await connection.ExecuteScalarAsync<DateTime>(
            new CommandDefinition("SELECT CAST(SYSUTCDATETIME() AS DATETIME2(3))", cancellationToken: ct)), DateTimeKind.Utc);
        if (disabled is not null)
        {
            return new IntakePreview(sourceChannelId, channel.Kod, false, disabled, threshold, previewedAt, []);
        }

        var listings = (await connection.QueryAsync<ListingRow>(new CommandDefinition("""
            SELECT l.Id AS ListingId, l.SkuId, c.Kod AS ChannelCode, l.ChannelId, l.KanalBarkod, l.KanalUrunKodu,
                   snap.KanalStok, snap.CekilmeZamani,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.SyncQueue q WHERE q.ListingId = l.Id AND q.Tip = 'STOK' AND q.Durum = 'BEKLIYOR') THEN 1 ELSE 0 END AS bit) AS SyncPending
              FROM dbo.ChannelListing l
              JOIN dbo.Channel c ON c.Id = l.ChannelId
              OUTER APPLY (SELECT TOP (1) x.KanalStok, x.CekilmeZamani FROM dbo.ChannelStockSnapshot x
                            WHERE x.ListingId = l.Id ORDER BY x.CekilmeZamani DESC, x.Id DESC) AS snap
             WHERE l.Durum = 'AKTIF'
               AND l.SkuId IN (SELECT s.SkuId FROM dbo.ChannelListing s WHERE s.ChannelId = @channelId AND s.Durum = 'AKTIF')
             ORDER BY l.SkuId, c.Id, l.Id
            """, new { channelId = sourceChannelId }, cancellationToken: ct))).ToList();
        var skuIds = listings.Select(l => l.SkuId).Distinct().ToList();
        if (skuIds.Count == 0)
        {
            return new IntakePreview(sourceChannelId, channel.Kod, true, null, threshold, previewedAt, []);
        }

        var skus = (await connection.QueryAsync<SkuRow>(new CommandDefinition("""
            SELECT s.Id AS SkuId, s.SkuKodu, p.Ad AS ProductName,
                   ISNULL(b.Fiziksel, 0) AS Physical, ISNULL(b.Rezerve, 0) AS Reserved, ISNULL(b.GuvenlikStogu, 0) AS SafetyStock,
                   ISNULL(b.Satilabilir, 0) AS Available,
                   -- Satılabilir stoğu artıran / fizikseli değiştiren hareketler (rezervasyon ve sevk formülde zaten doğru hesaplanır)
                   (SELECT MAX(m.Tarih) FROM dbo.StockMovement m
                     WHERE m.SkuId = s.Id AND m.Tip IN ('GIRIS', 'IADE_GIRIS', 'SAYIM', 'REZERVE_IPTAL')) AS LastMovementUtc
              FROM dbo.Sku s JOIN dbo.Product p ON p.Id = s.ProductId
              LEFT JOIN dbo.StockBalance b ON b.SkuId = s.Id AND b.WarehouseId = @warehouseId
             WHERE s.Id IN (SELECT CAST([value] AS INT) FROM OPENJSON(@ids))
             ORDER BY s.SkuKodu
            """, new { warehouseId, ids = JsonSerializer.Serialize(skuIds) }, cancellationToken: ct))).ToList();
        var targets = (await syncStore.ComputeTargetsAsync(connection, null, listings.Select(l => l.ListingId).ToList(), ct))
            .ToDictionary(t => t.ListingId, t => t.Target);
        var cap = ReadCap(channel.Ayarlar);
        var now = clock.GetUtcNow().UtcDateTime;

        // SKU başına kaynak çekim anı (kaynak ilanların en eskisi) ve o andan sonraki net rezervasyon
        var snapshotAt = listings.Where(l => l.ChannelId == sourceChannelId).GroupBy(l => l.SkuId)
            .Where(g => g.All(l => l.CekilmeZamani is not null))
            .ToDictionary(g => g.Key, g => g.Min(l => l.CekilmeZamani!.Value));
        var reservedSince = snapshotAt.Count == 0 ? [] : (await connection.QueryAsync<(int SkuId, int D)>(new CommandDefinition("""
            SELECT j.SkuId, ISNULL(SUM(CASE m.Tip WHEN 'REZERVE' THEN m.Adet WHEN 'REZERVE_IPTAL' THEN -m.Adet END), 0) AS D
              FROM OPENJSON(@pairs) WITH (SkuId INT, T DATETIME2(3)) AS j
              LEFT JOIN dbo.StockMovement AS m
                ON m.SkuId = j.SkuId AND m.WarehouseId = @warehouseId AND m.Tarih > j.T AND m.Tip IN ('REZERVE', 'REZERVE_IPTAL')
             GROUP BY j.SkuId
            """, new { warehouseId, pairs = JsonSerializer.Serialize(snapshotAt.Select(kv => new { SkuId = kv.Key, T = kv.Value })) },
            cancellationToken: ct))).ToDictionary(x => x.SkuId, x => x.D);

        var rows = new List<IntakePreviewRow>(skus.Count);
        foreach (var s in skus)
        {
            var own = listings.Where(l => l.SkuId == s.SkuId).ToList();
            var source = own.Where(l => l.ChannelId == sourceChannelId).ToList();
            var d = reservedSince.GetValueOrDefault(s.SkuId);
            var eval = ChannelIntakeRules.Evaluate(
                source.Select(l => l.KanalStok).ToList(), own.Select(l => l.KanalStok).ToList(), s.Available, d,
                channel.PayYuzde, cap, threshold,
                snapshotAt.TryGetValue(s.SkuId, out var at) ? at : null,
                s.LastMovementUtc, source.Any(l => l.SyncPending), now);
            if (onlyDifferences && eval.Flags.Contains(IntakeFlags.NoDifference))
            {
                continue;
            }
            rows.Add(new IntakePreviewRow(s.SkuId, s.SkuKodu, s.ProductName, s.Physical, s.Reserved, s.SafetyStock, s.Available,
                own.Select(l => new IntakeChannelValue(l.ListingId, l.ChannelCode, l.KanalBarkod, l.KanalUrunKodu, l.KanalStok,
                    Utc(l.CekilmeZamani), targets[l.ListingId], l.SyncPending)).ToList(),
                eval.Value, eval.MinValue, d, eval.Target, ChannelIntakeRules.ProposedPhysical(eval.Target, s.Reserved, s.SafetyStock),
                eval.Flags, eval.DefaultSelected));
        }
        return new IntakePreview(sourceChannelId, channel.Kod, true, null, threshold, previewedAt, rows);
    }

    public async Task<IntakeApplyResult> ApplyAsync(IntakeApplyRequest request, string? user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rows = request.Rows ?? [];
        new Validator()
            .Must(rows.Count > 0, "rows", "En az bir satır seçilmeli.")
            .Must(rows.Count <= MaxRows, "rows", $"Tek seferde en fazla {MaxRows:N0} satır uygulanabilir.")
            .Must(rows.All(r => r.TargetAvailable >= 0), "rows", "Hedef satılabilir stok negatif olamaz.")
            .Must(rows.Select(r => r.SkuId).Distinct().Count() == rows.Count, "rows", "Aynı SKU birden fazla satırda.")
            .Must(request.PreviewedAtUtc is not null, "previewedAtUtc", "Önizleme zamanı zorunlu; önce önizleyin.")
            .Must(request.PreviewedAtUtc is not { } pv || pv > clock.GetUtcNow().UtcDateTime.AddHours(-24), "previewedAtUtc",
                "Önizleme 24 saatten eski; yeniden önizleyin.")
            .ThrowIfInvalid();

        string channelCode;
        int warehouseId;
        Dictionary<int, string> skuCodes;
        await using (var connection = await OpenAsync(ct))
        {
            channelCode = (await ChannelAsync(connection, request.SourceChannelId, ct)).Kod;
            var (wh, disabled) = await WarehouseAsync(connection, ct);
            if (disabled is not null)
            {
                throw new ConflictException(disabled);
            }
            warehouseId = wh;
            skuCodes = (await connection.QueryAsync<(int Id, string SkuKodu)>(new CommandDefinition(
                    "SELECT Id, SkuKodu FROM dbo.Sku WHERE Id IN (SELECT CAST([value] AS INT) FROM OPENJSON(@ids))",
                    new { ids = JsonSerializer.Serialize(rows.Select(r => r.SkuId)) }, cancellationToken: ct)))
                .ToDictionary(x => x.Id, x => x.SkuKodu);
        }

        var batchId = Guid.NewGuid().ToString("N")[..12];
        var sourceId = $"{SourceType}:{batchId}:{channelCode}";
        var results = new List<IntakeRowResult>(rows.Count);
        foreach (var row in rows)
        {
            if (!skuCodes.TryGetValue(row.SkuId, out var code))
            {
                results.Add(new(row.SkuId, null, IntakeOutcomes.Rejected, "SKU bulunamadı.", null, null, null));
                continue;
            }
            try
            {
                var r = await ledger.TakeChannelStockAsync(
                    new StockChangeRequest(row.SkuId, warehouseId, row.TargetAvailable, SourceType, sourceId, user),
                    row.ExpectedPhysical, request.PreviewedAtUtc, ct);
                results.Add(new(row.SkuId, code, r.MovementId == 0 ? IntakeOutcomes.Unchanged : IntakeOutcomes.Applied, null,
                    r.Physical, r.Available, r.MovementId == 0 ? null : r.MovementId));
            }
            catch (StockOperationException ex)
            {
                // Yarım hata: bu satır reddedilir, diğerleri devam eder (her satır kendi transaction'ında)
                results.Add(new(row.SkuId, code, IntakeOutcomes.Rejected, ex.Message, null, null, null));
            }
        }

        // Tüm kanallarda aynı değer: uygulanan SKU'ların tüm aktif ilanları hedef aynı olsa da yeniden gönderilir.
        var done = results.Where(r => r.Outcome != IntakeOutcomes.Rejected).Select(r => r.SkuId).ToList();
        PushListingsResult push = new(0, []);
        if (done.Count > 0)
        {
            await using var connection = await OpenAsync(ct);
            var listingIds = (await connection.QueryAsync<int>(new CommandDefinition(
                "SELECT Id FROM dbo.ChannelListing WHERE Durum = 'AKTIF' AND SkuId IN (SELECT CAST([value] AS INT) FROM OPENJSON(@ids))",
                new { ids = JsonSerializer.Serialize(done) }, cancellationToken: ct))).ToList();
            if (listingIds.Count > 0)
            {
                push = await sync.PushListingsAsync(listingIds, ct);
            }
        }

        var applied = results.Count(r => r.Outcome == IntakeOutcomes.Applied);
        var unchanged = results.Count(r => r.Outcome == IntakeOutcomes.Unchanged);
        var rejected = results.Count(r => r.Outcome == IntakeOutcomes.Rejected);
        logger.LogInformation(
            "Kanal stoğu deftere alındı: kaynak {Kanal}, parti {Parti}, kullanıcı {Kullanici}: {Uygulanan} uygulandı, {Degismeyen} fark yok, {Reddedilen} reddedildi, {Gonderilen} ilan kuyruğa alındı",
            channelCode, batchId, user, applied, unchanged, rejected, push.Queued);
        return new IntakeApplyResult(batchId, applied, unchanged, rejected, push.Queued, push.Skipped.Count, results);
    }

    // ------------------------------------------------------------------ yardımcılar

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task<ChannelRow> ChannelAsync(SqlConnection connection, int channelId, CancellationToken ct) =>
        await connection.QuerySingleOrDefaultAsync<ChannelRow>(new CommandDefinition(
            "SELECT Id, Kod, PayYuzde, Ayarlar FROM dbo.Channel WHERE Id = @channelId", new { channelId }, cancellationToken: ct))
        ?? throw new NotFoundException($"Kanal bulunamadı (Id={channelId}).");

    /// <summary>Tek depo modeli (ADR-015): yalnızca bir depo varsa çalışır.</summary>
    private static async Task<(int WarehouseId, string? Disabled)> WarehouseAsync(SqlConnection connection, CancellationToken ct)
    {
        var ids = (await connection.QueryAsync<int>(new CommandDefinition("SELECT Id FROM dbo.Warehouse", cancellationToken: ct))).ToList();
        return ids.Count == 1 ? (ids[0], null) : (0, "Kanal stoğunu deftere alma tek depo modelinde çalışır; birden fazla depo tanımlı.");
    }

    private static int? ReadCap(string? settings)
    {
        if (string.IsNullOrWhiteSpace(settings))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(settings);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("stokUstLimit", out var cap)
                   && cap.TryGetInt32(out var v) && v >= 0 ? v : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTime? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private sealed class ChannelRow
    {
        public int Id { get; init; }
        public string Kod { get; init; } = "";
        public decimal PayYuzde { get; init; }
        public string? Ayarlar { get; init; }
    }

    private sealed class ListingRow
    {
        public int ListingId { get; init; }
        public int SkuId { get; init; }
        public int ChannelId { get; init; }
        public string ChannelCode { get; init; } = "";
        public string? KanalBarkod { get; init; }
        public string? KanalUrunKodu { get; init; }
        public int? KanalStok { get; init; }
        public DateTime? CekilmeZamani { get; init; }
        public bool SyncPending { get; init; }
    }

    private sealed class SkuRow
    {
        public int SkuId { get; init; }
        public string SkuKodu { get; init; } = "";
        public string ProductName { get; init; } = "";
        public int Physical { get; init; }
        public int Reserved { get; init; }
        public int SafetyStock { get; init; }
        public int Available { get; init; }
        public DateTime? LastMovementUtc { get; init; }
    }
}
