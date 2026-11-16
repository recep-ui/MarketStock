using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PazaryeriHub.Infrastructure.Channels;

/// <summary>
/// Her dış kanal çağrısını loglar: kanal, metot, uç nokta (sorgu dizesi olmadan), süre, durum kodu (CLAUDE.md §9).
/// Başlıklar ve gövde LOGLANMAZ (Authorization, müşteri verisi vb. sızmasın).
/// </summary>
public sealed class ChannelCallLoggingHandler(string channelCode, ILogger<ChannelCallLoggingHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var endpoint = request.RequestUri is { } uri ? uri.Host + uri.AbsolutePath : "?";
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            var level = (int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                ? LogLevel.Warning
                : (int)response.StatusCode >= 400 ? LogLevel.Warning : LogLevel.Information;
            logger.Log(level, "Kanal çağrısı {Kanal} {Metot} {UcNokta} → {Durum} ({Sure} ms)",
                channelCode, request.Method.Method, endpoint, (int)response.StatusCode, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Kanal çağrısı {Kanal} {Metot} {UcNokta} başarısız: {HataTipi} ({Sure} ms)",
                channelCode, request.Method.Method, endpoint, ex.GetType().Name, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }
}
