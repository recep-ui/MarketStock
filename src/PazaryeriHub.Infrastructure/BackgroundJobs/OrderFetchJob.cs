using Dapper;
using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.BackgroundJobs;

/// <summary>
/// SiparisCek (CLAUDE.md §7.1): kanaldan siparişleri örtüşmeli pencereyle çeker ve idempotent kaydeder.
/// <list type="bullet">
/// <item>Pencere başı = son başarılı çalışmanın başlangıcı - <see cref="OrderOptions.OverlapMinutes"/>.</item>
/// <item>Aynı kanal için iki çalışma eşzamanlı olamaz (DisableConcurrentExecution).</item>
/// <item>Otomatik yeniden deneme yok: tekrarlayan job zaten 1-2 dk'da bir çalışır; hata ChannelJobState'e yazılır.</item>
/// <item>Geçersiz sipariş verisi (doğrulama hatası) atlanır ve raporlanır; beklenmeyen hata olursa pencere ilerletilmez
/// (bir sonraki çalışma aynı aralığı tekrar çeker; kayıt idempotent olduğu için güvenli).</item>
/// </list>
/// </summary>
public sealed class OrderFetchJob(
    IEnumerable<IChannelAdapter> adapters,
    IOrderIngestionService ingestion,
    SqlConnectionFactory connectionFactory,
    IOptions<OrderOptions> options,
    TimeProvider clock,
    ILogger<OrderFetchJob> logger)
{
    public const string JobType = "SIPARIS_CEK";

    public static string RecurringJobId(string channelCode) => "siparis-cek-" + channelCode.ToLowerInvariant();

    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 0)]
    public async Task<OrderFetchResult> RunAsync(string channelCode, CancellationToken ct)
    {
        var adapter = adapters.FirstOrDefault(a => a.ChannelCode == channelCode)
                      ?? throw new InvalidOperationException($"'{channelCode}' için kanal adaptörü kayıtlı değil.");

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var channel = await connection.QuerySingleOrDefaultAsync<(int Id, bool Aktif)?>(new CommandDefinition(
            "SELECT Id, Aktif FROM dbo.Channel WHERE Kod = @channelCode", new { channelCode }, cancellationToken: ct));
        if (channel is null)
        {
            return Skipped(channelCode, "Kanal veritabanında yok.");
        }
        if (!channel.Value.Aktif)
        {
            logger.LogDebug("SiparisCek atlandı: {Kanal} pasif.", channelCode);
            return Skipped(channelCode, "Kanal pasif.");
        }

        var channelId = channel.Value.Id;
        var startedAt = clock.GetUtcNow().UtcDateTime;
        var lastSuccess = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT SonBasarili FROM dbo.ChannelJobState WHERE ChannelId = @channelId AND IsTipi = @JobType",
            new { channelId, JobType }, cancellationToken: ct));
        var windowStart = (lastSuccess ?? startedAt.AddHours(-options.Value.InitialLookbackHours))
            .AddMinutes(-options.Value.OverlapMinutes);

        IReadOnlyList<NormalizedOrder> orders;
        try
        {
            orders = await adapter.FetchOrdersAsync(windowStart, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await SaveStateAsync(connection, channelId, startedAt, success: false, error: $"Siparişler çekilemedi: {ex.Message}", ct);
            logger.LogError(ex, "SiparisCek başarısız: {Kanal}", channelCode);
            throw;
        }

        int created = 0, updated = 0, invalid = 0, failed = 0, insufficient = 0;
        var problems = new List<string>();
        foreach (var order in orders)
        {
            try
            {
                var result = await ingestion.IngestAsync(channelCode, order, ct);
                if (result.Created) created++;
                else if (result.StatusChanged) updated++;
                if (result.InsufficientStock) insufficient++;
                if (result.RejectedTransition is not null) problems.Add($"{order.ChannelOrderNo}: {result.RejectedTransition}");
            }
            catch (RequestValidationException ex)
            {
                invalid++;
                problems.Add($"{order.ChannelOrderNo}: geçersiz veri ({string.Join("; ", ex.Errors.Select(e => $"{e.Key}: {string.Join(", ", e.Value)}"))})");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                problems.Add($"{order.ChannelOrderNo}: işlenemedi");
                logger.LogError(ex, "Sipariş işlenemedi. Kanal {Kanal}, sipariş {SiparisNo}", channelCode, order.ChannelOrderNo);
            }
        }

        var error = problems.Count == 0 ? null : string.Join(" | ", problems.Take(10)) + (problems.Count > 10 ? $" (+{problems.Count - 10})" : "");
        await SaveStateAsync(connection, channelId, startedAt, success: failed == 0, error, ct);

        logger.LogInformation(
            "SiparisCek {Kanal}: pencere {PencereBasi:o}, {Cekilen} çekildi, {Yeni} yeni, {Guncellenen} güncellendi, {Gecersiz} geçersiz, {Hatali} hatalı, {StokYetersiz} stok yetersiz",
            channelCode, windowStart, orders.Count, created, updated, invalid, failed, insufficient);

        return new OrderFetchResult(channelCode, false, null, windowStart, orders.Count, created, updated, invalid + failed, insufficient);
    }

    private static OrderFetchResult Skipped(string channelCode, string reason) => new(channelCode, true, reason, null, 0, 0, 0, 0, 0);

    /// <summary>Başarıda SonBasarili = bu çalışmanın BAŞLANGICI (çalışma sırasında düşen siparişler bir sonraki pencereye girer).</summary>
    private static Task SaveStateAsync(Microsoft.Data.SqlClient.SqlConnection connection, int channelId, DateTime startedAt, bool success, string? error, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.ChannelJobState
               SET SonDeneme = @startedAt,
                   SonBasarili = CASE WHEN @success = 1 THEN @startedAt ELSE SonBasarili END,
                   SonHata = @error,
                   ArdisikHata = CASE WHEN @success = 1 THEN 0 ELSE ArdisikHata + 1 END
             WHERE ChannelId = @channelId AND IsTipi = @JobType;
            IF @@ROWCOUNT = 0
                INSERT dbo.ChannelJobState (ChannelId, IsTipi, SonBasarili, SonDeneme, SonHata, ArdisikHata)
                VALUES (@channelId, @JobType, CASE WHEN @success = 1 THEN @startedAt END, @startedAt, @error, CASE WHEN @success = 1 THEN 0 ELSE 1 END);
            """, new { channelId, JobType, startedAt, success, error = Truncate(error, 1000) }, cancellationToken: ct));

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
