using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;

namespace PazaryeriHub.Application.Orders;

/// <summary>Kanal iade talebi kararları (ADR-027). Yalnızca Yönetici verir; her karar kullanıcı adıyla ReturnDecision'a yazılır.</summary>
public static class ReturnDecisions
{
    public const string Approve = "ONAY";
    public const string Reject = "RED";
    public const string Received = "TESLIM_ALINDI";

    public static IReadOnlyList<string> All { get; } = [Approve, Reject, Received];

    public static ReturnDecisionKind ToKind(string decision) => decision switch
    {
        Approve => ReturnDecisionKind.Approve,
        Reject => ReturnDecisionKind.Reject,
        Received => ReturnDecisionKind.Received,
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, null)
    };
}

public sealed record ReturnListItem(
    long Id, int ChannelId, string ChannelCode, string ReturnNo, string ChannelOrderNo, long? OrderId, string? ChannelStatus, string? Reason,
    DateTime RequestedAtUtc, DateTime LastUpdatedUtc, int LineCount, int ReceivedLineCount, string? LastDecision, string? LastDecisionState);

/// <param name="ReceiveProblem">Depoya giriş yapılamıyorsa nedeni (sipariş eşleşmedi, sevk yok, zaten girildi…).</param>
public sealed record ReturnLineDto(
    long Id, string LineKey, string? Barcode, string? ChannelProductCode, long? OrderLineId, int? SkuId, string? SkuCode, int Quantity,
    bool Received, string? ReceiveProblem);

public sealed record ReturnDecisionDto(
    long Id, string Decision, string? ReasonCode, string? Description, string? User, DateTime AtUtc, long? CommandId, string? CommandState, string? CommandError);

public sealed record ReturnDecisionRuleDto(string Decision, ReturnDecisionRule Rule);

public sealed record ReturnDetail(
    ReturnListItem Summary, IReadOnlyList<ReturnLineDto> Lines, IReadOnlyList<ReturnDecisionDto> Decisions, IReadOnlyList<ReturnDecisionRuleDto> Rules);

public sealed record ReturnDecisionInput(string Decision, string? ReasonCode, string? Description);

/// <param name="Outcome">KUYRUGA_ALINDI | ONAY_BEKLIYOR | ZATEN_VAR</param>
public sealed record ReturnDecisionResult(long? DecisionId, long CommandId, string Outcome, string? Message);

public sealed record ReturnUpsertResult(long Id, bool Created, bool Changed);

public interface IReturnService
{
    Task<PagedResult<ReturnListItem>> SearchAsync(int? channelId, string? search, int? page, int? pageSize, CancellationToken ct);

    Task<ReturnDetail> GetAsync(long id, CancellationToken ct);

    /// <summary>İade satırının malı depoya ulaştı (usp_IadeGiris; KaynakId RL:{satır}). Kanala karar bildirimini tetiklemez.</summary>
    Task<ReturnDetail> ReceiveLineAsync(long returnId, long lineId, string? user, CancellationToken ct);

    /// <summary>Yönetici kararı → IADE_KARAR kanal komutu (ADR-026 kuyruğu) + denetim kaydı.</summary>
    Task<ReturnDecisionResult> DecideAsync(long returnId, ReturnDecisionInput input, string? user, CancellationToken ct);

    /// <summary>Kanaldan gelen talebi tekil kaydeder (UNIQUE ChannelId + KanalIadeNo); IadeCek job'ı çağırır.</summary>
    Task<ReturnUpsertResult?> UpsertAsync(int channelId, NormalizedReturn request, CancellationToken ct);
}
