using System.Data;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Pricing;
using PazaryeriHub.Application.Sync;
using PazaryeriHub.Domain.Pricing;
using PazaryeriHub.Infrastructure.Sync;

namespace PazaryeriHub.Infrastructure.Pricing;

/// <summary>Bir ilan için kanala gidecek fiyat ve karar girdileri (ADR-028).</summary>
public sealed record ListingPriceTarget(
    int ListingId, int SkuId, int ProductId, string SkuCode, string ProductName, int ChannelId, string ChannelCode,
    string? ChannelProductCode, string? ChannelBarcode, bool Active, decimal? LastSale, decimal? LastList, DateTime? LastPriceSyncUtc,
    decimal? Override, PriceTarget Target, long? PendingApprovalId, (decimal Sale, decimal List)? Approved, (decimal Sale, decimal List)? Rejected,
    string CapabilityState, string? ChannelData = null)
{
    public PriceSyncDecision Decide(decimal thresholdPercent) =>
        PriceSyncPolicy.Decide(Target, LastSale, LastList, thresholdPercent, CapabilityState, Approved, Rejected);
}

public sealed record PriceEvaluation(int Queued, int ApprovalsOpened, int ApprovalsClosed);

/// <summary>
/// Fiyat senkronunun veritabanı işlemleri (Dapper): hedef hesaplama (<see cref="PriceAllocator"/>), karar
/// (<see cref="PriceSyncPolicy"/>), onay kayıtları, kuyruğa alma ve başarılı gönderimin işlenmesi.
/// Kanal fiyat kuralı Channel.Ayarlar.fiyatKurali'dan okunur. Beymen gibi "beden varyantlarına aynı fiyat" isteyen kanallarda
/// aynı ürünün aktif ilanları farklı fiyata çıkıyorsa ürünün hiçbir ilanı gönderilmez.
/// </summary>
public sealed class PriceStore(IEnumerable<IChannelAdapter> adapters, ILogger<PriceStore> logger)
{
    public const string RuleProperty = "fiyatKurali";

    /// <summary>Fiyat hedeflerini hesaplar. Varyant kuralı olan kanallarda aynı ürünün diğer aktif ilanları da hesaba katılır.</summary>
    public async Task<IReadOnlyList<ListingPriceTarget>> ComputeAsync(SqlConnection connection, SqlTransaction? tx, IReadOnlyCollection<int> listingIds, CancellationToken ct)
    {
        if (listingIds.Count == 0)
        {
            return [];
        }

        var requested = listingIds.ToHashSet();
        var variantChannels = adapters.Where(a => a.DescribePriceUpdate().SamePriceForProductVariants)
            .Select(a => a.ChannelCode).Distinct(StringComparer.Ordinal).ToList();

        // Varyant kuralı: aynı kanal + aynı üründeki aktif kardeş ilanlar da hesaplanır.
        var ids = requested.ToList();
        if (variantChannels.Count > 0)
        {
            ids = (await connection.QueryAsync<int>(new CommandDefinition("""
                SELECT DISTINCT k.Id
                  FROM dbo.ChannelListing AS l
                  JOIN dbo.Channel AS c ON c.Id = l.ChannelId
                  JOIN dbo.Sku AS s ON s.Id = l.SkuId
                  JOIN dbo.Sku AS ks ON ks.ProductId = s.ProductId
                  JOIN dbo.ChannelListing AS k ON k.SkuId = ks.Id AND k.ChannelId = l.ChannelId AND k.Durum = 'AKTIF'
                 WHERE l.Id IN (SELECT CAST([value] AS INT) FROM OPENJSON(@ids))
                   AND c.Kod IN (SELECT [value] FROM OPENJSON(@codes))
                """, new { ids = JsonSerializer.Serialize(requested), codes = JsonSerializer.Serialize(variantChannels) }, tx, cancellationToken: ct)))
                .Concat(requested).Distinct().ToList();
        }

        var rows = (await connection.QueryAsync<TargetRow>(new CommandDefinition("""
            SELECT l.Id AS ListingId, l.SkuId, s.ProductId, s.SkuKodu, p.Ad AS UrunAdi, l.ChannelId, c.Kod AS ChannelCode,
                   l.KanalUrunKodu, l.KanalBarkod,
                   CAST(CASE WHEN l.Durum = 'AKTIF' AND c.Aktif = 1 THEN 1 ELSE 0 END AS bit) AS Active,
                   l.SonGonderilenSatisFiyati, l.SonGonderilenListeFiyati, l.SonFiyatSenkron, l.FiyatOverride,
                   s.ListeFiyati, s.SatisFiyati, c.Ayarlar, ISNULL(cap.Durum, 'KAPALI') AS CapabilityState,
                   pend.Id AS PendingId, appr.YeniSatisFiyati AS ApprovedSale, appr.YeniListeFiyati AS ApprovedList,
                   rej.YeniSatisFiyati AS RejectedSale, rej.YeniListeFiyati AS RejectedList, l.KanalVerisi AS ChannelData
              FROM dbo.ChannelListing AS l
              JOIN dbo.Sku AS s ON s.Id = l.SkuId
              JOIN dbo.Product AS p ON p.Id = s.ProductId
              JOIN dbo.Channel AS c ON c.Id = l.ChannelId
              LEFT JOIN dbo.ChannelCapability AS cap ON cap.ChannelId = l.ChannelId AND cap.Yetenek = 'FIYAT'
              OUTER APPLY (SELECT TOP (1) a.Id FROM dbo.PriceApproval AS a WHERE a.ListingId = l.Id AND a.Durum = 'BEKLIYOR') AS pend
              OUTER APPLY (SELECT TOP (1) a.YeniSatisFiyati, a.YeniListeFiyati FROM dbo.PriceApproval AS a
                            WHERE a.ListingId = l.Id AND a.Durum = 'ONAYLANDI' AND (l.SonFiyatSenkron IS NULL OR a.KararTarihi > l.SonFiyatSenkron)
                            ORDER BY a.KararTarihi DESC, a.Id DESC) AS appr
              OUTER APPLY (SELECT TOP (1) a.YeniSatisFiyati, a.YeniListeFiyati FROM dbo.PriceApproval AS a
                            WHERE a.ListingId = l.Id AND a.Durum = 'REDDEDILDI' AND (l.SonFiyatSenkron IS NULL OR a.KararTarihi > l.SonFiyatSenkron)
                            ORDER BY a.KararTarihi DESC, a.Id DESC) AS rej
             WHERE l.Id IN (SELECT CAST([value] AS INT) FROM OPENJSON(@ids))
            """, new { ids = JsonSerializer.Serialize(ids) }, tx, cancellationToken: ct))).ToList();

        var targets = rows.Select(r =>
        {
            var (rule, ruleError) = ReadRule(r.Ayarlar, r.ChannelCode);
            var target = ruleError is not null
                ? PriceTarget.Unpriceable(ruleError)
                : PriceAllocator.Compute(r.ListeFiyati, r.SatisFiyati, r.FiyatOverride, rule);
            return new ListingPriceTarget(
                r.ListingId, r.SkuId, r.ProductId, r.SkuKodu, r.UrunAdi, r.ChannelId, r.ChannelCode, r.KanalUrunKodu, r.KanalBarkod, r.Active,
                r.SonGonderilenSatisFiyati, r.SonGonderilenListeFiyati, Utc(r.SonFiyatSenkron), r.FiyatOverride, target, r.PendingId,
                r.ApprovedSale is { } aS && r.ApprovedList is { } aL ? (aS, aL) : null,
                r.RejectedSale is { } rS && r.RejectedList is { } rL ? (rS, rL) : null,
                r.CapabilityState, r.ChannelData);
        }).ToList();

        // Varyant kuralı (Beymen): aynı ürünün aktif ilanları farklı fiyata çıkıyorsa hiçbiri gönderilmez.
        var conflicts = targets
            .Where(t => t.Active && t.Target.Sendable && variantChannels.Contains(t.ChannelCode, StringComparer.Ordinal))
            .GroupBy(t => (t.ChannelId, t.ProductId))
            .Where(g => g.Select(t => (t.Target.SalePrice, t.Target.ListPrice)).Distinct().Count() > 1)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.OrderBy(t => t.SkuCode, StringComparer.Ordinal)
                .Select(t => $"{t.SkuCode}: {t.Target.SalePrice.ToString("0.00", CultureInfo.InvariantCulture)}")));
        if (conflicts.Count > 0)
        {
            targets = targets.Select(t => conflicts.TryGetValue((t.ChannelId, t.ProductId), out var detail)
                ? t with { Target = PriceTarget.Unpriceable($"Kanal aynı ürünün tüm bedenlerine aynı fiyatı ister; bu üründe farklı fiyatlar var ({detail}).") }
                : t).ToList();
        }

        return targets.Where(t => requested.Contains(t.ListingId)).ToList();
    }

    public static async Task<decimal> ThresholdAsync(SqlConnection connection, SqlTransaction? tx, CancellationToken ct)
    {
        var raw = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Deger FROM dbo.AppSetting WHERE Anahtar = @key", new { key = PriceSettingKeys.ChangeThresholdPercent }, tx, cancellationToken: ct));
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value >= 0m
            ? value
            : PriceSettingKeys.DefaultChangeThresholdPercent;
    }

    /// <summary>
    /// İlanların fiyat kararını uygular: gönderilecekleri FIYAT kuyruğuna alır (debounce), onay gerekenler için bekleyen onayı
    /// açar/günceller, artık geçerli olmayan bekleyen onayları kapatır. FIYAT yeteneği KAPALI kanallar ve pasif ilanlar atlanır.
    /// </summary>
    public async Task<PriceEvaluation> EvaluateAsync(SqlConnection connection, SqlTransaction? tx, IReadOnlyCollection<int> listingIds, CancellationToken ct)
    {
        var targets = await ComputeAsync(connection, tx, listingIds, ct);
        if (targets.Count == 0)
        {
            return new(0, 0, 0);
        }

        var threshold = await ThresholdAsync(connection, tx, ct);
        var toQueue = new List<int>();
        int opened = 0, closed = 0;
        foreach (var t in targets)
        {
            if (!t.Active || t.CapabilityState == CapabilityStates.Closed)
            {
                continue;
            }

            var decision = t.Decide(threshold);
            switch (decision.Action)
            {
                case PriceSyncAction.Send:
                    toQueue.Add(t.ListingId);
                    closed += await CloseApprovalAsync(connection, tx, t, ct);
                    break;
                case PriceSyncAction.NeedsApproval:
                    opened += await UpsertApprovalAsync(connection, tx, t, decision, ct);
                    break;
                default:
                    closed += await CloseApprovalAsync(connection, tx, t, ct);
                    break;
            }
        }

        var queued = await SyncStore.EnqueueAsync(connection, tx, toQueue, ct, SyncTypes.Price);
        return new(queued, opened, closed);
    }

    /// <summary>Bekleyen onayı yeni değerle günceller veya açar. Yeni açıldıysa 1 döner.</summary>
    public static async Task<int> UpsertApprovalAsync(SqlConnection connection, SqlTransaction? tx, ListingPriceTarget t, PriceSyncDecision decision, CancellationToken ct)
    {
        var args = new
        {
            listingId = t.ListingId, old = t.LastSale, sale = t.Target.SalePrice, list = t.Target.ListPrice,
            pct = decision.ChangePercent, reason = decision.ApprovalReason ?? PriceApprovalReasons.Threshold
        };
        var updated = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.PriceApproval WITH (UPDLOCK, HOLDLOCK)
               SET EskiSatisFiyati = @old, YeniSatisFiyati = @sale, YeniListeFiyati = @list, DegisimYuzde = @pct, Neden = @reason,
                   Guncelleme = SYSUTCDATETIME()
             WHERE ListingId = @listingId AND Durum = 'BEKLIYOR'
               AND (YeniSatisFiyati <> @sale OR YeniListeFiyati <> @list OR Neden <> @reason OR ISNULL(EskiSatisFiyati, -1) <> ISNULL(@old, -1))
            """, args, tx, cancellationToken: ct));
        if (updated > 0 || t.PendingApprovalId is not null)
        {
            return 0;
        }
        try
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT dbo.PriceApproval (ListingId, EskiSatisFiyati, YeniSatisFiyati, YeniListeFiyati, DegisimYuzde, Neden)
                VALUES (@listingId, @old, @sale, @list, @pct, @reason)
                """, args, tx, cancellationToken: ct));
            return 1;
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            return 0; // eşzamanlı başka işlem açtı
        }
    }

    private static async Task<int> CloseApprovalAsync(SqlConnection connection, SqlTransaction? tx, ListingPriceTarget t, CancellationToken ct) =>
        t.PendingApprovalId is null
            ? 0
            : await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE dbo.PriceApproval SET Durum = 'GECERSIZ', Guncelleme = SYSUTCDATETIME()
                 WHERE ListingId = @listingId AND Durum = 'BEKLIYOR'
                """, new { listingId = t.ListingId }, tx, cancellationToken: ct));

    /// <summary>
    /// Başarılı fiyat gönderimi: kayıt GONDERILDI, ilanın son gönderilen fiyatı güncellenir, geçmişe yazılır; sonra (gönderim
    /// sürerken değiştiyse) yeniden değerlendirilir.
    /// </summary>
    public async Task MarkSentAsync(SqlConnection connection, IReadOnlyCollection<(long QueueId, int ListingId, decimal Sale, decimal List)> items, DateTime now, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(items.Select(i => new { q = i.QueueId, l = i.ListingId, s = i.Sale, p = i.List }));
        await using (var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE q SET q.Durum = 'GONDERILDI', q.Hata = NULL, q.GonderilenSatisFiyati = j.s, q.GonderilenListeFiyati = j.p
                  FROM dbo.SyncQueue AS q
                  JOIN OPENJSON(@payload) WITH (q BIGINT, l INT, s DECIMAL(18,2), p DECIMAL(18,2)) AS j ON j.q = q.Id;

                INSERT dbo.PriceHistory (Tip, SkuId, ListingId, ChannelId, EskiSatis, YeniSatis, EskiListe, YeniListe, Kaynak)
                SELECT 'KANALA_GONDERILDI', l.SkuId, l.Id, l.ChannelId, l.SonGonderilenSatisFiyati, j.s, l.SonGonderilenListeFiyati, j.p, 'SENKRON'
                  FROM dbo.ChannelListing AS l
                  JOIN OPENJSON(@payload) WITH (q BIGINT, l INT, s DECIMAL(18,2), p DECIMAL(18,2)) AS j ON j.l = l.Id;

                UPDATE l SET l.SonGonderilenSatisFiyati = j.s, l.SonGonderilenListeFiyati = j.p, l.SonFiyatSenkron = @now
                  FROM dbo.ChannelListing AS l
                  JOIN OPENJSON(@payload) WITH (q BIGINT, l INT, s DECIMAL(18,2), p DECIMAL(18,2)) AS j ON j.l = l.Id;
                """, new { payload, now }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }

        await using (var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            await EvaluateAsync(connection, tx, items.Select(i => i.ListingId).Distinct().ToList(), ct);
            await tx.CommitAsync(ct);
        }
    }

    /// <summary>Kuyruk kaydını siler (gönderilecek bir şey kalmadı veya onaya düştü). Yalnızca bu talebin kayıtları.</summary>
    public static Task DeleteClaimedAsync(SqlConnection connection, IReadOnlyCollection<long> queueIds, CancellationToken ct) =>
        queueIds.Count == 0
            ? Task.CompletedTask
            : connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.SyncQueue WHERE Id IN (SELECT CAST([value] AS BIGINT) FROM OPENJSON(@ids)) AND Durum = 'BEKLIYOR' AND Tip = 'FIYAT'",
                new { ids = JsonSerializer.Serialize(queueIds) }, cancellationToken: ct));

    /// <summary>Channel.Ayarlar.fiyatKurali → kural. Yoksa varsayılan (çarpan 1, ek 0, yuvarlama yok, taban yok).</summary>
    public (PriceRule Rule, string? Error) ReadRule(string? settingsJson, string channelCode)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return (PriceRule.Default, null);
        }
        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(RuleProperty, out var r)
                || r.ValueKind == JsonValueKind.Null)
            {
                return (PriceRule.Default, null);
            }
            if (r.ValueKind != JsonValueKind.Object)
            {
                return (PriceRule.Default, "Kanal fiyat kuralı okunamadı (fiyatKurali nesne olmalı).");
            }
            var multiplier = Dec(r, "carpan") ?? 1m;
            var add = Dec(r, "sabitEk") ?? 0m;
            var floor = Dec(r, "taban");
            var roundingCode = r.TryGetProperty("yuvarlama", out var y) && y.ValueKind == JsonValueKind.String ? y.GetString() : null;
            if (!PriceRoundingCodes.TryParse(roundingCode, out var rounding))
            {
                return (PriceRule.Default, $"Kanal fiyat kuralında geçersiz yuvarlama: '{roundingCode}'.");
            }
            var rule = new PriceRule(multiplier, add, rounding, floor);
            var errors = rule.Validate();
            return errors.Count > 0 ? (rule, "Kanal fiyat kuralı geçersiz: " + string.Join(" ", errors)) : (rule, null);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            logger.LogWarning("{Kanal} kanal fiyat kuralı okunamadı: {Hata}", channelCode, ex.Message);
            return (PriceRule.Default, "Kanal fiyat kuralı okunamadı.");
        }
    }

    public static string RuleToJson(PriceRule rule) => JsonSerializer.Serialize(new
    {
        carpan = rule.Multiplier, sabitEk = rule.FixedAdd, yuvarlama = PriceRoundingCodes.ToCode(rule.Rounding), taban = rule.Floor
    });

    private static decimal? Dec(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal()
        : obj.TryGetProperty(name, out v) && v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null
        : obj.TryGetProperty(name, out _) ? throw new FormatException($"'{name}' sayı olmalı.")
        : null;

    private static DateTime? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private sealed class TargetRow
    {
        public int ListingId { get; init; }
        public int SkuId { get; init; }
        public int ProductId { get; init; }
        public string SkuKodu { get; init; } = "";
        public string UrunAdi { get; init; } = "";
        public int ChannelId { get; init; }
        public string ChannelCode { get; init; } = "";
        public string? KanalUrunKodu { get; init; }
        public string? KanalBarkod { get; init; }
        public bool Active { get; init; }
        public decimal? SonGonderilenSatisFiyati { get; init; }
        public decimal? SonGonderilenListeFiyati { get; init; }
        public DateTime? SonFiyatSenkron { get; init; }
        public decimal? FiyatOverride { get; init; }
        public decimal? ListeFiyati { get; init; }
        public decimal? SatisFiyati { get; init; }
        public string? Ayarlar { get; init; }
        public string CapabilityState { get; init; } = "KAPALI";
        public long? PendingId { get; init; }
        public decimal? ApprovedSale { get; init; }
        public decimal? ApprovedList { get; init; }
        public decimal? RejectedSale { get; init; }
        public decimal? RejectedList { get; init; }
        public string? ChannelData { get; init; }
    }
}
