using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Domain.Orders;
using PazaryeriHub.Infrastructure.Channels;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.Orders;

/// <summary>
/// Sipariş durumu / kargo bildirimi (ADR-026, M2). Bildirim yalnızca PANEL'den yapılmış geçiş için anlamlıdır: kanaldan gelen
/// durum zaten kanaldadır (yankı yok). Komut içeriği oluşturma anında Payload'a yazılır; gönderim KanalKomutGonder'dedir.
/// </summary>
public sealed class OrderNotificationService(
    SqlConnectionFactory connectionFactory,
    ChannelCommandStore commands,
    IEnumerable<IChannelAdapter> adapters,
    ILogger<OrderNotificationService> logger) : IOrderNotificationService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly OrderStatus[] Notifiable = [OrderStatus.Hazirlaniyor, OrderStatus.Kargoda];

    public static string IdempotencyKey(long orderId, OrderStatus status) => $"{ChannelCommandTypes.OrderStatus}:{orderId}:{status}";

    public static string TargetKey(long orderId, OrderStatus status) => $"ORDER:{orderId}:{status}";

    // ------------------------------------------------------------------ Liste

    public async Task<PagedResult<OrderNotificationRow>> SearchAsync(int? channelId, string? filter, string? search, int? page, int? pageSize, CancellationToken ct)
    {
        var p = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 50, 1, 500);
        var f = (filter ?? "BILDIRILMEDI").Trim().ToUpperInvariant();
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        // Son geçişin kaynağı ve bu duruma ait son (iptal edilmemiş) komut
        var sql = """
            WITH s AS (
                SELECT o.Id AS OrderId, o.ChannelId, c.Kod AS ChannelCode, o.KanalSiparisNo AS ChannelOrderNo, o.Durum AS Status,
                       o.SiparisTarihi AS OrderDateUtc,
                       ISNULL((SELECT TOP (1) e.Kaynak FROM dbo.OrderEvent e WHERE e.OrderId = o.Id AND e.YeniDurum = o.Durum ORDER BY e.Tarih DESC, e.Id DESC), '') AS StatusSource,
                       cmd.Id AS CommandId, cmd.Durum AS NotificationState, cmd.Hata AS Error,
                       CAST(CASE WHEN o.KanalPaketNo IS NOT NULL OR NOT EXISTS (SELECT 1 FROM dbo.OrderLine l WHERE l.OrderId = o.Id AND l.KanalSatirNo IS NULL)
                                 THEN 1 ELSE 0 END AS bit) AS HasChannelIds
                  FROM dbo.[Order] o
                  JOIN dbo.Channel c ON c.Id = o.ChannelId
                  OUTER APPLY (SELECT TOP (1) Id, Durum, Hata FROM dbo.ChannelCommand k
                                WHERE k.Tip = 'SIPARIS_DURUM' AND k.IdempotencyKey = CONCAT('SIPARIS_DURUM:', o.Id, ':', o.Durum)
                                ORDER BY CASE WHEN k.Durum = 'IPTAL' THEN 1 ELSE 0 END, k.Id DESC) cmd
                 WHERE o.Durum IN ('Hazirlaniyor', 'Kargoda') AND (@channelId IS NULL OR o.ChannelId = @channelId)
                   AND (@search IS NULL OR o.KanalSiparisNo LIKE @search))
            SELECT * FROM s
             WHERE (@f = 'TUMU')
                OR (@f = 'BILDIRILMEDI' AND StatusSource = 'PANEL' AND (NotificationState IS NULL OR NotificationState = 'IPTAL'))
                OR (@f = 'HATA' AND NotificationState = 'HATA')
                OR (@f = 'BEKLEYEN' AND NotificationState IN ('ONAY_BEKLIYOR', 'BEKLIYOR'))
            """;
        var all = (await connection.QueryAsync<RowData>(new CommandDefinition(sql + " ORDER BY OrderDateUtc DESC",
            new { channelId, f, search = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%" }, cancellationToken: ct))).ToList();
        var items = all.Skip((p - 1) * size).Take(size).Select(r => new OrderNotificationRow(r.OrderId, r.ChannelId, r.ChannelCode, r.ChannelOrderNo,
            Enum.Parse<OrderStatus>(r.Status), DateTime.SpecifyKind(r.OrderDateUtc, DateTimeKind.Utc), r.StatusSource, r.CommandId,
            r.NotificationState, r.Error, r.HasChannelIds)).ToList();
        return new PagedResult<OrderNotificationRow>(items, all.Count, p, size);
    }

    // ------------------------------------------------------------------ Bildir

    public async Task<OrderNotifyResult> NotifyAsync(OrderNotifyRequest request, string? user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OrderIds is not { Count: > 0 } || request.OrderIds.Count > 500)
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["orderIds"] = ["1–500 sipariş seçin."] });
        }
        if (request.Target is { } t && !Notifiable.Contains(t))
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["target"] = ["Yalnızca Hazırlanıyor veya Kargoda bildirilebilir."] });
        }

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var results = new List<OrderNotifyItemResult>();
        foreach (var orderId in request.OrderIds.Distinct())
        {
            results.Add(await NotifyOneAsync(connection, orderId, request.Target, request.OwnCargo, request.Cargo, user, ct));
        }
        return new OrderNotifyResult(results);
    }

    public async Task<OrderNotifyItemResult?> OnPanelTransitionAsync(long orderId, OrderStatus target, string? user, CancellationToken ct)
    {
        if (target != OrderStatus.Hazirlaniyor)
        {
            return null; // Kargoda kargo bilgisi ister; otomatik modda da elle bildirilir.
        }
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var channelId = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT ChannelId FROM dbo.[Order] WHERE Id = @orderId", new { orderId }, cancellationToken: ct));
        if (channelId is null || await ModeAsync(connection, channelId.Value, ct) != NotificationModes.Automatic)
        {
            return null;
        }
        // Yetenek kapalıysa vb. durum değişikliği başarısız olmaz; sipariş "bildirilmedi" listesinde kalır.
        var result = await NotifyOneAsync(connection, orderId, target, ownCargo: false, cargo: null, user, ct);
        if (result.Outcome == NotifyOutcomes.Rejected)
        {
            logger.LogWarning("Otomatik bildirim oluşturulamadı: sipariş {SiparisId}: {Neden}", orderId, result.Message);
        }
        return result;
    }

    private async Task<OrderNotifyItemResult> NotifyOneAsync(SqlConnection connection, long orderId, OrderStatus? requestedTarget, bool ownCargo,
        CargoInput? cargo, string? user, CancellationToken ct)
    {
        var order = await connection.QuerySingleOrDefaultAsync<OrderData?>(new CommandDefinition("""
            SELECT o.Id, o.ChannelId, c.Kod AS ChannelCode, o.KanalSiparisNo, o.KanalPaketNo, o.Durum,
                   ISNULL((SELECT TOP (1) e.Kaynak FROM dbo.OrderEvent e WHERE e.OrderId = o.Id AND e.YeniDurum = o.Durum ORDER BY e.Tarih DESC, e.Id DESC), '') AS Kaynak
              FROM dbo.[Order] o JOIN dbo.Channel c ON c.Id = o.ChannelId WHERE o.Id = @orderId
            """, new { orderId }, cancellationToken: ct));
        if (order is null)
        {
            return new OrderNotifyItemResult(orderId, null, NotifyOutcomes.Rejected, null, "Sipariş bulunamadı.");
        }
        var current = Enum.Parse<OrderStatus>(order.Durum);
        var target = requestedTarget ?? current;
        OrderNotifyItemResult Result(string outcome, string? message, long? commandId = null) =>
            new(orderId, order.KanalSiparisNo, outcome, commandId, message);

        if (!Notifiable.Contains(target))
        {
            return Result(NotifyOutcomes.Rejected, "Siparişin durumu Hazırlanıyor veya Kargoda değil; bildirilecek bir şey yok.");
        }
        if (current != target)
        {
            return Result(NotifyOutcomes.Rejected, $"Siparişin şu anki durumu {current}; önce panelde {target} yapın.");
        }
        if (!order.Kaynak.Equals("PANEL", StringComparison.Ordinal))
        {
            return Result(NotifyOutcomes.NotRequired, "Bu durum kanaldan geldi; kanal zaten bu durumda.");
        }

        var adapter = adapters.FirstOrDefault(a => a.ChannelCode == order.ChannelCode);
        if (adapter is null)
        {
            return Result(NotifyOutcomes.NotSupported, "Kanal adaptörü kayıtlı değil.");
        }
        var rule = adapter.DescribeOrderNotification(target, ownCargo);
        if (rule.NotRequired)
        {
            return Result(NotifyOutcomes.NotRequired, rule.Note);
        }
        if (!rule.Supported)
        {
            return Result(NotifyOutcomes.NotSupported, rule.Note);
        }

        var lines = (await connection.QueryAsync<(string? KanalSatirNo, int Adet)>(new CommandDefinition(
            "SELECT KanalSatirNo, Adet FROM dbo.OrderLine WHERE OrderId = @orderId ORDER BY Id", new { orderId }, cancellationToken: ct))).ToList();
        var problems = new List<string>();
        if (rule.RequiresPackageId && string.IsNullOrWhiteSpace(order.KanalPaketNo))
        {
            problems.Add("kanal paket/sipariş kimliği yok (sipariş bu sürümden önce çekilmiş; bir sonraki sipariş çekiminde dolar)");
        }
        if (rule.RequiresLineIds && (lines.Count == 0 || lines.Any(l => string.IsNullOrWhiteSpace(l.KanalSatirNo))))
        {
            problems.Add("kanal satır kimlikleri eksik (bir sonraki sipariş çekiminde dolar)");
        }
        var company = cargo?.Company?.Trim();
        var number = cargo?.TrackingNumber?.Trim();
        var url = cargo?.TrackingUrl?.Trim();
        if (rule.RequiresTrackingNumber && string.IsNullOrEmpty(number))
        {
            problems.Add("kargo takip numarası zorunlu");
        }
        if (rule.RequiresTrackingUrl && string.IsNullOrEmpty(url))
        {
            problems.Add("kargo takip linki zorunlu");
        }
        if (!string.IsNullOrEmpty(url) && !(Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)))
        {
            problems.Add("kargo takip linki http(s) adresi olmalı");
        }
        if (rule.RequiresCargoCompany && string.IsNullOrEmpty(company))
        {
            problems.Add("kargo firması zorunlu");
        }
        if (!string.IsNullOrEmpty(company) && !await IsKnownCargoAsync(connection, order.ChannelId, company, ct))
        {
            problems.Add($"kargo firması '{company}' kanalın kargo listesinde yok (Ayarlar → Kanal referans verisi)");
        }
        if (problems.Count > 0)
        {
            return Result(NotifyOutcomes.Rejected, "Kanala bildirilemez: " + string.Join("; ", problems) + ".");
        }

        var payload = JsonSerializer.Serialize(new OrderStatusPayload(orderId, order.KanalSiparisNo, order.KanalPaketNo, target, ownCargo,
            lines.Where(l => l.KanalSatirNo is not null).Select(l => new OrderStatusUpdateLine(l.KanalSatirNo!, l.Adet)).ToList(),
            ownCargo && (company is not null || number is not null || url is not null) ? new CargoInfo(company, number, url) : null), Json);
        ChannelCommandCreateResult created;
        try
        {
            created = await commands.CreateAsync(connection, null,
                new NewChannelCommand(order.ChannelId, ChannelCommandTypes.OrderStatus, TargetKey(orderId, target), payload, IdempotencyKey(orderId, target), user), ct);
        }
        catch (ConflictException ex)
        {
            return Result(NotifyOutcomes.Rejected, ex.Message); // ör. yetenek kapalı
        }
        return created.Outcome == ChannelCommandStore.AlreadyExists
            ? Result(NotifyOutcomes.AlreadyExists, $"Bu bildirim zaten var (durum {created.State}).", created.Id)
            : Result(created.State == ChannelCommandStates.AwaitingApproval ? NotifyOutcomes.AwaitingApproval : NotifyOutcomes.Queued, null, created.Id);
    }

    /// <summary>Kanalın kargo referans listesi boşsa (çekilmemiş/desteklenmiyor) kontrol yapılmaz.</summary>
    private static async Task<bool> IsKnownCargoAsync(SqlConnection connection, int channelId, string code, CancellationToken ct)
    {
        var counts = await connection.QuerySingleAsync<(int Total, int Match)>(new CommandDefinition("""
            SELECT COUNT(*) AS Total, SUM(CASE WHEN KanalKimlik = @code THEN 1 ELSE 0 END) AS Match
              FROM dbo.ChannelReference WHERE ChannelId = @channelId AND Tip = 'KARGO_FIRMASI' AND Aktif = 1
            """, new { channelId, code }, cancellationToken: ct));
        return counts.Total == 0 || counts.Match > 0;
    }

    // ------------------------------------------------------------------ Diğer

    public async Task<IReadOnlyList<ChannelCommandDto>> GetForOrderAsync(long orderId, CancellationToken ct)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var rows = await connection.QueryAsync<ChannelCommandService.Row>(new CommandDefinition("""
            SELECT c.Id, c.ChannelId, ch.Kod AS ChannelCode, c.Tip, c.HedefAnahtar, c.Payload, c.Durum, c.Deneme, c.SonrakiDeneme, c.Hata,
                   c.KanalIslemNo, c.Olusturan, c.Olusma, c.Onaylayan, c.OnayTarihi, c.Gonderilme
              FROM dbo.ChannelCommand c JOIN dbo.Channel ch ON ch.Id = c.ChannelId
             WHERE c.Tip = 'SIPARIS_DURUM' AND c.HedefAnahtar LIKE @prefix
             ORDER BY c.Id
            """, new { prefix = $"ORDER:{orderId}:%" }, cancellationToken: ct));
        return rows.Select(r => r.ToDto()).ToList();
    }

    public async Task<IReadOnlyList<OrderNotificationRuleDto>> GetRulesAsync(int channelId, CancellationToken ct)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var code = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Kod FROM dbo.Channel WHERE Id = @channelId", new { channelId }, cancellationToken: ct))
            ?? throw new NotFoundException($"Kanal bulunamadı (Id={channelId}).");
        var adapter = adapters.FirstOrDefault(a => a.ChannelCode == code);
        if (adapter is null)
        {
            return [];
        }
        return [.. from s in Notifiable from own in new[] { false, true } select new OrderNotificationRuleDto(s.ToString(), own, adapter.DescribeOrderNotification(s, own))];
    }

    public async Task<string> GetModeAsync(int channelId, CancellationToken ct)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        return await ModeAsync(connection, channelId, ct);
    }

    public async Task SetModeAsync(int channelId, string mode, string? user, CancellationToken ct)
    {
        var m = mode?.Trim().ToUpperInvariant() ?? "";
        if (!NotificationModes.All.Contains(m))
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["mode"] = [$"{string.Join(" veya ", NotificationModes.All)} olmalı."] });
        }
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        // Ayarlar JSON'unda yalnızca bildirimModu değişir (stokUstLimit vb. korunur; stok yeniden gönderimi tetiklenmez).
        var changed = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.Channel SET Ayarlar = JSON_MODIFY(ISNULL(Ayarlar, N'{}'), '$.bildirimModu', @m) WHERE Id = @channelId
            """, new { channelId, m }, cancellationToken: ct));
        if (changed == 0)
        {
            throw new NotFoundException($"Kanal bulunamadı (Id={channelId}).");
        }
        logger.LogWarning("Sipariş bildirim modu değişti: kanal {KanalId} → {Mod}, kullanıcı {Kullanici}", channelId, m, user);
    }

    private static async Task<string> ModeAsync(SqlConnection connection, int channelId, CancellationToken ct)
    {
        var mode = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT JSON_VALUE(Ayarlar, '$.bildirimModu') FROM dbo.Channel WHERE Id = @channelId", new { channelId }, cancellationToken: ct));
        return mode is not null && NotificationModes.All.Contains(mode) ? mode : NotificationModes.ConfirmAndSend;
    }

    private sealed class RowData
    {
        public long OrderId { get; init; }
        public int ChannelId { get; init; }
        public string ChannelCode { get; init; } = "";
        public string ChannelOrderNo { get; init; } = "";
        public string Status { get; init; } = "";
        public DateTime OrderDateUtc { get; init; }
        public string StatusSource { get; init; } = "";
        public long? CommandId { get; init; }
        public string? NotificationState { get; init; }
        public string? Error { get; init; }
        public bool HasChannelIds { get; init; }
    }

    private sealed class OrderData
    {
        public long Id { get; init; }
        public int ChannelId { get; init; }
        public string ChannelCode { get; init; } = "";
        public string KanalSiparisNo { get; init; } = "";
        public string? KanalPaketNo { get; init; }
        public string Durum { get; init; } = "";
        public string Kaynak { get; init; } = "";
    }
}

/// <summary>SIPARIS_DURUM komut içeriği (oluşturma anındaki görüntü).</summary>
public sealed record OrderStatusPayload(
    long OrderId, string ChannelOrderNo, string? ChannelPackageId, OrderStatus Status, bool OwnCargo,
    IReadOnlyList<OrderStatusUpdateLine> Lines, CargoInfo? Cargo);

/// <summary>SIPARIS_DURUM komutunu kanala gönderir (KanalKomutGonder çağırır; gerçek adaptör yazma kapısıyla sarılıdır).</summary>
public sealed class OrderStatusCommandHandler : IChannelCommandHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Type => ChannelCommandTypes.OrderStatus;

    public async Task<string?> ExecuteAsync(IChannelAdapter adapter, string payloadJson, CancellationToken ct)
    {
        var p = JsonSerializer.Deserialize<OrderStatusPayload>(payloadJson, Json)
                ?? throw new ArgumentException("Sipariş durum komutunun içeriği okunamadı.");
        await adapter.UpdateOrderStatusAsync(new OrderStatusUpdate(p.ChannelOrderNo, p.ChannelPackageId, p.Status, p.Lines, p.Cargo, p.OwnCargo), ct);
        return null;
    }
}
