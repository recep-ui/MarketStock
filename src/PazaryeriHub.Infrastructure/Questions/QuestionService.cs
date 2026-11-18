using System.Text.Json;
using Dapper;
using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Questions;
using PazaryeriHub.Infrastructure.Channels;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.Questions;

/// <summary>
/// Müşteri soruları (ADR-029). Sorular SoruCek ile tekil kaydedilir; cevap Yönetici veya Destek tarafından yazılır ve SORU_CEVAP kanal
/// komutu olarak KanalKomutGonder'den gider. KVKK: müşteri adı/kimliği/iletişim bilgisi tutulmaz.
/// </summary>
public sealed class QuestionService(
    SqlConnectionFactory connectionFactory,
    ChannelCommandStore commands,
    IEnumerable<IChannelAdapter> adapters,
    ILogger<QuestionService> logger) : IQuestionService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<QuestionUpsertResult> UpsertAsync(int channelId, NormalizedQuestion q, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(q);
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var args = new
        {
            channelId, no = Clip(q.QuestionNo, 100), text = Clip(q.Text, 4000), product = Clip(q.ProductName, 500), code = Clip(q.ProductCode, 100),
            state = QuestionStates.From(q.State), channelStatus = Clip(q.ChannelStatus, 50), answer = Clip(q.AnswerText, 4000),
            asked = q.CreatedUtc, deadline = q.AnswerDeadlineUtc
        };
        // MERGE: aynı soru kaç kez gelirse gelsin tek kayıt; değişiklik yoksa güncelleme yazılmaz.
        var row = await connection.QuerySingleAsync<(long Id, string Action, bool Changed)>(new CommandDefinition("""
            DECLARE @out TABLE (Id BIGINT, Act NVARCHAR(10), Changed BIT);
            MERGE dbo.CustomerQuestion WITH (HOLDLOCK) AS t
            USING (SELECT @channelId AS ChannelId, @no AS KanalSoruNo) AS s ON t.ChannelId = s.ChannelId AND t.KanalSoruNo = s.KanalSoruNo
            WHEN MATCHED AND (t.Durum <> @state OR ISNULL(t.KanalDurum, N'') <> ISNULL(@channelStatus, N'') OR ISNULL(t.KanalCevabi, N'') <> ISNULL(@answer, N'')
                              OR ISNULL(t.SonCevapTarihi, '19000101') <> ISNULL(@deadline, '19000101'))
                THEN UPDATE SET Durum = @state, KanalDurum = @channelStatus, KanalCevabi = @answer, SonCevapTarihi = @deadline, SonGuncelleme = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (ChannelId, KanalSoruNo, SoruMetni, UrunAdi, UrunKodu, SkuId, Durum, KanalDurum, KanalCevabi, SoruTarihi, SonCevapTarihi)
                VALUES (@channelId, @no, @text, @product, @code,
                        (SELECT TOP (1) l.SkuId FROM dbo.ChannelListing l WHERE l.ChannelId = @channelId AND (l.KanalBarkod = @code OR l.KanalUrunKodu = @code)),
                        @state, @channelStatus, @answer, @asked, @deadline)
            OUTPUT inserted.Id, $action, 1 INTO @out;
            IF NOT EXISTS (SELECT 1 FROM @out)
                SELECT Id, N'NONE' AS Action, CAST(0 AS BIT) AS Changed FROM dbo.CustomerQuestion WHERE ChannelId = @channelId AND KanalSoruNo = @no;
            ELSE
                SELECT Id, Act AS Action, Changed FROM @out;
            """, args, cancellationToken: ct));
        return new QuestionUpsertResult(row.Id, row.Action == "INSERT", row.Action == "UPDATE");
    }

    private const string ListSelect = """
        SELECT q.Id, q.ChannelId, c.Kod AS ChannelCode, q.KanalSoruNo AS QuestionNo, q.SoruMetni AS Text, q.UrunAdi AS ProductName,
               q.UrunKodu AS ProductCode, s.SkuKodu AS SkuCode, q.Durum AS State, q.KanalDurum AS ChannelStatus, q.SoruTarihi AS AskedAtUtc,
               q.SonCevapTarihi AS DeadlineUtc, q.KanalCevabi AS ChannelAnswer,
               (SELECT TOP (1) k.Durum FROM dbo.QuestionAnswerLog a JOIN dbo.ChannelCommand k ON k.Id = a.CommandId
                 WHERE a.QuestionId = q.Id ORDER BY a.Id DESC) AS LastAnswerState,
               q.SonGuncelleme AS LastUpdatedUtc
          FROM dbo.CustomerQuestion q
          JOIN dbo.Channel c ON c.Id = q.ChannelId
          LEFT JOIN dbo.Sku s ON s.Id = q.SkuId
        """;

    public async Task<PagedResult<QuestionListItem>> SearchAsync(int? channelId, string? state, string? search, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var args = new
        {
            channelId, state = Text.Clean(state)?.ToUpperInvariant(),
            search = Text.Clean(search) is { } s ? "%" + s + "%" : null, skip = (p - 1) * size, size
        };
        const string where = """
            WHERE (@channelId IS NULL OR q.ChannelId = @channelId) AND (@state IS NULL OR q.Durum = @state)
              AND (@search IS NULL OR q.SoruMetni LIKE @search OR q.UrunAdi LIKE @search OR q.KanalSoruNo LIKE @search)
            """;
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition($"SELECT COUNT(*) FROM dbo.CustomerQuestion q {where}", args, cancellationToken: ct));
        // Cevap bekleyenler önce, son cevap tarihi en yakın olan üstte
        var rows = await connection.QueryAsync<ListRow>(new CommandDefinition($"""
            {ListSelect} {where}
            ORDER BY CASE WHEN q.Durum = 'CEVAP_BEKLIYOR' THEN 0 ELSE 1 END, ISNULL(q.SonCevapTarihi, '99991231'), q.SoruTarihi DESC, q.Id DESC
            OFFSET @skip ROWS FETCH NEXT @size ROWS ONLY
            """, args, cancellationToken: ct));
        return new PagedResult<QuestionListItem>(rows.Select(r => r.ToDto()).ToList(), total, p, size);
    }

    public async Task<QuestionDetail> GetAsync(long id, CancellationToken ct)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var head = await connection.QuerySingleOrDefaultAsync<ListRow>(new CommandDefinition($"{ListSelect} WHERE q.Id = @id", new { id }, cancellationToken: ct))
                   ?? throw new NotFoundException($"Soru bulunamadı (Id={id}).");
        var logs = await connection.QueryAsync<LogRow>(new CommandDefinition("""
            SELECT a.Id, a.CevapMetni, a.Kullanici, a.Tarih, a.CommandId, k.Durum AS CommandState, k.Hata AS CommandError
              FROM dbo.QuestionAnswerLog a LEFT JOIN dbo.ChannelCommand k ON k.Id = a.CommandId
             WHERE a.QuestionId = @id ORDER BY a.Id
            """, new { id }, cancellationToken: ct));
        var rule = Rule(head.ChannelCode);
        var summary = head.ToDto();
        var reason = await CannotAnswerReasonAsync(connection, summary, rule, ct);
        return new QuestionDetail(summary,
            logs.Select(l => new QuestionAnswerLogDto(l.Id, l.CevapMetni, l.Kullanici, DateTime.SpecifyKind(l.Tarih, DateTimeKind.Utc), l.CommandId, l.CommandState, l.CommandError)).ToList(),
            rule, reason is null, reason);
    }

    public async Task<QuestionAnswerResult> AnswerAsync(long id, QuestionAnswerInput input, string? user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var head = await connection.QuerySingleOrDefaultAsync<ListRow>(new CommandDefinition($"{ListSelect} WHERE q.Id = @id", new { id }, cancellationToken: ct))
                   ?? throw new NotFoundException($"Soru bulunamadı (Id={id}).");
        var rule = Rule(head.ChannelCode);
        var text = input.Text?.Trim() ?? "";

        var v = new Validator();
        v.Must(input.Confirmed, "confirmed", "Gönderilecek cevabı onaylayın (onay kutusu).");
        v.Must(text.Length >= rule.MinLength && text.Length <= rule.MaxLength, "text", $"Cevap {rule.MinLength}–{rule.MaxLength} karakter olmalı ({text.Length}).");
        v.ThrowIfInvalid();
        if (await CannotAnswerReasonAsync(connection, head.ToDto(), rule, ct) is { } reason)
        {
            throw new ConflictException(reason);
        }

        var payload = JsonSerializer.Serialize(new QuestionAnswerPayload(id, new QuestionAnswerRequest(head.QuestionNo, text)), Json);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        // Soru başına tek etkin cevap: iptal edilmedikçe ikinci komut oluşmaz (IdempotencyKey).
        var created = await commands.CreateAsync(connection, tx, new NewChannelCommand(head.ChannelId, ChannelCommandTypes.QuestionAnswer,
            $"QUESTION:{id}", payload, $"{ChannelCommandTypes.QuestionAnswer}:{id}", user), ct);
        if (created.Outcome == ChannelCommandStore.AlreadyExists)
        {
            await tx.RollbackAsync(ct);
            throw new ConflictException($"Bu soru için zaten bir cevap var (durum {created.State}). Değiştirmek için Senkron Merkezi → Kanal işlemleri'nden iptal edin.");
        }
        var logId = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT dbo.QuestionAnswerLog (QuestionId, CevapMetni, Kullanici, CommandId) OUTPUT inserted.Id VALUES (@id, @text, @user, @CommandId)
            """, new { id, text, user, CommandId = created.Id }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        logger.LogWarning("Soru cevabı yazıldı: soru {SoruId} ({Kanal} {SoruNo}), kullanıcı {Kullanici}, komut {KomutId} ({Durum})",
            id, head.ChannelCode, head.QuestionNo, user, created.Id, created.State);
        return new QuestionAnswerResult(logId, created.Id, created.State == ChannelCommandStates.AwaitingApproval ? "ONAY_BEKLIYOR" : "KUYRUGA_ALINDI");
    }

    private QuestionAnswerRule Rule(string channelCode) =>
        adapters.FirstOrDefault(a => a.ChannelCode == channelCode)?.DescribeQuestionAnswer() ?? QuestionAnswerRule.NotSupported("Kanal adaptörü kayıtlı değil.");

    private static async Task<string?> CannotAnswerReasonAsync(SqlConnection connection, QuestionListItem q, QuestionAnswerRule rule, CancellationToken ct)
    {
        if (!rule.Supported)
        {
            return rule.Note ?? "Bu kanalda soru cevabı desteklenmiyor.";
        }
        if (q.State != QuestionStates.Waiting)
        {
            return "Soru cevap beklemiyor (cevaplanmış veya kapanmış).";
        }
        if (q.DeadlineUtc is { } deadline && deadline < DateTime.UtcNow)
        {
            return "Son cevap tarihi geçti.";
        }
        var pending = await connection.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT TOP (1) k.Durum FROM dbo.QuestionAnswerLog a JOIN dbo.ChannelCommand k ON k.Id = a.CommandId
             WHERE a.QuestionId = @Id AND k.Durum <> 'IPTAL' ORDER BY a.Id DESC
            """, new { q.Id }, cancellationToken: ct));
        if (pending is not null)
        {
            return $"Bu soru için zaten bir cevap var (kanal işlemi: {pending}).";
        }
        var capability = await ChannelWriteGate.StateAsync(connection, q.ChannelId, ChannelCapabilities.QuestionAnswer, ct);
        return capability == CapabilityStates.Closed
            ? "Kanalın soru cevabı yazma yeteneği kapalı (Ayarlar → Kanal yazma yetenekleri)."
            : null;
    }

    private static string? Clip(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];

    private sealed class ListRow
    {
        public long Id { get; init; }
        public int ChannelId { get; init; }
        public string ChannelCode { get; init; } = "";
        public string QuestionNo { get; init; } = "";
        public string Text { get; init; } = "";
        public string? ProductName { get; init; }
        public string? ProductCode { get; init; }
        public string? SkuCode { get; init; }
        public string State { get; init; } = "";
        public string? ChannelStatus { get; init; }
        public DateTime AskedAtUtc { get; init; }
        public DateTime? DeadlineUtc { get; init; }
        public string? ChannelAnswer { get; init; }
        public string? LastAnswerState { get; init; }
        public DateTime LastUpdatedUtc { get; init; }

        public QuestionListItem ToDto() => new(Id, ChannelId, ChannelCode, QuestionNo, Text, ProductName, ProductCode, SkuCode, State, ChannelStatus,
            DateTime.SpecifyKind(AskedAtUtc, DateTimeKind.Utc), DeadlineUtc is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null,
            ChannelAnswer, LastAnswerState, DateTime.SpecifyKind(LastUpdatedUtc, DateTimeKind.Utc));
    }

    private sealed class LogRow
    {
        public long Id { get; init; }
        public string CevapMetni { get; init; } = "";
        public string? Kullanici { get; init; }
        public DateTime Tarih { get; init; }
        public long? CommandId { get; init; }
        public string? CommandState { get; init; }
        public string? CommandError { get; init; }
    }
}

public sealed record QuestionAnswerPayload(long QuestionId, QuestionAnswerRequest Request);

/// <summary>SORU_CEVAP komutunu kanala gönderir (KanalKomutGonder; gerçek adaptör yazma kapısıyla sarılı).</summary>
public sealed class QuestionAnswerCommandHandler : IChannelCommandHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Type => ChannelCommandTypes.QuestionAnswer;

    public async Task<string?> ExecuteAsync(IChannelAdapter adapter, string payloadJson, CancellationToken ct)
    {
        var p = JsonSerializer.Deserialize<QuestionAnswerPayload>(payloadJson, Json) ?? throw new ArgumentException("Soru cevap komutunun içeriği okunamadı.");
        await adapter.AnswerQuestionAsync(p.Request, ct);
        return null;
    }
}

/// <summary>
/// SoruCek (ADR-029): kanal başına soruları çeker, tekil kaydeder. Pencere: son başarılı çalışmanın başlangıcı − 1 saat (örtüşmeli);
/// ilk çalışmada 7 gün. Soru API'si doğrulanmamış kanal (Hepsiburada) atlanır, hata sayılmaz. Hata durumunda pencere ilerlemez.
/// </summary>
public sealed class QuestionFetchJob(
    IEnumerable<IChannelAdapter> adapters,
    IQuestionService questions,
    SqlConnectionFactory connectionFactory,
    TimeProvider clock,
    ILogger<QuestionFetchJob> logger)
{
    public const string JobType = "SORU_CEK";

    public static string RecurringJobId(string channelCode) => "soru-cek-" + channelCode.ToLowerInvariant();

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

        IReadOnlyList<NormalizedQuestion> fetched;
        try
        {
            fetched = await adapter.FetchQuestionsAsync(since, ct);
        }
        catch (NotSupportedException ex)
        {
            await SaveStateAsync(connection, channel.Value.Id, startedAt, success: true, error: null, ct);
            return new RunResult(channelCode, true, ex.Message, 0, 0, 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = ex is ChannelApiException ? ex.Message : "Sorular çekilemedi (ayrıntı logda).";
            await SaveStateAsync(connection, channel.Value.Id, startedAt, success: false, error: message, ct);
            logger.LogError(ex, "SoruCek başarısız: {Kanal}", channelCode);
            throw;
        }

        int created = 0, changed = 0;
        foreach (var q in fetched)
        {
            var result = await questions.UpsertAsync(channel.Value.Id, q, ct);
            created += result.Created ? 1 : 0;
            changed += result.Changed ? 1 : 0;
        }
        await SaveStateAsync(connection, channel.Value.Id, startedAt, success: true, error: null, ct);
        if (fetched.Count > 0)
        {
            logger.LogInformation("SoruCek {Kanal}: {Adet} soru, {Yeni} yeni, {Degisen} güncellendi.", channelCode, fetched.Count, created, changed);
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
