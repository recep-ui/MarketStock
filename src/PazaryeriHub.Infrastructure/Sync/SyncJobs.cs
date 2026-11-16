using System.Data;
using System.Text.Json;
using Dapper;
using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Sync;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.Sync;

public sealed record OutboxRunResult(int Events, int Batches, int Enqueued);

public sealed record PushRunResult(string ChannelCode, bool Skipped, string? SkipReason, int Claimed, int Sent, int Failed, string? BatchId);

public sealed record BatchCheckRunResult(int Batches, int Completed, int Failed, int Pending, int RecoveredClaims);

/// <summary>
/// OutboxIsle (CLAUDE.md §7.2): StokDegisti/EslestirmeDegisti/KanalStokYenile olaylarını okur, etkilenen eşleştirmeler
/// için hedef stoğu hesaplar ve son gönderilenden farklıysa SyncQueue'ya yazar.
/// Olayları alma ve kuyruğa yazma TEK transaction'dadır: hata olursa olaylar işlenmemiş kalır ve tekrar denenir.
/// Alma deseni (spesifikasyon §4): TOP(100) ... WITH (ROWLOCK, UPDLOCK, READPAST) + UPDATE ... OUTPUT;
/// birden fazla worker aynı olayı almaz.
/// </summary>
public sealed class OutboxProcessorJob(
    SqlConnectionFactory connectionFactory,
    SyncStore store,
    IOptions<SyncOptions> options,
    TimeProvider clock,
    ILogger<OutboxProcessorJob> logger)
{
    public const string RecurringJobId = "outbox-isle";

    [DisableConcurrentExecution(timeoutInSeconds: 120)]
    [AutomaticRetry(Attempts = 0)]
    public async Task<OutboxRunResult> RunAsync(CancellationToken ct)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);

        int events = 0, batches = 0, enqueued = 0;
        while (batches < options.Value.OutboxMaxBatchesPerRun)
        {
            await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var claimed = (await connection.QueryAsync<(long Id, string OlayTipi, string Payload)>(new CommandDefinition("""
                WITH bekleyen AS (
                    SELECT TOP (100) Id, OlayTipi, Payload, IslendiAt
                      FROM dbo.Outbox WITH (ROWLOCK, UPDLOCK, READPAST)
                     WHERE IslendiAt IS NULL
                     ORDER BY Id)
                UPDATE bekleyen SET IslendiAt = @now
                OUTPUT inserted.Id, inserted.OlayTipi, inserted.Payload
                """, new { now = clock.GetUtcNow().UtcDateTime }, tx, cancellationToken: ct))).ToList();

            if (claimed.Count == 0)
            {
                await tx.CommitAsync(ct);
                break;
            }

            var (skuIds, listingIds, channelIds) = Parse(claimed);
            var candidates = (await connection.QueryAsync<int>(new CommandDefinition("""
                SELECT l.Id FROM dbo.ChannelListing AS l JOIN dbo.Channel AS c ON c.Id = l.ChannelId
                 WHERE l.Durum = 'AKTIF' AND c.Aktif = 1
                   AND (l.SkuId IN (SELECT CAST([value] AS INT) FROM OPENJSON(@skus))
                        OR l.Id IN (SELECT CAST([value] AS INT) FROM OPENJSON(@listings))
                        OR l.ChannelId IN (SELECT CAST([value] AS INT) FROM OPENJSON(@channels)))
                """, new
                {
                    skus = JsonSerializer.Serialize(skuIds),
                    listings = JsonSerializer.Serialize(listingIds),
                    channels = JsonSerializer.Serialize(channelIds)
                }, tx, cancellationToken: ct))).ToList();

            var targets = await store.ComputeTargetsAsync(connection, tx, candidates, ct);
            // Kanal yenileme isteği gelen kanallarda değer aynı olsa bile gönderilir (kanaldaki değer kaymış olabilir).
            var toQueue = targets
                .Where(t => t.Syncable && (t.Target != t.LastSent || channelIds.Contains(t.ChannelId) || listingIds.Contains(t.ListingId)))
                .Select(t => t.ListingId).ToList();
            enqueued += await SyncStore.EnqueueAsync(connection, tx, toQueue, ct);

            await tx.CommitAsync(ct);
            events += claimed.Count;
            batches++;
        }

        if (events > 0)
        {
            logger.LogInformation("OutboxIsle: {Olay} olay işlendi ({Parti} parti), {Kuyruk} eşleştirme kuyruğa alındı.", events, batches, enqueued);
        }
        return new OutboxRunResult(events, batches, enqueued);
    }

    private (HashSet<int> Skus, HashSet<int> Listings, HashSet<int> Channels) Parse(IEnumerable<(long Id, string OlayTipi, string Payload)> events)
    {
        HashSet<int> skus = [], listings = [], channels = [];
        foreach (var e in events)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.Payload);
                var root = doc.RootElement;
                switch (e.OlayTipi)
                {
                    case OutboxEvents.StockChanged when root.TryGetProperty("SkuId", out var s) && s.TryGetInt32(out var skuId):
                        skus.Add(skuId);
                        break;
                    case OutboxEvents.ListingChanged when root.TryGetProperty("ListingId", out var l) && l.TryGetInt32(out var listingId):
                        listings.Add(listingId);
                        break;
                    case OutboxEvents.ChannelRefresh when root.TryGetProperty("ChannelId", out var c) && c.TryGetInt32(out var channelId):
                        channels.Add(channelId);
                        break;
                    default:
                        logger.LogWarning("Outbox olayı {OlayId} ({OlayTipi}) tanınmadı; işlendi sayıldı.", e.Id, e.OlayTipi);
                        break;
                }
            }
            catch (JsonException)
            {
                logger.LogWarning("Outbox olayı {OlayId} okunamadı; işlendi sayıldı.", e.Id);
            }
        }
        return (skus, listings, channels);
    }
}

/// <summary>
/// StokGonder (CLAUDE.md §7.3), kanal başına: bekleyen STOK kayıtlarını talep eder, değeri gönderim anında en güncel
/// stoktan hesaplar, adaptörle toplu gönderir ve BatchId'yi kaydeder. Senkron (BatchId'siz) yanıtlar hemen GONDERILDI olur.
/// </summary>
public sealed class StockPushJob(
    IEnumerable<IChannelAdapter> adapters,
    SqlConnectionFactory connectionFactory,
    SyncStore store,
    IOptions<SyncOptions> options,
    TimeProvider clock,
    ILogger<StockPushJob> logger)
{
    public static string RecurringJobId(string channelCode) => "stok-gonder-" + channelCode.ToLowerInvariant();

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [AutomaticRetry(Attempts = 0)]
    public async Task<PushRunResult> RunAsync(string channelCode, CancellationToken ct)
    {
        var adapter = adapters.FirstOrDefault(a => a.ChannelCode == channelCode);
        if (adapter is null)
        {
            return new(channelCode, true, "Adaptör kayıtlı değil.", 0, 0, 0, null);
        }

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var channel = await connection.QuerySingleOrDefaultAsync<(int Id, bool Aktif)?>(new CommandDefinition(
            "SELECT Id, Aktif FROM dbo.Channel WHERE Kod = @channelCode", new { channelCode }, cancellationToken: ct));
        if (channel is not { Aktif: true })
        {
            return new(channelCode, true, "Kanal pasif.", 0, 0, 0, null);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var claimToken = SyncStore.ClaimPrefix + Guid.NewGuid().ToString("N");
        var claimed = (await connection.QueryAsync<(long Id, int ListingId, int Deneme)>(new CommandDefinition("""
            WITH gonderilecek AS (
                SELECT TOP (@take) q.Id, q.ListingId, q.BatchId, q.SonDeneme, q.Deneme
                  FROM dbo.SyncQueue AS q WITH (ROWLOCK, UPDLOCK, READPAST)
                 WHERE q.Tip = 'STOK' AND q.Durum = 'BEKLIYOR' AND q.BatchId IS NULL
                   AND (q.SonrakiDeneme IS NULL OR q.SonrakiDeneme <= @now)
                   AND q.ListingId IN (SELECT l.Id FROM dbo.ChannelListing AS l WHERE l.ChannelId = @channelId)
                 ORDER BY q.Id)
            UPDATE gonderilecek SET BatchId = @claimToken, SonDeneme = @now, Deneme = Deneme + 1
            OUTPUT inserted.Id, inserted.ListingId, inserted.Deneme
            """, new { take = options.Value.PushChunkSize, now, channelId = channel.Value.Id, claimToken }, cancellationToken: ct))).ToList();

        if (claimed.Count == 0)
        {
            return new(channelCode, false, null, 0, 0, 0, null);
        }

        var targets = (await store.ComputeTargetsAsync(connection, null, claimed.Select(c => c.ListingId).ToList(), ct))
            .ToDictionary(t => t.ListingId);

        // Kodu/barkodu olmayan eşleştirme kanala gönderilemez: denemeye gerek yok, doğrudan HATA.
        var noCode = claimed.Where(c => targets[c.ListingId] is { ChannelProductCode: null, ChannelBarcode: null }).ToList();
        if (noCode.Count > 0)
        {
            var exhausted = noCode.Select(c => (c.Id, options.Value.MaxAttempts)).ToList();
            await store.MarkFailedAsync(connection, exhausted, "Eşleştirmede kanal ürün kodu veya barkodu yok.", options.Value, now, ct);
        }

        var sendable = claimed.Except(noCode).ToList();
        if (sendable.Count == 0)
        {
            return new(channelCode, false, null, claimed.Count, 0, claimed.Count, null);
        }

        var updates = sendable.Select(c => targets[c.ListingId])
            .Select(t => new StockUpdate(t.ChannelProductCode, t.ChannelBarcode, t.Target)).ToList();

        BatchResult result;
        try
        {
            result = await adapter.PushStockAsync(updates, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "StokGonder {Kanal}: gönderim başarısız ({Adet} kalem).", channelCode, sendable.Count);
            await store.MarkFailedAsync(connection, sendable.Select(c => (c.Id, c.Deneme)).ToList(), SimplifyError(ex), options.Value, now, ct);
            return new(channelCode, false, null, claimed.Count, 0, claimed.Count, null);
        }

        if (!result.Accepted)
        {
            await store.MarkFailedAsync(connection, sendable.Select(c => (c.Id, c.Deneme)).ToList(),
                result.Error ?? "Kanal gönderimi kabul etmedi.", options.Value, now, ct);
            return new(channelCode, false, null, claimed.Count, 0, claimed.Count, null);
        }

        if (result.BatchId is null)
        {
            await store.MarkSentAsync(connection, sendable.Select(c => (c.Id, c.ListingId, targets[c.ListingId].Target)).ToList(), now, ct);
        }
        else
        {
            var payload = JsonSerializer.Serialize(sendable.Select(c => new { q = c.Id, v = targets[c.ListingId].Target }));
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE q SET q.BatchId = @batchId, q.GonderilenDeger = j.v
                  FROM dbo.SyncQueue AS q JOIN OPENJSON(@payload) WITH (q BIGINT, v INT) AS j ON j.q = q.Id
                 WHERE q.BatchId = @claimToken
                """, new { batchId = result.BatchId, payload, claimToken }, cancellationToken: ct));
        }

        logger.LogInformation("StokGonder {Kanal}: {Adet} kalem gönderildi, batch {BatchId}.", channelCode, sendable.Count, result.BatchId ?? "(senkron)");
        return new(channelCode, false, null, claimed.Count, sendable.Count, noCode.Count, result.BatchId);
    }

    /// <summary>Ham hata metni kullanıcıya gösterilmeden önce sadeleştirilir; ayrıntı loglardadır.</summary>
    internal static string SimplifyError(Exception ex) => ex switch
    {
        ChannelApiException api => api.Message, // adaptör zaten sadeleştirdi
        NotSupportedException => ex.Message,
        HttpRequestException { StatusCode: { } code } => $"Kanal hata döndü (HTTP {(int)code}): {ex.Message}",
        HttpRequestException => $"Kanala ulaşılamadı: {ex.Message}",
        TaskCanceledException or TimeoutException => "Kanal zamanında yanıt vermedi.",
        _ => $"Gönderim başarısız: {ex.Message}"
    };
}

/// <summary>
/// BatchSorgula (CLAUDE.md §7.4): gönderilmiş toplu işlemlerin sonucunu sorgular; başarılıları GONDERILDI yapar,
/// başarısızları üstel geri çekilme ile yeniden kuyruğa alır (5 denemeden sonra HATA). Ayrıca takılı kalmış talepleri kurtarır.
/// </summary>
public sealed class BatchCheckJob(
    IEnumerable<IChannelAdapter> adapters,
    SqlConnectionFactory connectionFactory,
    SyncStore store,
    IOptions<SyncOptions> options,
    TimeProvider clock,
    ILogger<BatchCheckJob> logger)
{
    public const string RecurringJobId = "batch-sorgula";

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [AutomaticRetry(Attempts = 0)]
    public async Task<BatchCheckRunResult> RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);

        // Gönderilemeden kalan talepler (süreç çöktü vb.) serbest bırakılır.
        var recovered = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.SyncQueue SET BatchId = NULL
             WHERE Durum = 'BEKLIYOR' AND BatchId LIKE 'claim:%' AND SonDeneme < @limit
            """, new { limit = now.AddMinutes(-options.Value.ClaimTimeoutMinutes) }, cancellationToken: ct));

        var rows = (await connection.QueryAsync<InFlightRow>(new CommandDefinition("""
            SELECT q.Id, q.ListingId, q.BatchId, q.GonderilenDeger, q.Deneme, q.SonDeneme, c.Kod AS ChannelCode, l.KanalUrunKodu, l.KanalBarkod
              FROM dbo.SyncQueue AS q
              JOIN dbo.ChannelListing AS l ON l.Id = q.ListingId
              JOIN dbo.Channel AS c ON c.Id = l.ChannelId
             WHERE q.Durum = 'BEKLIYOR' AND q.BatchId IS NOT NULL AND q.BatchId NOT LIKE 'claim:%'
            """, cancellationToken: ct))).ToList();

        int completed = 0, failed = 0, pending = 0;
        var batches = rows.GroupBy(r => (r.ChannelCode, r.BatchId)).ToList();
        foreach (var batch in batches)
        {
            var adapter = adapters.FirstOrDefault(a => a.ChannelCode == batch.Key.ChannelCode);
            if (adapter is null)
            {
                continue;
            }

            BatchStatus status;
            try
            {
                status = await adapter.CheckBatchAsync(batch.Key.BatchId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "BatchSorgula: {Kanal} batch {BatchId} sorgulanamadı; sonraki çalışmada denenecek.", batch.Key.ChannelCode, batch.Key.BatchId);
                pending++;
                continue;
            }

            var items = batch.ToList();
            switch (status.State)
            {
                case BatchState.Pending when items.Min(i => i.SonDeneme) >= now.AddMinutes(-options.Value.BatchTimeoutMinutes):
                    pending++;
                    break;

                case BatchState.Pending:
                    await store.MarkFailedAsync(connection, items.Select(i => (i.Id, i.Deneme)).ToList(),
                        $"Kanal {options.Value.BatchTimeoutMinutes} dakika içinde sonuç vermedi.", options.Value, now, ct);
                    failed++;
                    break;

                case BatchState.Completed:
                    await store.MarkSentAsync(connection, items.Select(i => (i.Id, i.ListingId, i.GonderilenDeger ?? 0)).ToList(), now, ct);
                    completed++;
                    break;

                case BatchState.Failed:
                    await store.MarkFailedAsync(connection, items.Select(i => (i.Id, i.Deneme)).ToList(),
                        status.Errors.FirstOrDefault()?.Message ?? "Kanal toplu işlemi reddetti.", options.Value, now, ct);
                    failed++;
                    break;

                case BatchState.PartiallyFailed:
                {
                    var bad = items.Select(i => (Item: i, Error: status.Errors.FirstOrDefault(e =>
                            (e.ChannelProductCode is not null && e.ChannelProductCode == i.KanalUrunKodu) ||
                            (e.Barcode is not null && e.Barcode == i.KanalBarkod))))
                        .Where(x => x.Error is not null).ToList();
                    foreach (var (item, error) in bad)
                    {
                        await store.MarkFailedAsync(connection, [(item.Id, item.Deneme)], error!.Message, options.Value, now, ct);
                    }
                    var good = items.Except(bad.Select(b => b.Item)).ToList();
                    await store.MarkSentAsync(connection, good.Select(i => (i.Id, i.ListingId, i.GonderilenDeger ?? 0)).ToList(), now, ct);
                    completed++;
                    break;
                }
            }
        }

        return new BatchCheckRunResult(batches.Count, completed, failed, pending, recovered);
    }

    private sealed class InFlightRow
    {
        public long Id { get; init; }
        public int ListingId { get; init; }
        public string BatchId { get; init; } = "";
        public int? GonderilenDeger { get; init; }
        public int Deneme { get; init; }
        public DateTime SonDeneme { get; init; }
        public string ChannelCode { get; init; } = "";
        public string? KanalUrunKodu { get; init; }
        public string? KanalBarkod { get; init; }
    }
}

/// <summary>
/// Mutabakat (CLAUDE.md §7.5), kanal başına: kanaldaki stokları çeker, ChannelStockSnapshot'a yazar ve
/// beklenen (hesaplanan) değerle karşılaştırır. Rapor <see cref="ISyncService.GetReconciliationAsync"/> ile alınır.
/// </summary>
public sealed class ReconciliationJob(
    IEnumerable<IChannelAdapter> adapters,
    SqlConnectionFactory connectionFactory,
    SyncStore store,
    TimeProvider clock,
    ILogger<ReconciliationJob> logger)
{
    public const string JobType = "MUTABAKAT";

    public static string RecurringJobId(string channelCode) => "mutabakat-" + channelCode.ToLowerInvariant();

    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    [AutomaticRetry(Attempts = 2)]
    public async Task<ReconciliationResult> RunAsync(string channelCode, CancellationToken ct)
    {
        var adapter = adapters.FirstOrDefault(a => a.ChannelCode == channelCode);
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var channel = await connection.QuerySingleOrDefaultAsync<(int Id, bool Aktif)?>(new CommandDefinition(
            "SELECT Id, Aktif FROM dbo.Channel WHERE Kod = @channelCode", new { channelCode }, cancellationToken: ct));
        if (adapter is null || channel is not { Aktif: true })
        {
            return new(channelCode, true, adapter is null ? "Adaptör kayıtlı değil." : "Kanal pasif.", 0, 0, 0, 0);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var listingIds = (await connection.QueryAsync<int>(new CommandDefinition(
            "SELECT Id FROM dbo.ChannelListing WHERE ChannelId = @channelId AND Durum = 'AKTIF'",
            new { channelId = channel.Value.Id }, cancellationToken: ct))).ToList();
        var targets = await store.ComputeTargetsAsync(connection, null, listingIds, ct);

        var keys = targets.Select(t => t.ChannelProductCode ?? t.ChannelBarcode).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        IReadOnlyList<ChannelStock> channelStocks;
        try
        {
            channelStocks = await adapter.FetchStockAsync(keys, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SaveStateAsync(connection, channel.Value.Id, now, $"Kanal stokları çekilemedi: {ex.Message}", ct);
            throw;
        }

        var byCode = channelStocks.Where(s => s.ChannelProductCode is not null).GroupBy(s => s.ChannelProductCode!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Quantity, StringComparer.Ordinal);
        var byBarcode = channelStocks.Where(s => s.Barcode is not null).GroupBy(s => s.Barcode!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Quantity, StringComparer.Ordinal);

        var snapshots = new List<(int ListingId, int Quantity)>();
        var deviations = 0;
        foreach (var t in targets)
        {
            int? quantity = t.ChannelProductCode is not null && byCode.TryGetValue(t.ChannelProductCode, out var q1) ? q1
                : t.ChannelBarcode is not null && byBarcode.TryGetValue(t.ChannelBarcode, out var q2) ? q2
                : null;
            if (quantity is { } q)
            {
                snapshots.Add((t.ListingId, q));
                if (q != t.Target)
                {
                    deviations++;
                }
            }
        }

        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT dbo.ChannelStockSnapshot (ListingId, KanalStok, CekilmeZamani)
            SELECT l, k, @now FROM OPENJSON(@payload) WITH (l INT, k INT)
            """, new { now, payload = JsonSerializer.Serialize(snapshots.Select(s => new { l = s.ListingId, k = s.Quantity })) }, cancellationToken: ct));
        await SaveStateAsync(connection, channel.Value.Id, now, null, ct);

        var missing = targets.Count - snapshots.Count;
        logger.LogInformation("Mutabakat {Kanal}: {Eslestirme} eşleştirme, {Bulunan} kanalda bulundu, {Eksik} bulunamadı, {Sapma} sapma.",
            channelCode, targets.Count, snapshots.Count, missing, deviations);
        if (deviations > 0 || missing > 0)
        {
            logger.LogWarning("UYARI: {Kanal} mutabakatında {Sapma} sapma ve {Eksik} kanalda bulunamayan eşleştirme var.", channelCode, deviations, missing);
        }

        return new(channelCode, false, null, targets.Count, snapshots.Count, missing, deviations);
    }

    private static Task SaveStateAsync(SqlConnection connection, int channelId, DateTime now, string? error, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.ChannelJobState
               SET SonDeneme = @now, SonBasarili = CASE WHEN @error IS NULL THEN @now ELSE SonBasarili END,
                   SonHata = @error, ArdisikHata = CASE WHEN @error IS NULL THEN 0 ELSE ArdisikHata + 1 END
             WHERE ChannelId = @channelId AND IsTipi = 'MUTABAKAT';
            IF @@ROWCOUNT = 0
                INSERT dbo.ChannelJobState (ChannelId, IsTipi, SonBasarili, SonDeneme, SonHata, ArdisikHata)
                VALUES (@channelId, 'MUTABAKAT', CASE WHEN @error IS NULL THEN @now END, @now, @error, CASE WHEN @error IS NULL THEN 0 ELSE 1 END);
            """, new { channelId, now, error }, cancellationToken: ct));
}
