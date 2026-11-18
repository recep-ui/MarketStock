using PazaryeriHub.Application.Common;

namespace PazaryeriHub.Application.Questions;

/// <summary>Kanaldaki soru durumunun normalize hali.</summary>
public enum QuestionChannelState
{
    /// <summary>Cevap bekliyor (cevaplanabilir).</summary>
    Waiting,

    /// <summary>Cevaplandı (veya cevap kanal onayında).</summary>
    Answered,

    /// <summary>Kapandı (süre doldu, sorun bildirildi, reddedildi) — cevaplanamaz.</summary>
    Closed
}

/// <summary>
/// Kanaldan gelen müşteri sorusu (ADR-029). KVKK: müşteri adı / kimliği / iletişim bilgisi TAŞINMAZ; yalnızca soru metni ve ürün.
/// </summary>
/// <param name="QuestionNo">Kanalın soru kimliği (idempotentlik anahtarı).</param>
/// <param name="ProductCode">Kanalın ürün tanımı (barkod, model kodu…); SKU eşlemesi için.</param>
/// <param name="AnswerDeadlineUtc">Kanalın son cevap tarihi (varsa).</param>
/// <param name="AnswerText">Kanalda görünen cevap (varsa).</param>
public sealed record NormalizedQuestion(
    string QuestionNo, string Text, string? ProductName, string? ProductCode, DateTime CreatedUtc,
    string? ChannelStatus, QuestionChannelState State, DateTime? AnswerDeadlineUtc = null, string? AnswerText = null);

/// <summary>Kanala gidecek cevap (KanalKomutGonder → adaptör).</summary>
public sealed record QuestionAnswerRequest(string QuestionNo, string Text);

/// <summary>Kanalın cevap kuralı (statik).</summary>
public sealed record QuestionAnswerRule(bool Supported, int MinLength, int MaxLength, string? Note)
{
    public static QuestionAnswerRule NotSupported(string note) => new(false, 0, 0, note);
}

public static class QuestionStates
{
    public const string Waiting = "CEVAP_BEKLIYOR";
    public const string Answered = "CEVAPLANDI";
    public const string Closed = "KAPANDI";

    public static string From(QuestionChannelState state) => state switch
    {
        QuestionChannelState.Waiting => Waiting,
        QuestionChannelState.Answered => Answered,
        _ => Closed
    };
}

public sealed record QuestionListItem(
    long Id, int ChannelId, string ChannelCode, string QuestionNo, string Text, string? ProductName, string? ProductCode, string? SkuCode,
    string State, string? ChannelStatus, DateTime AskedAtUtc, DateTime? DeadlineUtc, string? ChannelAnswer,
    string? LastAnswerState, DateTime LastUpdatedUtc);

/// <summary>Gönderilen her cevap (denetim izi; kullanıcı ve zaman).</summary>
public sealed record QuestionAnswerLogDto(long Id, string Text, string? User, DateTime AtUtc, long? CommandId, string? CommandState, string? CommandError);

public sealed record QuestionDetail(QuestionListItem Summary, IReadOnlyList<QuestionAnswerLogDto> Answers, QuestionAnswerRule Rule, bool CanAnswer, string? CannotAnswerReason);

/// <param name="Confirmed">Panelde "Gönderilecek cevap" onay kutusu; işaretli değilse cevap kaydedilmez.</param>
public sealed record QuestionAnswerInput(string? Text, bool Confirmed);

/// <param name="Outcome">KUYRUGA_ALINDI | ONAY_BEKLIYOR (yetenek DENEME)</param>
public sealed record QuestionAnswerResult(long LogId, long CommandId, string Outcome);

public sealed record QuestionUpsertResult(long Id, bool Created, bool Changed);

public interface IQuestionService
{
    /// <summary>Kanal sorusunu tekil kaydeder (UNIQUE(ChannelId, KanalSoruNo)); aynı soru tekrar gelirse durumu güncellenir.</summary>
    Task<QuestionUpsertResult> UpsertAsync(int channelId, NormalizedQuestion question, CancellationToken ct);

    Task<PagedResult<QuestionListItem>> SearchAsync(int? channelId, string? state, string? search, int? page, int? pageSize, CancellationToken ct);
    Task<QuestionDetail> GetAsync(long id, CancellationToken ct);

    /// <summary>
    /// Cevap (Yönetici + Destek; SoruCevap yetkisi): onay kutusu zorunlu, uzunluk kanal kuralına göre, soru cevap bekliyor olmalı,
    /// soru başına tek etkin cevap. Denetim kaydı ve SORU_CEVAP komutu AYNI transaction'da yazılır.
    /// </summary>
    Task<QuestionAnswerResult> AnswerAsync(long id, QuestionAnswerInput input, string? user, CancellationToken ct);
}
