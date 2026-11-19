using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Panel;
using PazaryeriHub.Application.Sync;
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

    public async Task<ChannelStockList> GetChannelStockListAsync(
        int channelId, string? search, bool onlyDeviations, bool onlyReview, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        await using var connection = await OpenAsync(ct);
        var channelCode = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Kod FROM dbo.Channel WHERE Id = @channelId", new { channelId }, cancellationToken: ct))
            ?? throw new PazaryeriHub.Application.Common.NotFoundException($"Kanal bulunamadı (Id={channelId}).");

        var unmatched = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.UnmatchedChannelItem WHERE ChannelId = @channelId AND Durum = 'BEKLIYOR'", new { channelId }, cancellationToken: ct));
        // Uyarılar ve kanal adı bellekte hesaplandığı için kanalın tüm ilanları okunur.
        var rows = await ReadChannelRowsAsync(connection, channelId, null, ct);

        var term = Text.Clean(search);
        IEnumerable<ChannelStockListRow> filtered = rows;
        if (term is not null)
        {
            bool Has(string? v) => v is not null && v.Contains(term, StringComparison.CurrentCultureIgnoreCase);
            filtered = filtered.Where(r => Has(r.SkuCode) || Has(r.SkuBarcode) || Has(r.ProductName) || Has(r.Channel.Barcode)
                || Has(r.Channel.ProductCode) || Has(r.Channel.StockCode) || Has(r.Channel.Name));
        }
        if (onlyDeviations)
        {
            filtered = filtered.Where(r => r.HasDeviation);
        }
        if (onlyReview)
        {
            filtered = filtered.Where(r => r.NeedsReview);
        }
        var matching = filtered.ToList();

        return new ChannelStockList(channelId, channelCode,
            new PagedResult<ChannelStockListRow>(matching.Skip((p - 1) * size).Take(size).ToList(), matching.Count, p, size),
            rows.Count, rows.Count(r => r.HasDeviation), rows.Count(r => r.NeedsReview), rows.Count(r => r.ChannelStock is null), unmatched,
            rows.Max(r => r.FetchedAtUtc), rows.Count(r => r.Approval is { Valid: true }));
    }

    /// <summary>
    /// Kanal stok listesi satırları (ADR-035/036): kanal tarafı (ilan verisi → yoksa bağlanmış eşleşmeyen kaydı), bağlı SKU,
    /// gitmesi gereken stok, uyarılar ve açık onay (imza bugünkü bilgiyle karşılaştırılır). <paramref name="listingId"/> verilirse yalnızca o ilan.
    /// </summary>
    internal async Task<List<ChannelStockListRow>> ReadChannelRowsAsync(SqlConnection connection, int channelId, int? listingId, CancellationToken ct,
        SqlTransaction? tx = null)
    {
        var listings = (await connection.QueryAsync<ChannelListingRow>(new CommandDefinition("""
            SELECT l.Id AS ListingId, l.Durum AS Status, l.KanalBarkod AS ChannelBarcode, l.KanalUrunKodu AS ChannelProductCode,
                   l.KanalVerisi AS ChannelData, l.SonGonderilenStok AS LastSent,
                   s.Id AS SkuId, s.SkuKodu AS SkuCode, s.Barkod AS SkuBarcode, p.Ad AS ProductName, p.Marka AS Brand,
                   s.Renk AS Color, s.Beden AS Size,
                   snap.KanalStok AS ChannelStock, snap.CekilmeZamani AS FetchedAtUtc,
                   u.UrunAdi AS FallbackName, u.Marka AS FallbackBrand, u.KanalStokKodu AS FallbackStockCode,
                   u.Renk AS FallbackColor, u.Beden AS FallbackSize,
                   a.Imza AS ApprovalSignature, a.Uyarilar AS ApprovalWarnings, a.Onaylayan AS ApprovedBy, a.OnayTarihi AS ApprovedAtUtc
              FROM dbo.ChannelListing l
              JOIN dbo.Sku s ON s.Id = l.SkuId
              JOIN dbo.Product p ON p.Id = s.ProductId
              OUTER APPLY (SELECT TOP (1) x.KanalStok, x.CekilmeZamani FROM dbo.ChannelStockSnapshot x
                            WHERE x.ListingId = l.Id ORDER BY x.CekilmeZamani DESC, x.Id DESC) AS snap
              OUTER APPLY (SELECT TOP (1) i.UrunAdi, i.Marka, i.KanalStokKodu, i.Renk, i.Beden FROM dbo.UnmatchedChannelItem i
                            WHERE i.ChannelId = l.ChannelId AND i.Durum = 'BAGLANDI'
                              AND ((l.KanalBarkod IS NOT NULL AND i.KanalBarkod = l.KanalBarkod)
                                OR (l.KanalUrunKodu IS NOT NULL AND i.KanalUrunKodu = l.KanalUrunKodu))
                            ORDER BY i.SonGorulme DESC, i.Id DESC) AS u
              LEFT JOIN dbo.ListingMatchApproval a ON a.ListingId = l.Id AND a.GeriAlmaTarihi IS NULL
             WHERE l.ChannelId = @channelId AND (@listingId IS NULL OR l.Id = @listingId)
             ORDER BY s.SkuKodu, l.Id
            """, new { channelId, listingId }, tx, cancellationToken: ct))).ToList();

        var targets = listings.Count == 0
            ? new Dictionary<int, int>()
            : (await syncStore.ComputeTargetsAsync(connection, tx, listings.Select(l => l.ListingId).ToList(), ct)).ToDictionary(t => t.ListingId, t => t.Target);

        return listings.Select(l =>
        {
            var data = PazaryeriHub.Application.Channels.ListingChannelData.Parse(l.ChannelData);
            var side = new ChannelSide(l.ChannelBarcode, l.ChannelProductCode,
                data.StockCode ?? l.FallbackStockCode, data.Name ?? l.FallbackName, data.Brand ?? l.FallbackBrand,
                data.Color ?? l.FallbackColor, data.Size ?? l.FallbackSize, data.Variant);
            var ledger = new LedgerSide(l.SkuCode, l.SkuBarcode, l.ProductName, l.Brand, l.Color, l.Size);
            var check = ListingMatchCheck.Evaluate(side, ledger);
            MatchApproval? approval = null;
            if (l.ApprovalSignature is { } signature && l.ApprovedAtUtc is { } approvedAt)
            {
                // Onay yalnızca bugün de aynı bilgiler ve aynı uyarılar varsa geçerlidir (ADR-036)
                var valid = check.Warnings.Count > 0 && signature == ListingMatchCheck.Signature(l.SkuId, side, ledger, check.Warnings);
                approval = new MatchApproval(l.ApprovedBy, DateTime.SpecifyKind(approvedAt, DateTimeKind.Utc), valid,
                    (l.ApprovalWarnings ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
            }
            return new ChannelStockListRow(l.ListingId, l.Status, side, data.ProductUrl, l.ChannelStock, Utc(l.FetchedAtUtc),
                l.SkuId, l.SkuCode, l.SkuBarcode, l.ProductName, l.Color, l.Size,
                targets.GetValueOrDefault(l.ListingId), l.LastSent, check.Basis, check.Warnings, approval);
        }).ToList();
    }

    public async Task<SkuDetail> GetSkuDetailAsync(int skuId, CancellationToken ct)
    {
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        await using var connection = await OpenAsync(ct);
        using var multi = await connection.QueryMultipleAsync(new CommandDefinition("""
            SELECT s.Id AS SkuId, s.SkuKodu AS SkuCode, s.Barkod AS Barcode, p.Id AS ProductId, p.Ad AS ProductName, p.Marka AS Brand,
                   p.Kategori AS Category, s.Renk AS Color, s.Beden AS Size, s.ListeFiyati AS ListPrice, s.SatisFiyati AS SalePrice
              FROM dbo.Sku s JOIN dbo.Product p ON p.Id = s.ProductId
             WHERE s.Id = @skuId;

            SELECT w.Id AS WarehouseId, w.Ad AS WarehouseName, b.Fiziksel AS Physical, b.Rezerve AS Reserved,
                   b.GuvenlikStogu AS SafetyStock, b.Satilabilir AS Available
              FROM dbo.StockBalance b JOIN dbo.Warehouse w ON w.Id = b.WarehouseId
             WHERE b.SkuId = @skuId
             ORDER BY w.Id;

            -- Net satış (İptal ve İade hariç; ADR-034)
            SELECT ISNULL(SUM(CASE WHEN o.SiparisTarihi >= @from7 THEN l.Adet END), 0) AS Units7Days,
                   ISNULL(SUM(CASE WHEN o.SiparisTarihi >= @from30 THEN l.Adet END), 0) AS Units30Days,
                   COUNT(DISTINCT CASE WHEN o.SiparisTarihi >= @from30 THEN o.Id END) AS Orders30Days,
                   ISNULL(SUM(CASE WHEN o.SiparisTarihi >= @from30 THEN l.Adet * l.BirimFiyat END), 0) AS Revenue30Days,
                   MAX(o.SiparisTarihi) AS LastSoldUtc
              FROM dbo.OrderLine l JOIN dbo.[Order] o ON o.Id = l.OrderId
             WHERE l.SkuId = @skuId AND o.Durum NOT IN ('Iptal', 'Iade');

            SELECT TOP (20) m.Id, m.Tarih AS AtUtc, m.SkuId, s.SkuKodu AS SkuCode, w.Ad AS WarehouseName, m.Tip AS Type, m.Adet AS Quantity,
                   m.KaynakTip AS SourceType, m.KaynakId AS SourceId, m.Kullanici AS [User]
              FROM dbo.StockMovement m JOIN dbo.Sku s ON s.Id = m.SkuId JOIN dbo.Warehouse w ON w.Id = m.WarehouseId
             WHERE m.SkuId = @skuId
             ORDER BY m.Tarih DESC, m.Id DESC;

            SELECT TOP (20) o.Id AS OrderId, c.Kod AS ChannelCode, o.KanalSiparisNo AS ChannelOrderNo, o.Durum AS Status,
                   o.SiparisTarihi AS OrderDateUtc, SUM(l.Adet) AS Quantity, MAX(l.BirimFiyat) AS UnitPrice, o.StokYetersiz AS InsufficientStock
              FROM dbo.OrderLine l JOIN dbo.[Order] o ON o.Id = l.OrderId JOIN dbo.Channel c ON c.Id = o.ChannelId
             WHERE l.SkuId = @skuId
             GROUP BY o.Id, c.Kod, o.KanalSiparisNo, o.Durum, o.SiparisTarihi, o.StokYetersiz
             ORDER BY o.SiparisTarihi DESC, o.Id DESC;
            """, new { skuId, from7 = nowUtc.AddDays(-7), from30 = nowUtc.AddDays(-30) }, cancellationToken: ct));

        var sku = await multi.ReadSingleOrDefaultAsync<SkuInfoRow>()
            ?? throw new PazaryeriHub.Application.Common.NotFoundException($"SKU bulunamadı: {skuId}.");
        var warehouses = (await multi.ReadAsync<WarehouseBalance>()).ToList();
        var sales = await multi.ReadSingleAsync<SalesRow>();
        var movements = (await multi.ReadAsync<StockMovementItem>())
            .Select(i => i with { AtUtc = DateTime.SpecifyKind(i.AtUtc, DateTimeKind.Utc) }).ToList();
        var orders = (await multi.ReadAsync<SkuOrderRow>()).Select(r => new SkuOrderItem(
            r.OrderId, r.ChannelCode, r.ChannelOrderNo, Enum.Parse<PazaryeriHub.Domain.Orders.OrderStatus>(r.Status),
            DateTime.SpecifyKind(r.OrderDateUtc, DateTimeKind.Utc), r.Quantity, r.UnitPrice, r.InsufficientStock)).ToList();

        var physical = warehouses.Sum(w => w.Physical);
        var reserved = warehouses.Sum(w => w.Reserved);
        var safety = warehouses.Sum(w => w.SafetyStock);
        var available = warehouses.Sum(w => w.Available);
        var channels = (await BuildRowsAsync(connection,
            [new SkuRow { SkuId = sku.SkuId, SkuCode = sku.SkuCode, ProductName = sku.ProductName, Physical = physical, Reserved = reserved, SafetyStock = safety, Available = available }],
            ct)).Single().Channels;

        return new SkuDetail(sku.SkuId, sku.SkuCode, sku.Barcode, sku.ProductId, sku.ProductName, sku.Brand, sku.Category, sku.Color, sku.Size,
            sku.ListPrice, sku.SalePrice, physical, reserved, safety, available, warehouses, channels,
            new SkuSalesSummary(sales.Units7Days, sales.Units30Days, sales.Orders30Days, sales.Revenue30Days, Utc(sales.LastSoldUtc)),
            movements, orders);
    }

    public async Task<AppSettingsDto> GetSettingsAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return new AppSettingsDto(await ThresholdAsync(connection, ct), await LowStockThresholdAsync(connection, null, ct));
    }

    public async Task<AppSettingsDto> UpdateSettingsAsync(AppSettingsDto settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        new Validator()
            .Must(settings.CriticalStockThreshold is >= 0 and <= 100_000, "criticalStockThreshold", "Eşik 0 ile 100.000 arasında olmalı.")
            .Must(settings.LowStockThreshold is null or (>= 0 and <= 100_000), "lowStockThreshold", "Eşik 0 ile 100.000 arasında olmalı.")
            .ThrowIfInvalid();

        await using var connection = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        await SaveSettingAsync(connection, tx, CriticalStockKey, settings.CriticalStockThreshold, ct);

        var lowStock = await LowStockThresholdAsync(connection, tx, ct);
        if (settings.LowStockThreshold is { } newThreshold && newThreshold != lowStock)
        {
            // Eşik değişti: aynı transaction'da olay → OutboxIsle tüm aktif ilanları yeniden hesaplar (ADR-023)
            await SaveSettingAsync(connection, tx, StockSettingKeys.LowStockThreshold, newThreshold, ct);
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT dbo.Outbox (OlayTipi, Payload) VALUES (@type, N'{}')", new { type = OutboxEvents.LowStockThresholdChanged }, tx, cancellationToken: ct));
            lowStock = newThreshold;
        }
        await tx.CommitAsync(ct);
        return new AppSettingsDto(settings.CriticalStockThreshold, lowStock);
    }

    private static Task SaveSettingAsync(SqlConnection connection, SqlTransaction tx, string key, int value, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.AppSetting SET Deger = @value, Guncelleme = SYSUTCDATETIME() WHERE Anahtar = @key;
            IF @@ROWCOUNT = 0 INSERT dbo.AppSetting (Anahtar, Deger) VALUES (@key, @value);
            """, new { key, value = value.ToString(CultureInfo.InvariantCulture) }, tx, cancellationToken: ct));

    private static async Task<int> LowStockThresholdAsync(SqlConnection connection, SqlTransaction? tx, CancellationToken ct)
    {
        var value = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Deger FROM dbo.AppSetting WHERE Anahtar = @key", new { key = StockSettingKeys.LowStockThreshold }, tx, cancellationToken: ct));
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) && t >= 0 ? t : 0;
    }

    private async Task<List<StockOverviewRow>> BuildRowsAsync(SqlConnection connection, IReadOnlyList<SkuRow> skus, CancellationToken ct)
    {
        if (skus.Count == 0)
        {
            return [];
        }

        var listings = (await connection.QueryAsync<ListingRow>(new CommandDefinition("""
            SELECT l.Id AS ListingId, l.SkuId, c.Kod AS ChannelCode, l.Durum AS Status, l.SonGonderilenStok AS LastSent,
                   l.KanalBarkod AS ChannelBarcode, l.KanalUrunKodu AS ChannelProductCode, l.KanalVerisi AS ChannelData,
                   snap.KanalStok AS ChannelStock, snap.CekilmeZamani AS FetchedAtUtc
              FROM dbo.ChannelListing l
              JOIN dbo.Channel c ON c.Id = l.ChannelId
              OUTER APPLY (SELECT TOP (1) x.KanalStok, x.CekilmeZamani FROM dbo.ChannelStockSnapshot x
                            WHERE x.ListingId = l.Id ORDER BY x.CekilmeZamani DESC, x.Id DESC) AS snap
             WHERE l.SkuId IN @skuIds
             ORDER BY c.Id, l.Id
            """, new { skuIds = skus.Select(s => s.SkuId).ToList() }, cancellationToken: ct))).ToList();

        var targets = (await syncStore.ComputeTargetsAsync(connection, null, listings.Select(l => l.ListingId).ToList(), ct))
            .ToDictionary(t => t.ListingId, t => t.Target);

        return skus.Select(s => new StockOverviewRow(s.SkuId, s.SkuCode, s.ProductName, s.Physical, s.Reserved, s.SafetyStock, s.Available,
            listings.Where(l => l.SkuId == s.SkuId).Select(l =>
            {
                var data = PazaryeriHub.Application.Channels.ListingChannelData.Parse(l.ChannelData);
                return new ChannelStockCell(
                    l.ChannelCode, l.ListingId, l.Status, targets[l.ListingId], l.LastSent, l.ChannelStock, Utc(l.FetchedAtUtc),
                    l.ChannelBarcode, l.ChannelProductCode, data.ProductUrl, data.Variant);
            }).ToList())).ToList();
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

    private sealed class ChannelListingRow
    {
        public int ListingId { get; init; }
        public string Status { get; init; } = "";
        public string? ChannelBarcode { get; init; }
        public string? ChannelProductCode { get; init; }
        public string? ChannelData { get; init; }
        public int? LastSent { get; init; }
        public int SkuId { get; init; }
        public string SkuCode { get; init; } = "";
        public string? SkuBarcode { get; init; }
        public string ProductName { get; init; } = "";
        public string? Brand { get; init; }
        public string? Color { get; init; }
        public string? Size { get; init; }
        public int? ChannelStock { get; init; }
        public DateTime? FetchedAtUtc { get; init; }
        public string? FallbackName { get; init; }
        public string? FallbackBrand { get; init; }
        public string? FallbackStockCode { get; init; }
        public string? FallbackColor { get; init; }
        public string? FallbackSize { get; init; }
        public string? ApprovalSignature { get; init; }
        public string? ApprovalWarnings { get; init; }
        public string? ApprovedBy { get; init; }
        public DateTime? ApprovedAtUtc { get; init; }
    }

    private sealed class SkuInfoRow
    {
        public int SkuId { get; init; }
        public string SkuCode { get; init; } = "";
        public string? Barcode { get; init; }
        public int ProductId { get; init; }
        public string ProductName { get; init; } = "";
        public string? Brand { get; init; }
        public string? Category { get; init; }
        public string? Color { get; init; }
        public string? Size { get; init; }
        public decimal? ListPrice { get; init; }
        public decimal? SalePrice { get; init; }
    }

    private sealed class SalesRow
    {
        public int Units7Days { get; init; }
        public int Units30Days { get; init; }
        public int Orders30Days { get; init; }
        public decimal Revenue30Days { get; init; }
        public DateTime? LastSoldUtc { get; init; }
    }

    private sealed class SkuOrderRow
    {
        public long OrderId { get; init; }
        public string ChannelCode { get; init; } = "";
        public string ChannelOrderNo { get; init; } = "";
        public string Status { get; init; } = "";
        public DateTime OrderDateUtc { get; init; }
        public int Quantity { get; init; }
        public decimal UnitPrice { get; init; }
        public bool InsufficientStock { get; init; }
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
        public string? ChannelBarcode { get; init; }
        public string? ChannelProductCode { get; init; }
        public string? ChannelData { get; init; }
    }
}
