using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Domain.Orders;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.Channels;

/// <summary>
/// Kanala yazma kapısı (ADR-026). Gerçek kanala yazma için dört koşulun hepsi gerekir:
/// 1) global kilit kapalı (<c>Kanallar:YazmaKapali=false</c>; deneme modu true yapar), 2) adaptör Gercek modda (bu kapı yalnızca gerçek
/// adaptörleri saran <see cref="GuardedChannelAdapter"/>'dan çağrılır), 3) kanal aktif, 4) yetenek ACIK veya DENEME
/// (DENEME'de kayıt bazlı onay KanalKomutGonder'de zorunlu tutulur).
/// </summary>
public sealed class ChannelWriteGate(IOptionsMonitor<ChannelWriteOptions> options, SqlConnectionFactory connectionFactory) : IChannelWriteGate
{
    public async Task EnsureAllowedAsync(string channelCode, string capability, CancellationToken ct)
    {
        if (options.CurrentValue.YazmaKapali)
        {
            throw new ChannelWriteBlockedException(channelCode, capability, "Global yazma kilidi açık (Kanallar:YazmaKapali; deneme modu).");
        }
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<(int Id, bool Aktif)?>(new CommandDefinition(
            "SELECT Id, Aktif FROM dbo.Channel WHERE Kod = @channelCode", new { channelCode }, cancellationToken: ct))
            ?? throw new ChannelWriteBlockedException(channelCode, capability, "Kanal bulunamadı.");
        if (!row.Aktif)
        {
            throw new ChannelWriteBlockedException(channelCode, capability, "Kanal pasif.");
        }
        var state = await StateAsync(connection, row.Id, capability, ct);
        if (state == CapabilityStates.Closed)
        {
            throw new ChannelWriteBlockedException(channelCode, capability, "Yetenek kapalı (Ayarlar → Kanal yazma yetenekleri).");
        }
    }

    public async Task<string> GetStateAsync(int channelId, string capability, CancellationToken ct)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        return await StateAsync(connection, channelId, capability, ct);
    }

    internal static async Task<string> StateAsync(Microsoft.Data.SqlClient.SqlConnection connection, int channelId, string capability, CancellationToken ct,
        Microsoft.Data.SqlClient.SqlTransaction? tx = null) =>
        await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Durum FROM dbo.ChannelCapability WHERE ChannelId = @channelId AND Yetenek = @capability",
            new { channelId, capability }, tx, cancellationToken: ct))
        ?? ChannelCapabilities.Default(capability);
}

/// <summary>
/// Kanal yazma yeteneklerini okur/değiştirir (Yönetici). Her değişiklik kullanıcı adı ve zamanla yazılır.
/// FIYAT (ADR-028): kanalın fiyat kuralı KDV'yi resmi dokümanla doğrulamamışsa DENEME/ACIK yapılamaz; açıldığında kanalın tüm
/// ilanları yeniden değerlendirilsin diye aynı transaction'da FiyatDegisti(ChannelId) yazılır.
/// </summary>
public sealed class ChannelCapabilityService(
    SqlConnectionFactory connectionFactory,
    IEnumerable<IChannelAdapter> adapters,
    ILogger<ChannelCapabilityService> logger) : IChannelCapabilityService
{
    public async Task<IReadOnlyList<ChannelCapabilityDto>> GetAsync(int channelId, CancellationToken ct)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        await EnsureChannelAsync(connection, channelId, ct);
        var rows = (await connection.QueryAsync<(string Yetenek, string Durum, string? Degistiren, DateTime DegisimTarihi)>(new CommandDefinition(
            "SELECT Yetenek, Durum, Degistiren, DegisimTarihi FROM dbo.ChannelCapability WHERE ChannelId = @channelId",
            new { channelId }, cancellationToken: ct))).ToDictionary(r => r.Yetenek);
        return ChannelCapabilities.All.Select(c => rows.TryGetValue(c, out var r)
                ? new ChannelCapabilityDto(c, r.Durum, r.Degistiren, DateTime.SpecifyKind(r.DegisimTarihi, DateTimeKind.Utc))
                : new ChannelCapabilityDto(c, ChannelCapabilities.Default(c), null, DateTime.MinValue))
            .ToList();
    }

    public async Task<ChannelCapabilityDto> SetAsync(int channelId, string capability, string state, string? user, CancellationToken ct)
    {
        var cap = capability?.Trim().ToUpperInvariant() ?? "";
        var st = state?.Trim().ToUpperInvariant() ?? "";
        var errors = new Dictionary<string, string[]>();
        if (!ChannelCapabilities.All.Contains(cap))
        {
            errors["capability"] = [$"Geçersiz yetenek; {string.Join(", ", ChannelCapabilities.All)} olmalı."];
        }
        if (!CapabilityStates.All.Contains(st))
        {
            errors["state"] = [$"Geçersiz durum; {string.Join(", ", CapabilityStates.All)} olmalı."];
        }
        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        await EnsureChannelAsync(connection, channelId, ct);
        if (cap == ChannelCapabilities.Price && st != CapabilityStates.Closed)
        {
            var code = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT Kod FROM dbo.Channel WHERE Id = @channelId", new { channelId }, cancellationToken: ct));
            var rule = adapters.FirstOrDefault(a => a.ChannelCode == code)?.DescribePriceUpdate()
                       ?? PriceUpdateRule.NotSupported("Kanal adaptörü kayıtlı değil.");
            if (!rule.CanEnable)
            {
                throw new RequestValidationException(new Dictionary<string, string[]> { ["state"] = [rule.BlockReason!] });
            }
        }

        await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            MERGE dbo.ChannelCapability WITH (HOLDLOCK) AS t
            USING (SELECT @channelId AS ChannelId, @cap AS Yetenek) AS s ON t.ChannelId = s.ChannelId AND t.Yetenek = s.Yetenek
            WHEN MATCHED THEN UPDATE SET Durum = @st, Degistiren = @user, DegisimTarihi = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (ChannelId, Yetenek, Durum, Degistiren) VALUES (@channelId, @cap, @st, @user);
            """, new { channelId, cap, st, user }, tx, cancellationToken: ct));
        if (cap == ChannelCapabilities.Price && st != CapabilityStates.Closed)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT dbo.Outbox (OlayTipi, Payload) VALUES (@type, CONCAT(N'{\"ChannelId\":', @channelId, N'}'))",
                new { type = PazaryeriHub.Application.Sync.OutboxEvents.PriceChanged, channelId }, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
        logger.LogWarning("Kanal yazma yeteneği değişti: kanal {KanalId}, {Yetenek} → {Durum}, kullanıcı {Kullanici}", channelId, cap, st, user);
        return (await GetAsync(channelId, ct)).Single(c => c.Capability == cap);
    }

    private static async Task EnsureChannelAsync(Microsoft.Data.SqlClient.SqlConnection connection, int channelId, CancellationToken ct)
    {
        if (await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.Channel WHERE Id = @channelId", new { channelId }, cancellationToken: ct)) == 0)
        {
            throw new NotFoundException($"Kanal bulunamadı (Id={channelId}).");
        }
    }
}

/// <summary>
/// Gerçek (Gercek modlu) kanal adaptörünü saran son savunma hattı (ADR-026). Okuma çağrıları doğrudan geçer; YAZMA çağrıları
/// (stok, fiyat, sipariş durumu, iade kararı) önce <see cref="IChannelWriteGate"/>'ten geçer. Böylece hangi kod yolu çağırırsa çağırsın
/// kapalı yetenek, pasif kanal veya global kilitte gerçek kanala istek ÇIKMAZ.
/// </summary>
public sealed class GuardedChannelAdapter(IChannelAdapter inner, IChannelWriteGate gate) : IChannelAdapter
{
    public IChannelAdapter Inner => inner;

    public string ChannelCode => inner.ChannelCode;

    public Task<IReadOnlyList<NormalizedOrder>> FetchOrdersAsync(DateTime sinceUtc, CancellationToken ct) => inner.FetchOrdersAsync(sinceUtc, ct);

    public Task<IReadOnlyList<ChannelStock>> FetchStockAsync(IEnumerable<string>? skus, CancellationToken ct) => inner.FetchStockAsync(skus, ct);

    public async Task<BatchResult> PushStockAsync(IReadOnlyList<StockUpdate> updates, CancellationToken ct)
    {
        await gate.EnsureAllowedAsync(ChannelCode, ChannelCapabilities.Stock, ct);
        return await inner.PushStockAsync(updates, ct);
    }

    public async Task<BatchResult> PushPriceAsync(IReadOnlyList<PriceUpdate> updates, CancellationToken ct)
    {
        await gate.EnsureAllowedAsync(ChannelCode, ChannelCapabilities.Price, ct);
        return await inner.PushPriceAsync(updates, ct);
    }

    public Task<BatchStatus> CheckBatchAsync(string batchId, CancellationToken ct) => inner.CheckBatchAsync(batchId, ct);

    public PriceUpdateRule DescribePriceUpdate() => inner.DescribePriceUpdate();

    public async Task UpdateOrderStatusAsync(OrderStatusUpdate update, CancellationToken ct)
    {
        await gate.EnsureAllowedAsync(ChannelCode, ChannelCapabilities.OrderStatus, ct);
        await inner.UpdateOrderStatusAsync(update, ct);
    }

    public OrderNotificationRule DescribeOrderNotification(OrderStatus target, bool ownCargo) => inner.DescribeOrderNotification(target, ownCargo);

    public Task<IReadOnlyList<NormalizedReturn>> FetchReturnsAsync(DateTime sinceUtc, CancellationToken ct) => inner.FetchReturnsAsync(sinceUtc, ct);

    public async Task DecideReturnAsync(ReturnDecisionRequest request, CancellationToken ct)
    {
        await gate.EnsureAllowedAsync(ChannelCode, ChannelCapabilities.ReturnDecision, ct);
        await inner.DecideReturnAsync(request, ct);
    }

    public ReturnDecisionRule DescribeReturnDecision(ReturnDecisionKind kind) => inner.DescribeReturnDecision(kind);

    public Task<ChannelCatalogPage> FetchCatalogAsync(string? cursor, CancellationToken ct) => inner.FetchCatalogAsync(cursor, ct);

    public Task<ChannelReferenceList> FetchReferenceAsync(ChannelReferenceType type, CancellationToken ct) => inner.FetchReferenceAsync(type, ct);
}
