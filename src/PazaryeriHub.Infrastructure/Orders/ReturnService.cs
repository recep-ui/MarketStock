using System.Text.Json;
using Dapper;
using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Application.Stock;
using PazaryeriHub.Infrastructure.Channels;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.Orders;

/// <summary>
/// Kanal iade talepleri (ADR-027, M3).
/// - Kayıt: UNIQUE(ChannelId, KanalIadeNo); satırlar UNIQUE(ReturnRequestId, SatirAnahtar). Müşteri kişisel verisi saklanmaz.
/// - Depoya giriş (Depo + Yönetici): usp_IadeGiris, KaynakTip IADE_SATIRI, KaynakId RL:{satır}; her satır bir kez. Yalnızca siparişi
///   eşleşmiş ve SEVK hareketi olan satır girilir (satılmayan mal stoğu şişirmesin); eski sipariş-satırı yoluyla (OL:) çift giriş engellenir.
/// - Karar (yalnızca Yönetici): ReturnDecision (denetim izi) + IADE_KARAR kanal komutu aynı transaction'da. Depoya ulaşma kararı tetiklemez.
/// </summary>
public sealed class ReturnService(
    SqlConnectionFactory connectionFactory,
    IStockLedger ledger,
    ChannelCommandStore commands,
    IEnumerable<IChannelAdapter> adapters,
    ILogger<ReturnService> logger) : IReturnService
{
    public const string LineSourceType = "IADE_SATIRI";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string LineSourceId(long lineId) => "RL:" + lineId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ Kayıt (IadeCek)

    public async Task<ReturnUpsertResult?> UpsertAsync(int channelId, NormalizedReturn request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var returnNo = Clip(request.ReturnNo, 100);
        var orderNo = Clip(request.ChannelOrderNo, 100);
        if (returnNo is null || orderNo is null)
        {
            return null; // kimliksiz talep tekil kaydedilemez
        }

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var orderId = await ResolveOrderAsync(connection, channelId, orderNo, ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var head = await connection.QuerySingleOrDefaultAsync<(long Id, string Action)?>(new CommandDefinition("""
            MERGE dbo.ReturnRequest WITH (HOLDLOCK) AS t
            USING (SELECT @channelId AS ChannelId, @returnNo AS KanalIadeNo) AS s
               ON t.ChannelId = s.ChannelId AND t.KanalIadeNo = s.KanalIadeNo
            WHEN MATCHED AND (ISNULL(t.KanalDurum, N'') <> ISNULL(@status, N'') OR ISNULL(t.Gerekce, N'') <> ISNULL(@reason, N'')
                              OR (t.OrderId IS NULL AND @orderId IS NOT NULL)) THEN
                UPDATE SET KanalDurum = @status, Gerekce = @reason, OrderId = ISNULL(t.OrderId, @orderId), SonGuncelleme = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (ChannelId, KanalIadeNo, KanalSiparisNo, OrderId, KanalDurum, Gerekce, TalepTarihi)
                VALUES (@channelId, @returnNo, @orderNo, @orderId, @status, @reason, @requestedAt)
            OUTPUT inserted.Id, $action;
            """, new
            {
                channelId, returnNo, orderNo, orderId,
                status = Clip(request.ChannelStatus, 50), reason = Clip(request.Reason, 400),
                requestedAt = DateTime.SpecifyKind(request.CreatedUtc, DateTimeKind.Utc)
            }, tx, cancellationToken: ct));
        long id;
        var created = false;
        var changed = false;
        if (head is { } h)
        {
            id = h.Id;
            created = h.Action == "INSERT";
            changed = !created;
        }
        else
        {
            // MERGE değişiklik yoksa satır döndürmez
            id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT Id FROM dbo.ReturnRequest WHERE ChannelId = @channelId AND KanalIadeNo = @returnNo", new { channelId, returnNo }, tx, cancellationToken: ct));
        }

        foreach (var line in request.Lines)
        {
            var key = Clip(line.LineKey ?? line.Barcode ?? line.ChannelProductCode, 200);
            if (key is null || line.Quantity <= 0)
            {
                continue;
            }
            var skuId = await MapSkuAsync(connection, tx, channelId, line, ct);
            var orderLineId = orderId is null ? null : await MapOrderLineAsync(connection, tx, orderId.Value, key, skuId, ct);
            if (orderLineId is not null)
            {
                skuId ??= await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                    "SELECT SkuId FROM dbo.OrderLine WHERE Id = @orderLineId", new { orderLineId }, tx, cancellationToken: ct));
            }
            var items = line.ChannelItemIds is { Count: > 0 } ids ? JsonSerializer.Serialize(ids) : null;
            var affected = await connection.ExecuteAsync(new CommandDefinition("""
                MERGE dbo.ReturnRequestLine WITH (HOLDLOCK) AS t
                USING (SELECT @id AS ReturnRequestId, @key AS SatirAnahtar) AS s
                   ON t.ReturnRequestId = s.ReturnRequestId AND t.SatirAnahtar = s.SatirAnahtar
                WHEN MATCHED AND (t.Adet <> @qty OR ISNULL(t.KanalKalemleri, '') <> ISNULL(@items, '')
                                  OR (t.OrderLineId IS NULL AND @orderLineId IS NOT NULL) OR (t.SkuId IS NULL AND @skuId IS NOT NULL)) THEN
                    UPDATE SET Adet = @qty, KanalKalemleri = @items, OrderLineId = ISNULL(t.OrderLineId, @orderLineId), SkuId = ISNULL(t.SkuId, @skuId)
                WHEN NOT MATCHED THEN
                    INSERT (ReturnRequestId, SatirAnahtar, KanalKalemleri, Barkod, KanalUrunKodu, OrderLineId, SkuId, Adet)
                    VALUES (@id, @key, @items, @barcode, @code, @orderLineId, @skuId, @qty);
                """, new
                {
                    id, key, items, qty = line.Quantity, orderLineId, skuId,
                    barcode = Clip(line.Barcode, 100), code = Clip(line.ChannelProductCode, 100)
                }, tx, cancellationToken: ct));
            changed |= affected > 0 && !created;
        }
        await tx.CommitAsync(ct);
        return new ReturnUpsertResult(id, created, changed);
    }

    private static async Task<long?> ResolveOrderAsync(SqlConnection connection, int channelId, string orderNo, CancellationToken ct)
    {
        var exact = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT Id FROM dbo.[Order] WHERE ChannelId = @channelId AND KanalSiparisNo = @orderNo", new { channelId, orderNo }, cancellationToken: ct));
        if (exact is not null)
        {
            return exact;
        }
        // Paketli kanallarda (Trendyol/Beymen "{sipariş}/{paket}") iade paketi farklı olabilir: aynı sipariş numarasında TEK paket varsa o alınır.
        var number = orderNo.Split('/')[0];
        var candidates = (await connection.QueryAsync<long>(new CommandDefinition(
            "SELECT Id FROM dbo.[Order] WHERE ChannelId = @channelId AND (KanalSiparisNo = @number OR KanalSiparisNo LIKE @prefix)",
            new { channelId, number, prefix = number + "/%" }, cancellationToken: ct))).ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static async Task<int?> MapSkuAsync(SqlConnection connection, SqlTransaction tx, int channelId, NormalizedReturnLine line, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<int?>(new CommandDefinition("""
            SELECT TOP (1) SkuId FROM (
                SELECT l.SkuId, 1 AS Sira FROM dbo.ChannelListing l WHERE l.ChannelId = @channelId AND @barcode IS NOT NULL AND l.KanalBarkod = @barcode
                UNION ALL SELECT l.SkuId, 2 FROM dbo.ChannelListing l WHERE l.ChannelId = @channelId AND @code IS NOT NULL AND l.KanalUrunKodu = @code
                UNION ALL SELECT s.Id, 3 FROM dbo.Sku s WHERE @barcode IS NOT NULL AND s.Barkod = @barcode) x
             ORDER BY Sira
            """, new { channelId, barcode = Clip(line.Barcode, 100), code = Clip(line.ChannelProductCode, 100) }, tx, cancellationToken: ct));

    private static async Task<long?> MapOrderLineAsync(SqlConnection connection, SqlTransaction tx, long orderId, string key, int? skuId, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<long?>(new CommandDefinition("""
            SELECT TOP (1) Id FROM dbo.OrderLine
             WHERE OrderId = @orderId AND (KanalSatirNo = @key OR (@skuId IS NOT NULL AND SkuId = @skuId))
             ORDER BY CASE WHEN KanalSatirNo = @key THEN 0 ELSE 1 END, Id
            """, new { orderId, key, skuId }, tx, cancellationToken: ct));

    // ------------------------------------------------------------------ Liste ve detay

    private const string ListSelect = """
        SELECT r.Id, r.ChannelId, c.Kod AS ChannelCode, r.KanalIadeNo AS ReturnNo, r.KanalSiparisNo AS ChannelOrderNo, r.OrderId,
               r.KanalDurum AS ChannelStatus, r.Gerekce AS Reason, r.TalepTarihi AS RequestedAtUtc, r.SonGuncelleme AS LastUpdatedUtc,
               (SELECT COUNT(*) FROM dbo.ReturnRequestLine l WHERE l.ReturnRequestId = r.Id) AS LineCount,
               (SELECT COUNT(*) FROM dbo.ReturnRequestLine l WHERE l.ReturnRequestId = r.Id AND EXISTS (
                    SELECT 1 FROM dbo.StockMovement m WHERE m.KaynakTip = 'IADE_SATIRI' AND m.KaynakId = CONCAT('RL:', l.Id) AND m.Tip = 'IADE_GIRIS')) AS ReceivedLineCount,
               d.Karar AS LastDecision, k.Durum AS LastDecisionState
          FROM dbo.ReturnRequest r
          JOIN dbo.Channel c ON c.Id = r.ChannelId
          OUTER APPLY (SELECT TOP (1) Karar, CommandId FROM dbo.ReturnDecision WHERE ReturnRequestId = r.Id ORDER BY Id DESC) d
          LEFT JOIN dbo.ChannelCommand k ON k.Id = d.CommandId
        """;

    public async Task<PagedResult<ReturnListItem>> SearchAsync(int? channelId, string? search, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var args = new { channelId, search = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%", skip = (p - 1) * size, size };
        const string where = "WHERE (@channelId IS NULL OR r.ChannelId = @channelId) AND (@search IS NULL OR r.KanalIadeNo LIKE @search OR r.KanalSiparisNo LIKE @search)";
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition($"SELECT COUNT(*) FROM dbo.ReturnRequest r {where}", args, cancellationToken: ct));
        var rows = await connection.QueryAsync<ListRow>(new CommandDefinition(
            $"{ListSelect} {where} ORDER BY r.TalepTarihi DESC, r.Id DESC OFFSET @skip ROWS FETCH NEXT @size ROWS ONLY", args, cancellationToken: ct));
        return new PagedResult<ReturnListItem>(rows.Select(r => r.ToDto()).ToList(), total, p, size);
    }

    public async Task<ReturnDetail> GetAsync(long id, CancellationToken ct)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var head = await connection.QuerySingleOrDefaultAsync<ListRow>(new CommandDefinition($"{ListSelect} WHERE r.Id = @id", new { id }, cancellationToken: ct))
                   ?? throw new NotFoundException($"İade talebi bulunamadı (Id={id}).");
        var lines = (await connection.QueryAsync<LineRow>(new CommandDefinition("""
            SELECT l.Id, l.SatirAnahtar, l.Barkod, l.KanalUrunKodu, l.OrderLineId, l.SkuId, s.SkuKodu, l.Adet,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.StockMovement m WHERE m.KaynakTip = 'IADE_SATIRI' AND m.KaynakId = CONCAT('RL:', l.Id) AND m.Tip = 'IADE_GIRIS')
                             THEN 1 ELSE 0 END AS bit) AS Received
              FROM dbo.ReturnRequestLine l LEFT JOIN dbo.Sku s ON s.Id = l.SkuId
             WHERE l.ReturnRequestId = @id ORDER BY l.Id
            """, new { id }, cancellationToken: ct))).ToList();
        var problems = new List<ReturnLineDto>();
        foreach (var l in lines)
        {
            var problem = l.Received ? null : await ReceiveProblemAsync(connection, l.Id, l.OrderLineId, l.SkuId, l.Adet, ct);
            problems.Add(new ReturnLineDto(l.Id, l.SatirAnahtar, l.Barkod, l.KanalUrunKodu, l.OrderLineId, l.SkuId, l.SkuKodu, l.Adet, l.Received, problem?.Message));
        }
        var decisions = await connection.QueryAsync<DecisionRow>(new CommandDefinition("""
            SELECT d.Id, d.Karar, d.GerekceKodu, d.Aciklama, d.Kullanici, d.Tarih, d.CommandId, k.Durum AS CommandState, k.Hata AS CommandError
              FROM dbo.ReturnDecision d LEFT JOIN dbo.ChannelCommand k ON k.Id = d.CommandId
             WHERE d.ReturnRequestId = @id ORDER BY d.Id
            """, new { id }, cancellationToken: ct));
        var adapter = adapters.FirstOrDefault(a => a.ChannelCode == head.ChannelCode);
        var rules = adapter is null ? [] : ReturnDecisions.All.Select(d => new ReturnDecisionRuleDto(d, adapter.DescribeReturnDecision(ReturnDecisions.ToKind(d)))).ToList();
        return new ReturnDetail(head.ToDto(), problems,
            decisions.Select(d => new ReturnDecisionDto(d.Id, d.Karar, d.GerekceKodu, d.Aciklama, d.Kullanici, DateTime.SpecifyKind(d.Tarih, DateTimeKind.Utc),
                d.CommandId, d.CommandState, d.CommandError)).ToList(),
            rules);
    }

    // ------------------------------------------------------------------ Depoya giriş

    public async Task<ReturnDetail> ReceiveLineAsync(long returnId, long lineId, string? user, CancellationToken ct)
    {
        await using (var connection = connectionFactory.Create())
        {
            await connection.OpenAsync(ct);
            var line = await connection.QuerySingleOrDefaultAsync<(long? OrderLineId, int? SkuId, int Adet)?>(new CommandDefinition(
                "SELECT OrderLineId, SkuId, Adet FROM dbo.ReturnRequestLine WHERE Id = @lineId AND ReturnRequestId = @returnId",
                new { lineId, returnId }, cancellationToken: ct))
                ?? throw new NotFoundException($"İade satırı bulunamadı (talep {returnId}, satır {lineId}).");
            var check = await ReceiveProblemAsync(connection, lineId, line.OrderLineId, line.SkuId, line.Adet, ct);
            if (check is { } c)
            {
                throw new ConflictException(c.Message);
            }
            var warehouse = await ShipWarehouseAsync(connection, line.OrderLineId!.Value, ct);
            try
            {
                await ledger.ReceiveReturnAsync(new StockChangeRequest(line.SkuId!.Value, warehouse!.Value, line.Adet, LineSourceType, LineSourceId(lineId), user), ct);
            }
            catch (SqlException ex) when (ex.Number == DbErrors.UniqueIndex)
            {
                throw new ConflictException("Bu iade satırı depoya zaten girilmiş.", ex);
            }
            logger.LogInformation("İade talebi satırı depoya girdi: talep {TalepId}, satır {SatirId}, {Adet} adet, kullanıcı {Kullanici}", returnId, lineId, line.Adet, user);
        }
        return await GetAsync(returnId, ct);
    }

    private sealed record Problem(string Message);

    private static async Task<Problem?> ReceiveProblemAsync(SqlConnection connection, long lineId, long? orderLineId, int? skuId, int qty, CancellationToken ct)
    {
        if (await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.StockMovement WHERE KaynakTip = 'IADE_SATIRI' AND KaynakId = @key AND Tip = 'IADE_GIRIS'",
                new { key = LineSourceId(lineId) }, cancellationToken: ct)) > 0)
        {
            return new Problem("Bu iade satırı depoya zaten girilmiş.");
        }
        if (orderLineId is null)
        {
            return new Problem("İade, bir sipariş satırıyla eşleşmedi (sipariş bu uygulamada yok veya satır bulunamadı); stoğa girilemez.");
        }
        if (skuId is null)
        {
            return new Problem("Satır bir SKU ile eşleşmemiş; önce eşleştirme yapılmalı.");
        }
        if (await ShipWarehouseAsync(connection, orderLineId.Value, ct) is null)
        {
            return new Problem("Sipariş satırı sevk edilmemiş (stoktan düşmemiş); iade stoğa girilemez.");
        }
        var counts = await connection.QuerySingleAsync<(int Ordered, int OldPath, int NewPath)>(new CommandDefinition("""
            SELECT (SELECT Adet FROM dbo.OrderLine WHERE Id = @orderLineId) AS Ordered,
                   (SELECT COUNT(*) FROM dbo.StockMovement WHERE KaynakTip = 'SIPARIS_SATIRI' AND KaynakId = CONCAT('OL:', @orderLineId) AND Tip = 'IADE_GIRIS') AS OldPath,
                   ISNULL((SELECT SUM(m.Adet) FROM dbo.StockMovement m JOIN dbo.ReturnRequestLine l ON m.KaynakId = CONCAT('RL:', l.Id)
                            WHERE m.KaynakTip = 'IADE_SATIRI' AND m.Tip = 'IADE_GIRIS' AND l.OrderLineId = @orderLineId), 0) AS NewPath
            """, new { orderLineId }, cancellationToken: ct));
        if (counts.OldPath > 0)
        {
            return new Problem("Bu sipariş satırının iadesi sipariş ekranından zaten depoya girilmiş.");
        }
        if (counts.NewPath + qty > counts.Ordered)
        {
            return new Problem($"Depoya giriş sipariş adedini aşar (sipariş {counts.Ordered}, daha önce girilen {counts.NewPath}, bu satır {qty}).");
        }
        return null;
    }

    private static Task<int?> ShipWarehouseAsync(SqlConnection connection, long orderLineId, CancellationToken ct) =>
        connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT WarehouseId FROM dbo.StockMovement WHERE KaynakTip = 'SIPARIS_SATIRI' AND KaynakId = CONCAT('OL:', @orderLineId) AND Tip = 'SEVK'",
            new { orderLineId }, cancellationToken: ct));

    // ------------------------------------------------------------------ Karar

    public async Task<ReturnDecisionResult> DecideAsync(long returnId, ReturnDecisionInput input, string? user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var decision = input.Decision?.Trim().ToUpperInvariant() ?? "";
        if (!ReturnDecisions.All.Contains(decision))
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["decision"] = [$"{string.Join(", ", ReturnDecisions.All)} olmalı."] });
        }
        var description = input.Description?.Trim();
        if (description is { Length: > 500 })
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["description"] = ["Açıklama en fazla 500 karakter."] });
        }

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var head = await connection.QuerySingleOrDefaultAsync<(int ChannelId, string ChannelCode, string KanalIadeNo)?>(new CommandDefinition(
            "SELECT r.ChannelId, c.Kod, r.KanalIadeNo FROM dbo.ReturnRequest r JOIN dbo.Channel c ON c.Id = r.ChannelId WHERE r.Id = @returnId",
            new { returnId }, cancellationToken: ct))
            ?? throw new NotFoundException($"İade talebi bulunamadı (Id={returnId}).");
        var adapter = adapters.FirstOrDefault(a => a.ChannelCode == head.ChannelCode)
                      ?? throw new ConflictException("Kanal adaptörü kayıtlı değil.");
        var kind = ReturnDecisions.ToKind(decision);
        var rule = adapter.DescribeReturnDecision(kind);
        if (!rule.Supported)
        {
            throw new ConflictException(rule.Note ?? "Bu karar kanalda desteklenmiyor.");
        }

        var reason = input.ReasonCode?.Trim();
        if (kind == ReturnDecisionKind.Reject || rule.RequiresReason)
        {
            if (string.IsNullOrEmpty(reason))
            {
                throw new RequestValidationException(new Dictionary<string, string[]> { ["reasonCode"] = ["Red gerekçesi zorunlu."] });
            }
            if (rule.AllowedReasons is { } allowed && !allowed.Contains(reason))
            {
                throw new RequestValidationException(new Dictionary<string, string[]>
                {
                    ["reasonCode"] = [$"Bu kanalda yalnızca şu gerekçeler gönderilebilir: {string.Join(", ", allowed)}. {rule.Note}"]
                });
            }
            var known = await connection.QuerySingleAsync<(int Total, int Match)>(new CommandDefinition("""
                SELECT COUNT(*) AS Total, SUM(CASE WHEN KanalKimlik = @reason THEN 1 ELSE 0 END) AS Match
                  FROM dbo.ChannelReference WHERE ChannelId = @ChannelId AND Tip = 'IADE_RED_GEREKCE' AND Aktif = 1
                """, new { reason, head.ChannelId }, cancellationToken: ct));
            if (known.Total > 0 && known.Match == 0)
            {
                throw new RequestValidationException(new Dictionary<string, string[]>
                {
                    ["reasonCode"] = ["Gerekçe kanalın red gerekçeleri listesinde yok (Ayarlar → Kanal referans verisi)."]
                });
            }
        }

        var lines = (await connection.QueryAsync<(string SatirAnahtar, string? KanalUrunKodu, string? KanalKalemleri, int Adet)>(new CommandDefinition(
            "SELECT SatirAnahtar, KanalUrunKodu, KanalKalemleri, Adet FROM dbo.ReturnRequestLine WHERE ReturnRequestId = @returnId ORDER BY Id",
            new { returnId }, cancellationToken: ct)))
            .Select(l => new ReturnDecisionLine(l.SatirAnahtar, l.KanalUrunKodu,
                l.KanalKalemleri is null ? [] : JsonSerializer.Deserialize<List<string>>(l.KanalKalemleri) ?? [], l.Adet))
            .ToList();
        var payload = JsonSerializer.Serialize(new ReturnDecisionPayload(returnId,
            new ReturnDecisionRequest(head.KanalIadeNo, kind, lines, kind == ReturnDecisionKind.Reject ? reason : null, description)), Json);

        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        var created = await commands.CreateAsync(connection, tx, new NewChannelCommand(head.ChannelId, ChannelCommandTypes.ReturnDecision,
            $"RETURN:{returnId}:{decision}", payload, $"{ChannelCommandTypes.ReturnDecision}:{returnId}:{decision}", user), ct);
        if (created.Outcome == ChannelCommandStore.AlreadyExists)
        {
            await tx.RollbackAsync(ct);
            return new ReturnDecisionResult(null, created.Id, "ZATEN_VAR", $"Bu karar zaten verilmiş (durum {created.State}).");
        }
        var decisionId = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT dbo.ReturnDecision (ReturnRequestId, Karar, GerekceKodu, Aciklama, Kullanici, CommandId)
            OUTPUT inserted.Id VALUES (@returnId, @decision, @reason, @description, @user, @CommandId)
            """, new { returnId, decision, reason = kind == ReturnDecisionKind.Reject ? reason : null, description, user, CommandId = created.Id },
            tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        logger.LogWarning("İade kararı verildi: talep {TalepId} ({Kanal} {IadeNo}) → {Karar}, kullanıcı {Kullanici}, komut {KomutId} ({Durum})",
            returnId, head.ChannelCode, head.KanalIadeNo, decision, user, created.Id, created.State);
        return new ReturnDecisionResult(decisionId, created.Id,
            created.State == ChannelCommandStates.AwaitingApproval ? "ONAY_BEKLIYOR" : "KUYRUGA_ALINDI", null);
    }

    private static string? Clip(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim() is var v && v.Length > max ? v[..max] : value.Trim();

    // ------------------------------------------------------------------ Satır tipleri

    private sealed class ListRow
    {
        public long Id { get; init; }
        public int ChannelId { get; init; }
        public string ChannelCode { get; init; } = "";
        public string ReturnNo { get; init; } = "";
        public string ChannelOrderNo { get; init; } = "";
        public long? OrderId { get; init; }
        public string? ChannelStatus { get; init; }
        public string? Reason { get; init; }
        public DateTime RequestedAtUtc { get; init; }
        public DateTime LastUpdatedUtc { get; init; }
        public int LineCount { get; init; }
        public int ReceivedLineCount { get; init; }
        public string? LastDecision { get; init; }
        public string? LastDecisionState { get; init; }

        public ReturnListItem ToDto() => new(Id, ChannelId, ChannelCode, ReturnNo, ChannelOrderNo, OrderId, ChannelStatus, Reason,
            DateTime.SpecifyKind(RequestedAtUtc, DateTimeKind.Utc), DateTime.SpecifyKind(LastUpdatedUtc, DateTimeKind.Utc), LineCount, ReceivedLineCount,
            LastDecision, LastDecisionState);
    }

    private sealed class LineRow
    {
        public long Id { get; init; }
        public string SatirAnahtar { get; init; } = "";
        public string? Barkod { get; init; }
        public string? KanalUrunKodu { get; init; }
        public long? OrderLineId { get; init; }
        public int? SkuId { get; init; }
        public string? SkuKodu { get; init; }
        public int Adet { get; init; }
        public bool Received { get; init; }
    }

    private sealed class DecisionRow
    {
        public long Id { get; init; }
        public string Karar { get; init; } = "";
        public string? GerekceKodu { get; init; }
        public string? Aciklama { get; init; }
        public string? Kullanici { get; init; }
        public DateTime Tarih { get; init; }
        public long? CommandId { get; init; }
        public string? CommandState { get; init; }
        public string? CommandError { get; init; }
    }
}

/// <summary>IADE_KARAR komut içeriği.</summary>
public sealed record ReturnDecisionPayload(long ReturnId, ReturnDecisionRequest Request);

/// <summary>IADE_KARAR komutunu kanala gönderir (KanalKomutGonder; gerçek adaptör yazma kapısıyla sarılı).</summary>
public sealed class ReturnDecisionCommandHandler : IChannelCommandHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Type => ChannelCommandTypes.ReturnDecision;

    public async Task<string?> ExecuteAsync(IChannelAdapter adapter, string payloadJson, CancellationToken ct)
    {
        var p = JsonSerializer.Deserialize<ReturnDecisionPayload>(payloadJson, Json) ?? throw new ArgumentException("İade karar komutunun içeriği okunamadı.");
        await adapter.DecideReturnAsync(p.Request, ct);
        return null;
    }
}

/// <summary>
/// IadeCek (ADR-027; spesifikasyon akış 6): kanal başına iade taleplerini çeker, tekil kaydeder. Pencere: son başarılı çalışmanın
/// başlangıcı − 1 saat (örtüşmeli); ilk çalışmada 7 gün. Kanal iade okumayı desteklemiyorsa (Hepsiburada) atlanır, hata sayılmaz.
/// Hata durumunda pencere ilerlemez.
/// </summary>
public sealed class ReturnFetchJob(
    IEnumerable<IChannelAdapter> adapters,
    IReturnService returns,
    SqlConnectionFactory connectionFactory,
    TimeProvider clock,
    ILogger<ReturnFetchJob> logger)
{
    public const string JobType = "IADE_CEK";

    public static string RecurringJobId(string channelCode) => "iade-cek-" + channelCode.ToLowerInvariant();

    public sealed record RunResult(string ChannelCode, bool Skipped, string? Message, int Fetched, int Created, int Changed);

    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 0)]
    public async Task<RunResult> RunAsync(string channelCode, bool scheduled, CancellationToken ct)
    {
        var adapter = adapters.FirstOrDefault(a => a.ChannelCode == channelCode);
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var channel = await connection.QuerySingleOrDefaultAsync<(int Id, bool Aktif)?>(new CommandDefinition(
            "SELECT Id, Aktif FROM dbo.Channel WHERE Kod = @channelCode", new { channelCode }, cancellationToken: ct));
        if (adapter is null || channel is null || (scheduled && !channel.Value.Aktif))
        {
            return new RunResult(channelCode, true, adapter is null ? "Adaptör kayıtlı değil." : "Kanal pasif.", 0, 0, 0);
        }

        var startedAt = clock.GetUtcNow().UtcDateTime;
        var lastSuccess = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT SonBasarili FROM dbo.ChannelJobState WHERE ChannelId = @Id AND IsTipi = @JobType", new { channel.Value.Id, JobType }, cancellationToken: ct));
        var since = lastSuccess is { } ls ? DateTime.SpecifyKind(ls, DateTimeKind.Utc).AddHours(-1) : startedAt.AddDays(-7);

        IReadOnlyList<NormalizedReturn> fetched;
        try
        {
            fetched = await adapter.FetchReturnsAsync(since, ct);
        }
        catch (NotSupportedException ex)
        {
            await SaveStateAsync(connection, channel.Value.Id, startedAt, success: true, error: null, ct);
            return new RunResult(channelCode, true, ex.Message, 0, 0, 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = ex is ChannelApiException ? ex.Message : "İadeler çekilemedi (ayrıntı logda).";
            await SaveStateAsync(connection, channel.Value.Id, startedAt, success: false, error: message, ct);
            logger.LogError(ex, "IadeCek başarısız: {Kanal}", channelCode);
            throw;
        }

        int created = 0, changed = 0;
        foreach (var r in fetched)
        {
            var result = await returns.UpsertAsync(channel.Value.Id, r, ct);
            created += result is { Created: true } ? 1 : 0;
            changed += result is { Changed: true } ? 1 : 0;
        }
        await SaveStateAsync(connection, channel.Value.Id, startedAt, success: true, error: null, ct);
        if (fetched.Count > 0)
        {
            logger.LogInformation("IadeCek {Kanal}: {Adet} talep, {Yeni} yeni, {Degisen} güncellendi.", channelCode, fetched.Count, created, changed);
        }
        return new RunResult(channelCode, false, null, fetched.Count, created, changed);
    }

    private static Task SaveStateAsync(SqlConnection connection, int channelId, DateTime at, bool success, string? error, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.ChannelJobState
               SET SonDeneme = @at, SonBasarili = CASE WHEN @success = 1 THEN @at ELSE SonBasarili END, SonHata = @error,
                   ArdisikHata = CASE WHEN @success = 1 THEN 0 ELSE ArdisikHata + 1 END
             WHERE ChannelId = @channelId AND IsTipi = @JobType;
            IF @@ROWCOUNT = 0
                INSERT dbo.ChannelJobState (ChannelId, IsTipi, SonBasarili, SonDeneme, SonHata, ArdisikHata)
                VALUES (@channelId, @JobType, CASE WHEN @success = 1 THEN @at END, @at, @error, CASE WHEN @success = 1 THEN 0 ELSE 1 END);
            """, new { channelId, JobType, at, success, error }, cancellationToken: ct));
}
