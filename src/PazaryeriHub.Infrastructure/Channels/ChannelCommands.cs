using Dapper;
using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Sync;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.Channels;

/// <summary>
/// Kanal komut kuyruğu (ADR-026): kanala giden stok/fiyat dışı yazma işlemleri. Oluşturma anında yetenek kontrol edilir:
/// KAPALI → oluşturulmaz, DENEME → ONAY_BEKLIYOR (Yönetici tek tek onaylar), ACIK → BEKLIYOR.
/// İdempotentlik: <c>UNIQUE(IdempotencyKey) WHERE Durum &lt;&gt; 'IPTAL'</c>; aynı bildirim ikinci kez oluşmaz.
/// </summary>
public sealed class ChannelCommandStore(SqlConnectionFactory connectionFactory, ILogger<ChannelCommandStore> logger)
{
    public const string Created = "OLUSTURULDU";
    public const string AlreadyExists = "ZATEN_VAR";

    public async Task<ChannelCommandCreateResult> CreateAsync(NewChannelCommand command, CancellationToken ct)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        return await CreateAsync(connection, null, command, ct);
    }

    /// <summary>Var olan bağlantı/transaction içinde oluşturur (ör. sipariş durum geçişiyle aynı transaction).</summary>
    public async Task<ChannelCommandCreateResult> CreateAsync(SqlConnection connection, SqlTransaction? tx, NewChannelCommand command, CancellationToken ct)
    {
        var existing = await FindExistingAsync(connection, tx, command, ct);
        if (existing is { } e)
        {
            return new ChannelCommandCreateResult(e.Id, AlreadyExists, e.Durum);
        }

        var capability = ChannelCommandTypes.CapabilityOf(command.Type);
        var capabilityState = await ChannelWriteGate.StateAsync(connection, command.ChannelId, capability, ct, tx);
        if (capabilityState == CapabilityStates.Closed)
        {
            throw new ConflictException($"Kanalın '{capability}' yazma yeteneği kapalı (Ayarlar → Kanal yazma yetenekleri); kanala gönderilecek kayıt oluşturulmadı.");
        }
        var state = capabilityState == CapabilityStates.Trial ? ChannelCommandStates.AwaitingApproval : ChannelCommandStates.Pending;

        try
        {
            var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT dbo.ChannelCommand (ChannelId, Tip, HedefAnahtar, Payload, IdempotencyKey, Durum, Olusturan)
                OUTPUT inserted.Id
                VALUES (@ChannelId, @Type, @TargetKey, @PayloadJson, @IdempotencyKey, @state, @CreatedBy)
                """, new { command.ChannelId, command.Type, command.TargetKey, command.PayloadJson, command.IdempotencyKey, state, command.CreatedBy },
                tx, cancellationToken: ct));
            logger.LogInformation("Kanal komutu oluştu: {KomutId} {Tip} {Hedef} → {Durum}", id, command.Type, command.TargetKey, state);
            return new ChannelCommandCreateResult(id, Created, state);
        }
        catch (SqlException ex) when (ex.Number is DbErrors.UniqueIndex or DbErrors.UniqueConstraint)
        {
            // Eşzamanlı başka bir istek aynı komutu az önce oluşturdu.
            var again = await FindExistingAsync(connection, tx, command, ct)
                        ?? throw new InvalidOperationException("Benzersizlik ihlaline rağmen kanal komutu bulunamadı.", ex);
            return new ChannelCommandCreateResult(again.Id, AlreadyExists, again.Durum);
        }
    }

    private static Task<(long Id, string Durum)?> FindExistingAsync(SqlConnection connection, SqlTransaction? tx, NewChannelCommand command, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<(long Id, string Durum)?>(new CommandDefinition("""
            SELECT TOP (1) Id, Durum FROM dbo.ChannelCommand
             WHERE (IdempotencyKey = @IdempotencyKey AND Durum <> 'IPTAL')
                OR (ChannelId = @ChannelId AND Tip = @Type AND HedefAnahtar = @TargetKey AND Durum IN ('ONAY_BEKLIYOR', 'BEKLIYOR'))
             ORDER BY Id DESC
            """, new { command.IdempotencyKey, command.ChannelId, command.Type, command.TargetKey }, tx, cancellationToken: ct));
}

/// <summary>Panel/API: kanal işlemleri listesi ve Yönetici eylemleri (onayla, yeniden dene, iptal).</summary>
public sealed class ChannelCommandService(SqlConnectionFactory connectionFactory, ILogger<ChannelCommandService> logger) : IChannelCommandService
{
    public async Task<PagedResult<ChannelCommandDto>> SearchAsync(ChannelCommandQuery query, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page ?? 1);
        var size = Math.Clamp(query.PageSize ?? 50, 1, 500);
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var args = new { query.ChannelId, query.Type, query.State, query.TargetKey, skip = (page - 1) * size, size };
        const string where = """
            WHERE (@ChannelId IS NULL OR c.ChannelId = @ChannelId) AND (@Type IS NULL OR c.Tip = @Type)
              AND (@State IS NULL OR c.Durum = @State) AND (@TargetKey IS NULL OR c.HedefAnahtar = @TargetKey)
            """;
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition($"SELECT COUNT(*) FROM dbo.ChannelCommand c {where}", args, cancellationToken: ct));
        var rows = await connection.QueryAsync<Row>(new CommandDefinition($"""
            SELECT c.Id, c.ChannelId, ch.Kod AS ChannelCode, c.Tip, c.HedefAnahtar, c.Payload, c.Durum, c.Deneme, c.SonrakiDeneme, c.Hata,
                   c.KanalIslemNo, c.Olusturan, c.Olusma, c.Onaylayan, c.OnayTarihi, c.Gonderilme
              FROM dbo.ChannelCommand c JOIN dbo.Channel ch ON ch.Id = c.ChannelId
            {where}
             ORDER BY c.Id DESC OFFSET @skip ROWS FETCH NEXT @size ROWS ONLY
            """, args, cancellationToken: ct));
        return new PagedResult<ChannelCommandDto>(rows.Select(r => r.ToDto()).ToList(), total, page, size);
    }

    public Task<ChannelCommandDto> ApproveAsync(long id, string? user, CancellationToken ct) =>
        TransitionAsync(id, user, "onay", """
            UPDATE dbo.ChannelCommand SET Durum = 'BEKLIYOR', Onaylayan = @user, OnayTarihi = SYSUTCDATETIME(), SonrakiDeneme = NULL
             WHERE Id = @id AND Durum = 'ONAY_BEKLIYOR'
            """, "Yalnızca 'onay bekliyor' durumundaki kayıt onaylanabilir.", ct);

    public Task<ChannelCommandDto> RetryAsync(long id, string? user, CancellationToken ct) =>
        TransitionAsync(id, user, "yeniden deneme", """
            UPDATE dbo.ChannelCommand SET Durum = 'BEKLIYOR', Deneme = 0, SonrakiDeneme = NULL, Hata = NULL, TalepNo = NULL, TalepZamani = NULL
             WHERE Id = @id AND Durum = 'HATA'
            """, "Yalnızca 'hata' durumundaki kayıt yeniden denenebilir.", ct);

    public Task<ChannelCommandDto> CancelAsync(long id, string? user, CancellationToken ct) =>
        TransitionAsync(id, user, "iptal", """
            UPDATE dbo.ChannelCommand SET Durum = 'IPTAL', Hata = CONCAT(N'İptal eden: ', @user)
             WHERE Id = @id AND Durum IN ('ONAY_BEKLIYOR', 'BEKLIYOR', 'HATA') AND TalepNo IS NULL
            """, "Kayıt iptal edilemez (gönderildi, zaten iptal veya şu an gönderiliyor).", ct);

    private async Task<ChannelCommandDto> TransitionAsync(long id, string? user, string action, string sql, string conflict, CancellationToken ct)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var changed = await connection.ExecuteAsync(new CommandDefinition(sql, new { id, user }, cancellationToken: ct));
        var row = await GetRowAsync(connection, id, ct) ?? throw new NotFoundException($"Kanal işlemi bulunamadı (Id={id}).");
        if (changed == 0)
        {
            throw new ConflictException(conflict);
        }
        logger.LogWarning("Kanal işlemi {Eylem}: {KomutId} ({Tip} {Hedef}), kullanıcı {Kullanici}", action, id, row.Tip, row.HedefAnahtar, user);
        return row.ToDto();
    }

    private static Task<Row?> GetRowAsync(SqlConnection connection, long id, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<Row?>(new CommandDefinition("""
            SELECT c.Id, c.ChannelId, ch.Kod AS ChannelCode, c.Tip, c.HedefAnahtar, c.Payload, c.Durum, c.Deneme, c.SonrakiDeneme, c.Hata,
                   c.KanalIslemNo, c.Olusturan, c.Olusma, c.Onaylayan, c.OnayTarihi, c.Gonderilme
              FROM dbo.ChannelCommand c JOIN dbo.Channel ch ON ch.Id = c.ChannelId WHERE c.Id = @id
            """, new { id }, cancellationToken: ct));

    internal sealed class Row
    {
        public long Id { get; init; }
        public int ChannelId { get; init; }
        public string ChannelCode { get; init; } = "";
        public string Tip { get; init; } = "";
        public string HedefAnahtar { get; init; } = "";
        public string Payload { get; init; } = "{}";
        public string Durum { get; init; } = "";
        public int Deneme { get; init; }
        public DateTime? SonrakiDeneme { get; init; }
        public string? Hata { get; init; }
        public string? KanalIslemNo { get; init; }
        public string? Olusturan { get; init; }
        public DateTime Olusma { get; init; }
        public string? Onaylayan { get; init; }
        public DateTime? OnayTarihi { get; init; }
        public DateTime? Gonderilme { get; init; }

        public ChannelCommandDto ToDto() => new(Id, ChannelId, ChannelCode, Tip, HedefAnahtar, Payload, Durum, Deneme, Utc(SonrakiDeneme), Hata,
            KanalIslemNo, Olusturan, DateTime.SpecifyKind(Olusma, DateTimeKind.Utc), Onaylayan, Utc(OnayTarihi), Utc(Gonderilme));

        private static DateTime? Utc(DateTime? v) => v is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;
    }
}

/// <summary>
/// KanalKomutGonder (ADR-026; ilke 5): kanal başına BEKLIYOR komutları alır (READPAST, talep numarası) ve tipine göre
/// <see cref="IChannelCommandHandler"/> ile kanala gönderir. Deneme sayacı talep anında artar.
/// - Başarı → GONDERILDI.
/// - Yazma engeli (global kilit, pasif kanal, kapalı yetenek) → hata sayılmaz; kayıt BEKLIYOR kalır, 1 dk sonra yeniden bakılır.
/// - Doğrulama/desteklenmeyen işlem (ArgumentException, NotSupportedException) → hemen HATA.
/// - Diğer hatalar → üstel bekleme (SyncOptions: taban × 2^(n−1), en fazla MaxBackoff); MaxAttempts (5). başarısızlıkta HATA + uyarı.
/// </summary>
public sealed class ChannelCommandJob(
    IEnumerable<IChannelAdapter> adapters,
    IEnumerable<IChannelCommandHandler> handlers,
    SqlConnectionFactory connectionFactory,
    IOptions<SyncOptions> syncOptions,
    TimeProvider clock,
    ILogger<ChannelCommandJob> logger)
{
    public const int BatchSize = 20;

    public static string RecurringJobId(string channelCode) => "kanal-komut-gonder-" + channelCode.ToLowerInvariant();

    public sealed record RunResult(string ChannelCode, int Claimed, int Sent, int Blocked, int Retrying, int Failed);

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [AutomaticRetry(Attempts = 0)]
    public async Task<RunResult> RunAsync(string channelCode, CancellationToken ct)
    {
        var adapter = adapters.FirstOrDefault(a => a.ChannelCode == channelCode);
        if (adapter is null)
        {
            return new RunResult(channelCode, 0, 0, 0, 0, 0);
        }
        var options = syncOptions.Value;
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var channelId = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT Id FROM dbo.Channel WHERE Kod = @channelCode", new { channelCode }, cancellationToken: ct));
        if (channelId is null)
        {
            return new RunResult(channelCode, 0, 0, 0, 0, 0);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var claim = "cmd:" + Guid.NewGuid().ToString("N");
        var claimed = (await connection.QueryAsync<(long Id, string Tip, string Payload, int Deneme)>(new CommandDefinition("""
            WITH c AS (
                SELECT TOP (@BatchSize) * FROM dbo.ChannelCommand WITH (ROWLOCK, UPDLOCK, READPAST)
                 WHERE ChannelId = @channelId AND Durum = 'BEKLIYOR'
                   AND (SonrakiDeneme IS NULL OR SonrakiDeneme <= @now)
                   AND (TalepNo IS NULL OR TalepZamani < @expired)
                 ORDER BY Id)
            UPDATE c SET TalepNo = @claim, TalepZamani = @now, Deneme = Deneme + 1, SonDeneme = @now
            OUTPUT inserted.Id, inserted.Tip, inserted.Payload, inserted.Deneme;
            """, new { BatchSize, channelId, now, expired = now.AddMinutes(-options.ClaimTimeoutMinutes), claim }, cancellationToken: ct))).ToList();

        int sent = 0, blocked = 0, retrying = 0, failed = 0;
        foreach (var cmd in claimed)
        {
            var handler = handlers.FirstOrDefault(h => h.Type == cmd.Tip);
            try
            {
                if (handler is null)
                {
                    throw new NotSupportedException($"'{cmd.Tip}' tipi için gönderim işleyicisi yok.");
                }
                var operationNo = await handler.ExecuteAsync(adapter, cmd.Payload, ct);
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE dbo.ChannelCommand SET Durum = 'GONDERILDI', Gonderilme = @at, KanalIslemNo = @operationNo, Hata = NULL,
                           TalepNo = NULL, TalepZamani = NULL, SonrakiDeneme = NULL
                     WHERE Id = @Id AND TalepNo = @claim
                    """, new { cmd.Id, claim, operationNo, at = clock.GetUtcNow().UtcDateTime }, cancellationToken: ct));
                sent++;
            }
            catch (ChannelWriteBlockedException ex)
            {
                // Engel hata değildir: deneme geri alınır, kayıt bekler.
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE dbo.ChannelCommand SET Deneme = Deneme - 1, Hata = @reason, SonrakiDeneme = @next, TalepNo = NULL, TalepZamani = NULL
                     WHERE Id = @Id AND TalepNo = @claim
                    """, new { cmd.Id, claim, reason = Clip("Bekliyor: " + ex.Reason), next = now.AddMinutes(1) }, cancellationToken: ct));
                blocked++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var permanent = ex is ArgumentException or NotSupportedException;
                var message = Clip(ex switch
                {
                    ChannelApiException => ex.Message,
                    ArgumentException or NotSupportedException => ex.Message,
                    _ => "Kanala gönderilemedi (ayrıntı logda)."
                });
                var exhausted = permanent || cmd.Deneme >= options.MaxAttempts;
                var backoff = TimeSpan.FromSeconds(Math.Min(options.BaseBackoffSeconds * Math.Pow(2, cmd.Deneme - 1), options.MaxBackoffMinutes * 60.0));
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE dbo.ChannelCommand SET Durum = CASE WHEN @exhausted = 1 THEN 'HATA' ELSE 'BEKLIYOR' END, Hata = @message,
                           SonrakiDeneme = CASE WHEN @exhausted = 1 THEN NULL ELSE @next END, TalepNo = NULL, TalepZamani = NULL
                     WHERE Id = @Id AND TalepNo = @claim
                    """, new { cmd.Id, claim, exhausted, message, next = now.Add(backoff) }, cancellationToken: ct));
                if (exhausted)
                {
                    failed++;
                    logger.LogWarning(ex, "UYARI: kanal işlemi HATA durumuna düştü: {KomutId} {Tip} ({Kanal}), deneme {Deneme}: {Hata}",
                        cmd.Id, cmd.Tip, channelCode, cmd.Deneme, message);
                }
                else
                {
                    retrying++;
                    logger.LogInformation(ex, "Kanal işlemi yeniden denenecek: {KomutId} {Tip} ({Kanal}), deneme {Deneme}", cmd.Id, cmd.Tip, channelCode, cmd.Deneme);
                }
            }
        }

        if (claimed.Count > 0)
        {
            logger.LogInformation("KanalKomutGonder {Kanal}: {Alinan} alındı, {Gonderilen} gönderildi, {Engel} engelli, {Yeniden} yeniden denenecek, {Hata} hata.",
                channelCode, claimed.Count, sent, blocked, retrying, failed);
        }
        return new RunResult(channelCode, claimed.Count, sent, blocked, retrying, failed);
    }

    private static string Clip(string value) => value.Length <= 1000 ? value : value[..1000];
}
