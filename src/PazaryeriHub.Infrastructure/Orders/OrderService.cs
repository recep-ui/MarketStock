using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PazaryeriHub.Application.Catalog;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Application.Stock;
using PazaryeriHub.Domain.Orders;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.Orders;

/// <summary>
/// Sipariş kaydı, durum geçişleri ve stok etkileri (Dapper).
/// <para>
/// İdempotentlik iki katmanlıdır:
/// (1) <c>UNIQUE(ChannelId, KanalSiparisNo)</c>: aynı sipariş ikinci kez kaydedilmez;
/// (2) <c>UX_StockMovement_SiparisSatiri</c>: bir sipariş satırı için REZERVE/REZERVE_IPTAL/SEVK en fazla birer kez yazılır.
/// </para>
/// <para>
/// Stok etkileri "hedef duruma uzlaştırma" ile uygulanır: siparişin durumuna göre her satırın olması gereken stok hali
/// (rezerve / sevk / bırakılmış) hesaplanır ve eksik adım yapılır. Böylece işlem yarıda kalsa bile bir sonraki
/// işlemede tamamlanır ve hiçbir adım iki kez yapılmaz.
/// </para>
/// </summary>
public sealed class OrderService(
    SqlConnectionFactory connectionFactory,
    IStockLedger ledger,
    IOptions<OrderOptions> options,
    ILogger<OrderService> logger) : IOrderIngestionService, IOrderService
{
    public const string LineSourceType = "SIPARIS_SATIRI";
    public const string PanelSource = "PANEL";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private int? _defaultWarehouseId;

    public static string LineSourceId(long orderLineId) => "OL:" + orderLineId;

    // ------------------------------------------------------------------ Kayıt (kanaldan)

    public async Task<IngestResult> IngestAsync(string channelCode, NormalizedOrder order, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(order);
        Validate(order);
        var source = "KANAL:" + channelCode;

        await using var connection = await OpenAsync(ct);
        var channelId = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT Id FROM dbo.Channel WHERE Kod = @channelCode", new { channelCode }, cancellationToken: ct))
            ?? throw new NotFoundException($"Kanal bulunamadı: {channelCode}");

        var orderNo = order.ChannelOrderNo.Trim();
        var orderId = await FindOrderIdAsync(connection, channelId, orderNo, ct);
        var created = false;

        if (orderId is null)
        {
            var skuIds = new List<int?>(order.Lines.Count);
            foreach (var line in order.Lines)
            {
                skuIds.Add(await MapSkuAsync(connection, channelId, line, ct));
            }

            await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
            try
            {
                orderId = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
                    INSERT dbo.[Order] (ChannelId, KanalSiparisNo, Durum, Toplam, SiparisTarihi, MusteriOzet, KargoBilgisi)
                    OUTPUT inserted.Id
                    VALUES (@channelId, @orderNo, 'Yeni', @total, @orderDate, @customer, @cargo)
                    """, new
                    {
                        channelId,
                        orderNo,
                        total = order.TotalAmount,
                        orderDate = DateTime.SpecifyKind(order.OrderDateUtc, DateTimeKind.Utc),
                        customer = ToJson(order.Customer),
                        cargo = ToJson(order.Cargo)
                    }, tx, cancellationToken: ct));

                for (var i = 0; i < order.Lines.Count; i++)
                {
                    var line = order.Lines[i];
                    await connection.ExecuteAsync(new CommandDefinition("""
                        INSERT dbo.OrderLine (OrderId, SkuId, Adet, BirimFiyat, Komisyon)
                        VALUES (@orderId, @skuId, @qty, @price, @commission)
                        """, new { orderId, skuId = skuIds[i], qty = line.Quantity, price = line.UnitPrice, commission = line.Commission },
                        tx, cancellationToken: ct));
                }

                await InsertEventAsync(connection, tx, orderId.Value, null, OrderStatus.Yeni, source, ct);
                await tx.CommitAsync(ct);
                created = true;
            }
            catch (SqlException ex) when (ex.Number is DbErrors.UniqueIndex or DbErrors.UniqueConstraint)
            {
                // Eşzamanlı başka bir işleme aynı siparişi az önce kaydetti: mevcut kayıtla devam edilir.
                await tx.RollbackAsync(ct);
                orderId = await FindOrderIdAsync(connection, channelId, orderNo, ct)
                          ?? throw new InvalidOperationException("Benzersizlik ihlaline rağmen sipariş bulunamadı.", ex);
            }
        }

        if (order.Cargo is not null && !created)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE dbo.[Order] SET KargoBilgisi = @cargo
                WHERE Id = @orderId AND (KargoBilgisi IS NULL OR KargoBilgisi <> @cargo)
                """, new { orderId, cargo = ToJson(order.Cargo) }, cancellationToken: ct));
        }

        var transition = await TransitionCoreAsync(orderId.Value, order.Status, source, allowForwardPath: true, ct);
        if (transition.Rejected is not null)
        {
            logger.LogWarning("Kanaldan geçersiz durum geldi, uygulanmadı. Kanal {Kanal}, sipariş {SiparisNo}: {Neden}",
                channelCode, orderNo, transition.Rejected);
        }

        var shortage = await ApplyStockEffectsAsync(orderId.Value, ct);
        var unmatched = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.OrderLine WHERE OrderId = @orderId AND SkuId IS NULL", new { orderId }, cancellationToken: ct));

        if (created)
        {
            logger.LogInformation("Sipariş kaydedildi. Kanal {Kanal}, sipariş {SiparisNo}, Id {SiparisId}, stok yetersiz: {StokYetersiz}, eşleşmeyen satır: {Eslesmeyen}",
                channelCode, orderNo, orderId, shortage, unmatched);
        }

        return new IngestResult(orderId.Value, created, transition.Changed, transition.Rejected, shortage, unmatched);
    }

    // ------------------------------------------------------------------ Panel

    public async Task<OrderDetail> ChangeStatusAsync(long id, OrderStatus target, string source, CancellationToken ct)
    {
        var result = await TransitionCoreAsync(id, target, source, allowForwardPath: false, ct);
        if (result.Invalid is { } invalid)
        {
            throw new InvalidOrderTransitionException(invalid.From, invalid.To);
        }
        await ApplyStockEffectsAsync(id, ct);
        return await GetAsync(id, ct);
    }

    public async Task<PagedResult<OrderListItem>> SearchAsync(OrderQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (page, pageSize) = Paging.Normalize(query.Page, query.PageSize);
        var parameters = new
        {
            channelId = query.ChannelId,
            status = query.Status?.ToString(),
            from = query.FromUtc,
            to = query.ToUtc,
            insufficient = query.InsufficientStock,
            sku = LikePrefix(query.Sku),
            search = LikePrefix(query.Search),
            skip = (page - 1) * pageSize,
            take = pageSize
        };

        const string where = """
            WHERE (@channelId IS NULL OR o.ChannelId = @channelId)
              AND (@status IS NULL OR o.Durum = @status)
              AND (@from IS NULL OR o.SiparisTarihi >= @from)
              AND (@to IS NULL OR o.SiparisTarihi < @to)
              AND (@insufficient IS NULL OR o.StokYetersiz = @insufficient)
              AND (@search IS NULL OR o.KanalSiparisNo LIKE @search ESCAPE '\')
              AND (@sku IS NULL OR EXISTS (SELECT 1 FROM dbo.OrderLine l JOIN dbo.Sku s ON s.Id = l.SkuId
                                           WHERE l.OrderId = o.Id AND s.SkuKodu LIKE @sku ESCAPE '\'))
            """;

        await using var connection = await OpenAsync(ct);
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM dbo.[Order] AS o {where}", parameters, cancellationToken: ct));
        var rows = await connection.QueryAsync<OrderRow>(new CommandDefinition($"""
            SELECT o.Id, o.ChannelId, c.Kod AS ChannelCode, o.KanalSiparisNo, o.Durum, o.Toplam, o.SiparisTarihi, o.StokYetersiz,
                   (SELECT COUNT(*) FROM dbo.OrderLine l WHERE l.OrderId = o.Id) AS LineCount,
                   (SELECT COUNT(*) FROM dbo.OrderLine l WHERE l.OrderId = o.Id AND l.SkuId IS NULL) AS UnmatchedLineCount
              FROM dbo.[Order] AS o
              JOIN dbo.Channel AS c ON c.Id = o.ChannelId
            {where}
             ORDER BY o.SiparisTarihi DESC, o.Id DESC
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY
            """, parameters, cancellationToken: ct));

        var items = rows.Select(r => new OrderListItem(r.Id, r.ChannelId, r.ChannelCode, r.KanalSiparisNo, Enum.Parse<OrderStatus>(r.Durum),
            r.Toplam, Utc(r.SiparisTarihi), r.StokYetersiz, r.LineCount, r.UnmatchedLineCount)).ToList();
        return new PagedResult<OrderListItem>(items, total, page, pageSize);
    }

    public async Task<OrderDetail> GetAsync(long id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        using var multi = await connection.QueryMultipleAsync(new CommandDefinition("""
            SELECT o.Id, o.ChannelId, c.Kod AS ChannelCode, o.KanalSiparisNo, o.Durum, o.Toplam, o.SiparisTarihi, o.StokYetersiz,
                   o.MusteriOzet, o.KargoBilgisi
              FROM dbo.[Order] AS o JOIN dbo.Channel AS c ON c.Id = o.ChannelId
             WHERE o.Id = @id;

            SELECT l.Id, l.SkuId, s.SkuKodu, p.Ad AS UrunAdi, l.Adet, l.BirimFiyat, l.Komisyon,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.StockMovement m WHERE m.KaynakTip = 'SIPARIS_SATIRI' AND m.KaynakId = CONCAT('OL:', l.Id) AND m.Tip = 'REZERVE') THEN 1 ELSE 0 END AS bit) AS Rezerve,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.StockMovement m WHERE m.KaynakTip = 'SIPARIS_SATIRI' AND m.KaynakId = CONCAT('OL:', l.Id) AND m.Tip = 'SEVK') THEN 1 ELSE 0 END AS bit) AS Sevk,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.StockMovement m WHERE m.KaynakTip = 'SIPARIS_SATIRI' AND m.KaynakId = CONCAT('OL:', l.Id) AND m.Tip = 'REZERVE_IPTAL') THEN 1 ELSE 0 END AS bit) AS Iptal
              FROM dbo.OrderLine AS l
              LEFT JOIN dbo.Sku AS s ON s.Id = l.SkuId
              LEFT JOIN dbo.Product AS p ON p.Id = s.ProductId
             WHERE l.OrderId = @id
             ORDER BY l.Id;

            SELECT Id, EskiDurum, YeniDurum, Kaynak, Tarih FROM dbo.OrderEvent WHERE OrderId = @id ORDER BY Tarih, Id;
            """, new { id }, cancellationToken: ct));

        var o = await multi.ReadSingleOrDefaultAsync<OrderDetailRow>() ?? throw new NotFoundException($"Sipariş bulunamadı (Id={id}).");
        var lines = (await multi.ReadAsync<LineRow>()).Select(l => new OrderLineDto(
            l.Id, l.SkuId, l.SkuKodu, l.UrunAdi, l.Adet, l.BirimFiyat, l.Komisyon,
            Reserved: l.Rezerve && !l.Sevk && !l.Iptal, Shipped: l.Sevk, Released: l.Iptal)).ToList();
        var events = (await multi.ReadAsync<EventRow>()).Select(e => new OrderEventDto(
            e.Id, e.EskiDurum is null ? null : Enum.Parse<OrderStatus>(e.EskiDurum), Enum.Parse<OrderStatus>(e.YeniDurum), e.Kaynak, Utc(e.Tarih))).ToList();

        return new OrderDetail(o.Id, o.ChannelId, o.ChannelCode, o.KanalSiparisNo, Enum.Parse<OrderStatus>(o.Durum), o.Toplam,
            Utc(o.SiparisTarihi), o.StokYetersiz, FromJson<CustomerSummary>(o.MusteriOzet), FromJson<CargoInfo>(o.KargoBilgisi), lines, events);
    }

    // ------------------------------------------------------------------ Durum geçişi

    private sealed record TransitionOutcome(bool Changed, string? Rejected, (OrderStatus From, OrderStatus To)? Invalid);

    /// <summary>
    /// Durumu değiştirir; her adım OrderEvent'e yazılır. Sipariş satırı UPDLOCK ile kilitlenir (eşzamanlı geçişler sıralanır).
    /// <paramref name="allowForwardPath"/>: kanaldan gelen ileri atlamalarda ara adımlar da uygulanır; panelde yalnızca tek adım.
    /// </summary>
    private async Task<TransitionOutcome> TransitionCoreAsync(long orderId, OrderStatus target, string source, bool allowForwardPath, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var currentText = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Durum FROM dbo.[Order] WITH (UPDLOCK, ROWLOCK) WHERE Id = @orderId", new { orderId }, tx, cancellationToken: ct))
            ?? throw new NotFoundException($"Sipariş bulunamadı (Id={orderId}).");
        var current = Enum.Parse<OrderStatus>(currentText);

        if (current == target)
        {
            await tx.CommitAsync(ct);
            return new(false, null, null);
        }

        IReadOnlyList<OrderStatus>? path = allowForwardPath
            ? OrderStateMachine.FindPath(current, target)
            : OrderStateMachine.CanTransition(current, target) ? [target] : null;

        if (path is null)
        {
            await tx.RollbackAsync(ct);
            return new(false, $"'{current}' → '{target}' geçişine izin verilmiyor.", (current, target));
        }

        var from = current;
        foreach (var step in path)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.[Order] SET Durum = @to WHERE Id = @orderId", new { orderId, to = step.ToString() }, tx, cancellationToken: ct));
            await InsertEventAsync(connection, tx, orderId, from, step, source, ct);
            from = step;
        }

        await tx.CommitAsync(ct);
        return new(true, null, null);
    }

    // ------------------------------------------------------------------ Stok etkileri

    /// <summary>
    /// Siparişin durumuna göre her satırın stok halini uzlaştırır. Dönen değer: stok yetersizliği var mı
    /// (aynı zamanda <c>[Order].StokYetersiz</c> güncellenir). Eşleşmeyen (SkuId NULL) satırlar stoğu etkilemez.
    /// </summary>
    private async Task<bool> ApplyStockEffectsAsync(long orderId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var status = Enum.Parse<OrderStatus>(await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT Durum FROM dbo.[Order] WHERE Id = @orderId", new { orderId }, cancellationToken: ct)) ?? "Yeni");
        var lines = (await connection.QueryAsync<(long Id, int? SkuId, int Adet)>(new CommandDefinition(
            "SELECT Id, SkuId, Adet FROM dbo.OrderLine WHERE OrderId = @orderId ORDER BY Id", new { orderId }, cancellationToken: ct))).ToList();

        var keys = lines.Select(l => LineSourceId(l.Id)).ToList();
        var movements = keys.Count == 0 ? [] : (await connection.QueryAsync<(string KaynakId, string Tip, int WarehouseId)>(new CommandDefinition(
            "SELECT KaynakId, Tip, WarehouseId FROM dbo.StockMovement WHERE KaynakTip = @type AND KaynakId IN @keys",
            new { type = LineSourceType, keys }, cancellationToken: ct))).ToList();

        var intent = OrderStateMachine.StockIntentFor(status);
        var shortage = false;

        foreach (var line in lines.Where(l => l.SkuId is not null))
        {
            var key = LineSourceId(line.Id);
            var mine = movements.Where(m => m.KaynakId == key).ToList();
            var reserved = mine.Any(m => m.Tip == "REZERVE");
            var released = mine.Any(m => m.Tip == "REZERVE_IPTAL");
            var shipped = mine.Any(m => m.Tip == "SEVK");
            var warehouseId = reserved ? mine.First(m => m.Tip == "REZERVE").WarehouseId : await DefaultWarehouseIdAsync(connection, ct);
            var request = new StockChangeRequest(line.SkuId!.Value, warehouseId, line.Adet, LineSourceType, key);

            switch (intent)
            {
                case StockIntent.Reserved when !reserved:
                    shortage |= !await TryReserveAsync(request, ct);
                    break;

                case StockIntent.Shipped when !shipped && !released:
                    if (!reserved && !await TryReserveAsync(request, ct))
                    {
                        shortage = true; // rezerve edilemeyen satır sevk edilemez; mutabakatta sapma olarak görünür
                        break;
                    }
                    await IdempotentAsync(() => ledger.ShipAsync(request, ct), key, "SEVK");
                    break;

                case StockIntent.Released when reserved && !released && !shipped:
                    await IdempotentAsync(() => ledger.CancelReservationAsync(request, ct), key, "REZERVE_IPTAL");
                    break;
            }
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.[Order] SET StokYetersiz = @shortage WHERE Id = @orderId AND StokYetersiz <> @shortage",
            new { orderId, shortage }, cancellationToken: ct));
        return shortage;
    }

    /// <summary>true: rezerve edildi (veya eşzamanlı bir işlem zaten etmişti); false: stok yetersiz.</summary>
    private async Task<bool> TryReserveAsync(StockChangeRequest request, CancellationToken ct)
    {
        try
        {
            await ledger.ReserveAsync(request, ct);
            return true;
        }
        catch (InsufficientStockException)
        {
            return false;
        }
        catch (SqlException ex) when (ex.Number == DbErrors.UniqueIndex)
        {
            return true; // UX_StockMovement_SiparisSatiri: bu satır zaten rezerve edilmiş
        }
    }

    private async Task IdempotentAsync(Func<Task<StockChangeResult>> action, string key, string type)
    {
        try
        {
            await action();
        }
        catch (SqlException ex) when (ex.Number == DbErrors.UniqueIndex)
        {
            // Aynı satır için bu hareket zaten yazılmış (eşzamanlı işleme): yapılacak bir şey yok.
        }
        catch (StockOperationException ex)
        {
            logger.LogWarning("Sipariş satırı {Satir} için {Tip} yapılamadı: {Neden}", key, type, ex.Message);
        }
    }

    // ------------------------------------------------------------------ Yardımcılar

    private static void Validate(NormalizedOrder order)
    {
        var v = new Validator();
        v.Required("channelOrderNo", order.ChannelOrderNo, 100)
         .Must(order.Lines is { Count: > 0 }, "lines", "Siparişte en az bir satır olmalı.")
         .Must(order.TotalAmount >= 0, "totalAmount", "Toplam negatif olamaz.")
         .Must(order.OrderDateUtc != default, "orderDateUtc", "Sipariş tarihi zorunlu.");
        for (var i = 0; i < (order.Lines?.Count ?? 0); i++)
        {
            var l = order.Lines![i];
            v.Must(l.Quantity is > 0 and <= 1_000_000, $"lines[{i}].quantity", "Adet 1 ile 1.000.000 arasında olmalı.")
             .Must(l.UnitPrice >= 0, $"lines[{i}].unitPrice", "Birim fiyat negatif olamaz.")
             .MaxLength($"lines[{i}].channelProductCode", l.ChannelProductCode, CatalogLimits.ChannelProductCode)
             .MaxLength($"lines[{i}].barcode", l.Barcode, CatalogLimits.Barcode)
             .MaxLength($"lines[{i}].sellerSku", l.SellerSku, CatalogLimits.SkuCode);
        }
        v.ThrowIfInvalid();
    }

    /// <summary>Satırı SKU'ya eşler: kanal barkodu → kanal ürün kodu (ChannelListing) → SKU barkodu → satıcı SKU kodu.</summary>
    private static Task<int?> MapSkuAsync(SqlConnection connection, int channelId, NormalizedOrderLine line, CancellationToken ct) =>
        connection.ExecuteScalarAsync<int?>(new CommandDefinition("""
            SELECT TOP (1) SkuId FROM (
                SELECT l.SkuId, 1 AS Oncelik FROM dbo.ChannelListing l WHERE @barcode IS NOT NULL AND l.ChannelId = @channelId AND l.KanalBarkod = @barcode
                UNION ALL
                SELECT l.SkuId, 2 FROM dbo.ChannelListing l WHERE @code IS NOT NULL AND l.ChannelId = @channelId AND l.KanalUrunKodu = @code
                UNION ALL
                SELECT s.Id, 3 FROM dbo.Sku s WHERE @barcode IS NOT NULL AND s.Barkod = @barcode
                UNION ALL
                SELECT s.Id, 4 FROM dbo.Sku s WHERE @sellerSku IS NOT NULL AND s.SkuKodu = @sellerSku
            ) AS aday ORDER BY Oncelik
            """, new
            {
                channelId,
                barcode = Text.Clean(line.Barcode),
                code = Text.Clean(line.ChannelProductCode),
                sellerSku = Text.Clean(line.SellerSku)
            }, cancellationToken: ct));

    private static Task<long?> FindOrderIdAsync(SqlConnection connection, int channelId, string orderNo, CancellationToken ct) =>
        connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT Id FROM dbo.[Order] WHERE ChannelId = @channelId AND KanalSiparisNo = @orderNo", new { channelId, orderNo }, cancellationToken: ct));

    private static Task InsertEventAsync(SqlConnection connection, SqlTransaction tx, long orderId, OrderStatus? from, OrderStatus to, string source, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "INSERT dbo.OrderEvent (OrderId, EskiDurum, YeniDurum, Kaynak) VALUES (@orderId, @from, @to, @source)",
            new { orderId, from = from?.ToString(), to = to.ToString(), source = source.Length > 50 ? source[..50] : source }, tx, cancellationToken: ct));

    private async Task<int> DefaultWarehouseIdAsync(SqlConnection connection, CancellationToken ct)
    {
        if (_defaultWarehouseId is { } cached)
        {
            return cached;
        }
        var name = options.Value.DefaultWarehouseName;
        var id = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT Id FROM dbo.Warehouse WHERE Ad = @name", new { name }, cancellationToken: ct))
            ?? throw new InvalidOperationException($"Sipariş deposu bulunamadı: '{name}' (Siparis:DefaultWarehouseName).");
        _defaultWarehouseId = id;
        return id;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        return connection;
    }

    private static string? ToJson<T>(T? value) where T : class => value is null ? null : JsonSerializer.Serialize(value, JsonOptions);

    private static T? FromJson<T>(string? json) where T : class => json is null ? null : JsonSerializer.Deserialize<T>(json, JsonOptions);

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string? LikePrefix(string? value) =>
        Text.Clean(value) is { } v ? v.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[") + "%" : null;

    private sealed class OrderRow
    {
        public long Id { get; init; }
        public int ChannelId { get; init; }
        public string ChannelCode { get; init; } = "";
        public string KanalSiparisNo { get; init; } = "";
        public string Durum { get; init; } = "";
        public decimal Toplam { get; init; }
        public DateTime SiparisTarihi { get; init; }
        public bool StokYetersiz { get; init; }
        public int LineCount { get; init; }
        public int UnmatchedLineCount { get; init; }
    }

    private sealed class OrderDetailRow
    {
        public long Id { get; init; }
        public int ChannelId { get; init; }
        public string ChannelCode { get; init; } = "";
        public string KanalSiparisNo { get; init; } = "";
        public string Durum { get; init; } = "";
        public decimal Toplam { get; init; }
        public DateTime SiparisTarihi { get; init; }
        public bool StokYetersiz { get; init; }
        public string? MusteriOzet { get; init; }
        public string? KargoBilgisi { get; init; }
    }

    private sealed class LineRow
    {
        public long Id { get; init; }
        public int? SkuId { get; init; }
        public string? SkuKodu { get; init; }
        public string? UrunAdi { get; init; }
        public int Adet { get; init; }
        public decimal BirimFiyat { get; init; }
        public decimal? Komisyon { get; init; }
        public bool Rezerve { get; init; }
        public bool Sevk { get; init; }
        public bool Iptal { get; init; }
    }

    private sealed class EventRow
    {
        public long Id { get; init; }
        public string? EskiDurum { get; init; }
        public string YeniDurum { get; init; } = "";
        public string Kaynak { get; init; } = "";
        public DateTime Tarih { get; init; }
    }
}
