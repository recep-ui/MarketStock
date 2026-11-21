using System.Diagnostics;

namespace PazaryeriHub.Tools.HbSit;

/// <summary>Kaydedilen bir HTTP çağrısı (Authorization başlığı YOK).</summary>
public sealed record RecordedCall(DateTime AtUtc, string Step, string Method, string Url, string? RequestBody, int Status, string ResponseBody, long ElapsedMs, string? RequestContentType = null);

/// <summary>
/// Tüm istekleri (yeni SIT istemcisi + mevcut Hepsiburada adaptörü) kaydeder ve <b>yalnızca Hepsiburada test (SIT) adreslerine</b>
/// izin verir: canlı adrese giden her istek gönderilmeden reddedilir. Kimlik bilgisi (Authorization) kayda yazılmaz.
/// </summary>
public sealed class SitRecorder : DelegatingHandler
{
    private readonly List<RecordedCall> _calls = [];

    public SitRecorder() : base(new HttpClientHandler()) { }

    /// <summary>Sıradaki çağrıların rapordaki adım adı.</summary>
    public string CurrentStep { get; set; } = "";

    public IReadOnlyList<RecordedCall> Calls => _calls;

    public static bool IsSitHost(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.Host.EndsWith("-sit.hepsiburada.com", StringComparison.OrdinalIgnoreCase);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        if (!IsSitHost(uri))
        {
            throw new InvalidOperationException($"Güvenlik: yalnızca Hepsiburada test (SIT) adreslerine istek gönderilir; reddedildi: {uri.Host}");
        }

        var requestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        // Gönderilen Content-Type başlığı (charset dahil) kayda yazılır; HB charset ekini reddediyor (2026-11-21)
        var requestContentType = request.Content?.Headers.ContentType?.ToString();
        var watch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            _calls.Add(new(DateTime.UtcNow, CurrentStep, request.Method.Method, uri.ToString(), requestBody, 0, "Bağlantı hatası: " + ex.Message, watch.ElapsedMilliseconds, requestContentType));
            throw;
        }

        // Yanıt gövdesi okunup yeniden içerik olarak konur (çağıran da okuyabilsin)
        var text = response.Content is null ? "" : await response.Content.ReadAsStringAsync(ct);
        var mediaType = response.Content?.Headers.ContentType?.MediaType ?? "application/json";
        response.Content = new StringContent(text, System.Text.Encoding.UTF8, mediaType);
        _calls.Add(new(DateTime.UtcNow, CurrentStep, request.Method.Method, uri.ToString(), requestBody, (int)response.StatusCode, text, watch.ElapsedMilliseconds, requestContentType));
        return response;
    }
}
