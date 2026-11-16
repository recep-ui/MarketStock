namespace PazaryeriHub.Application.Channels;

/// <summary>
/// Kanal API kimlik bilgilerinin saklanması (CLAUDE.md §9). Değer JSON olarak, <c>Channel.KimlikSifreli</c> kolonunda
/// <c>IDataProtector</c> ile şifreli tutulur. Düz değer hiçbir API yanıtına, loga veya repoya yazılmaz.
/// Her adaptör kendi JSON şemasını tanımlar ve doğrular.
/// </summary>
public interface IChannelCredentialStore
{
    /// <summary>Çözülmüş kimlik bilgisi JSON'u; girilmemişse null.</summary>
    Task<string?> GetJsonAsync(string channelCode, CancellationToken ct);

    Task SaveJsonAsync(string channelCode, string json, CancellationToken ct);

    Task ClearAsync(string channelCode, CancellationToken ct);
}

/// <summary>
/// Kanal API hatası: ham pazaryeri yanıtı kullanıcıya gösterilmeden önce sadeleştirilmiş <see cref="Exception.Message"/>'a çevrilir
/// (CLAUDE.md §6). Ham ayrıntı yalnızca loga (gizli veri içermeden) yazılır.
/// </summary>
public sealed class ChannelApiException(string channelCode, string message, int? statusCode = null, bool transient = false, Exception? inner = null)
    : Exception(message, inner)
{
    public string ChannelCode { get; } = channelCode;
    public int? StatusCode { get; } = statusCode;

    /// <summary>Geçici hata (429, 5xx, zaman aşımı): daha sonra yeniden denenebilir.</summary>
    public bool Transient { get; } = transient;
}

/// <summary>Kanal adaptörünün çalışma modu ("Kanallar:{KOD}:Mod"). Varsayılan Mock: gerçek kanal çağrılmaz.</summary>
public enum ChannelMode
{
    Mock,
    Gercek
}
