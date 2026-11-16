using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PazaryeriHub.Application.Sync;
using PazaryeriHub.Domain.Stock;

namespace PazaryeriHub.Infrastructure.Sync;

/// <summary>Bir eşleştirme için kanala gidecek stok (CLAUDE.md §5).</summary>
public sealed record ListingTarget(
    int ListingId, int SkuId, int ChannelId, string ChannelCode, string? ChannelProductCode, string? ChannelBarcode,
    int? LastSent, int Target, bool Syncable);

/// <summary>Senkron job'larının ortak veritabanı işlemleri (Dapper).</summary>
public sealed class SyncStore(ILogger<SyncStore> logger)
{
    public const string ClaimPrefix = "claim:";

    /// <summary>
    /// Eşleştirmelerin hedef stoklarını hesaplar: Σ Satilabilir (tüm depolar) → <see cref="StockAllocator"/>
    /// (Channel.PayYuzde, Channel.Ayarlar.stokUstLimit). Syncable = eşleştirme AKTIF ve kanal aktif.
    /// </summary>
    public async Task<IReadOnlyList<ListingTarget>> ComputeTargetsAsync(SqlConnection connection, SqlTransaction? tx, IReadOnlyCollection<int> listingIds, CancellationToken ct)
    {
        if (listingIds.Count == 0)
        {
            return [];
        }

        var rows = await connection.QueryAsync<TargetRow>(new CommandDefinition("""
            SELECT l.Id AS ListingId, l.SkuId, l.ChannelId, c.Kod AS ChannelCode, l.KanalUrunKodu, l.KanalBarkod,
                   l.SonGonderilenStok, c.PayYuzde, c.Ayarlar, CAST(CASE WHEN l.Durum = 'AKTIF' AND c.Aktif = 1 THEN 1 ELSE 0 END AS bit) AS Syncable,
                   ISNULL((SELECT SUM(CAST(b.Satilabilir AS BIGINT)) FROM dbo.StockBalance b WHERE b.SkuId = l.SkuId), 0) AS TotalAvailable
              FROM dbo.ChannelListing AS l
              JOIN dbo.Channel AS c ON c.Id = l.ChannelId
             WHERE l.Id IN (SELECT CAST([value] AS INT) FROM OPENJSON(@ids))
            """, new { ids = JsonSerializer.Serialize(listingIds) }, tx, cancellationToken: ct));

        return rows.Select(r => new ListingTarget(
            r.ListingId, r.SkuId, r.ChannelId, r.ChannelCode, r.KanalUrunKodu, r.KanalBarkod, r.SonGonderilenStok,
            StockAllocator.Allocate(r.TotalAvailable, r.PayYuzde, ReadCap(r.Ayarlar, r.ChannelCode)), r.Syncable)).ToList();
    }

    /// <summary>
    /// Eşleştirmeler için bekleyen STOK kaydı açar (debounce: zaten bekleyen varsa açılmaz).
    /// UPDLOCK + HOLDLOCK eşzamanlı iki işlemcinin aynı kaydı açmasını engeller; yine de çakışırsa
    /// UX_SyncQueue_Listing_Tip_Bekleyen (2601) son savunmadır.
    /// </summary>
    public static async Task<int> EnqueueAsync(SqlConnection connection, SqlTransaction? tx, IReadOnlyCollection<int> listingIds, CancellationToken ct)
    {
        if (listingIds.Count == 0)
        {
            return 0;
        }
        return await connection.ExecuteAsync(new CommandDefinition("""
            INSERT dbo.SyncQueue (ListingId, Tip, Durum)
            SELECT DISTINCT CAST(j.[value] AS INT), 'STOK', 'BEKLIYOR'
              FROM OPENJSON(@ids) AS j
             WHERE NOT EXISTS (SELECT 1 FROM dbo.SyncQueue AS q WITH (UPDLOCK, HOLDLOCK)
                               WHERE q.ListingId = CAST(j.[value] AS INT) AND q.Tip = 'STOK' AND q.Durum = 'BEKLIYOR')
            """, new { ids = JsonSerializer.Serialize(listingIds) }, tx, cancellationToken: ct));
    }

    /// <summary>Başarılı gönderim: kayıt GONDERILDI, eşleştirmenin son gönderilen stoğu güncellenir; sonra değer değiştiyse yeniden kuyruğa alınır.</summary>
    public async Task MarkSentAsync(SqlConnection connection, IReadOnlyCollection<(long QueueId, int ListingId, int Value)> items, DateTime now, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return;
        }

        await using (var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            var payload = JsonSerializer.Serialize(items.Select(i => new { q = i.QueueId, l = i.ListingId, v = i.Value }));
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE q SET q.Durum = 'GONDERILDI', q.Hata = NULL, q.GonderilenDeger = j.v
                  FROM dbo.SyncQueue AS q
                  JOIN OPENJSON(@payload) WITH (q BIGINT, l INT, v INT) AS j ON j.q = q.Id;

                UPDATE l SET l.SonGonderilenStok = j.v, l.SonSenkron = @now
                  FROM dbo.ChannelListing AS l
                  JOIN OPENJSON(@payload) WITH (q BIGINT, l INT, v INT) AS j ON j.l = l.Id;
                """, new { payload, now }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }

        // Gönderim sürerken stok değiştiyse (o sırada debounce yeni kaydı engellemişti) yeni değer kuyruğa girer.
        var targets = await ComputeTargetsAsync(connection, null, items.Select(i => i.ListingId).Distinct().ToList(), ct);
        var changed = targets.Where(t => t.Syncable && t.Target != t.LastSent).Select(t => t.ListingId).ToList();
        if (changed.Count > 0)
        {
            await EnqueueAsync(connection, null, changed, ct);
        }
    }

    /// <summary>
    /// Başarısız deneme: <see cref="RetryPolicy"/>'e göre ya üstel bekleme ile yeniden denenecek şekilde serbest bırakılır
    /// ya da (deneme sınırı dolduysa) HATA yapılır ve uyarı loglanır. Deneme sayacı talep sırasında artırılmıştır.
    /// </summary>
    public async Task MarkFailedAsync(SqlConnection connection, IReadOnlyCollection<(long QueueId, int Attempts)> items, string error, SyncOptions options, DateTime now, CancellationToken ct)
    {
        var message = Truncate(error, 2000);
        foreach (var (queueId, attempts) in items)
        {
            var delay = RetryPolicy.NextDelay(attempts, options);
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE dbo.SyncQueue
                   SET Hata = @message,
                       BatchId = NULL,
                       Durum = CASE WHEN @final = 1 THEN 'HATA' ELSE 'BEKLIYOR' END,
                       SonrakiDeneme = @next
                 WHERE Id = @queueId AND Durum = 'BEKLIYOR'
                """, new { queueId, message, final = delay is null, next = delay is { } d ? now + d : (DateTime?)null }, cancellationToken: ct));

            if (delay is null)
            {
                logger.LogWarning("UYARI: senkron kaydı {KuyrukId} {Deneme} denemede gönderilemedi ve HATA durumuna alındı: {Hata}",
                    queueId, attempts, message);
            }
        }
    }

    private int? ReadCap(string? settingsJson, string channelCode)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("stokUstLimit", out var cap)
                && cap.ValueKind == JsonValueKind.Number && cap.TryGetInt32(out var value))
            {
                if (value >= 0)
                {
                    return value;
                }
                logger.LogWarning("{Kanal} için stokUstLimit negatif ({Deger}); yok sayıldı.", channelCode, value);
            }
        }
        catch (JsonException)
        {
            logger.LogWarning("{Kanal} kanal ayarları okunamadı; üst limit uygulanmadı.", channelCode);
        }
        return null;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private sealed class TargetRow
    {
        public int ListingId { get; init; }
        public int SkuId { get; init; }
        public int ChannelId { get; init; }
        public string ChannelCode { get; init; } = "";
        public string? KanalUrunKodu { get; init; }
        public string? KanalBarkod { get; init; }
        public int? SonGonderilenStok { get; init; }
        public decimal PayYuzde { get; init; }
        public string? Ayarlar { get; init; }
        public bool Syncable { get; init; }
        public long TotalAvailable { get; init; }
    }
}
