using System.Net;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using PazaryeriHub.Adapters.Mock;
using PazaryeriHub.Adapters.Trendyol;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Infrastructure.Channels;

namespace PazaryeriHub.Api.Channels;

/// <summary>
/// Kanal adaptörlerinin seçimi (bileşim kökü). "Kanallar:{KOD}:Mod" = Mock (varsayılan) | Gercek.
/// Gerçek adaptörü olmayan kanal (şimdilik Hepsiburada, Beymen, softcotton) her zaman Mock'tur.
/// </summary>
public static class ChannelAdapterRegistration
{
    public static IServiceCollection AddChannelAdapters(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<MockChannelStore>();

        foreach (var code in ChannelCodes.All)
        {
            var mode = Enum.TryParse<ChannelMode>(configuration[$"Kanallar:{code}:Mod"], ignoreCase: true, out var m) ? m : ChannelMode.Mock;
            if (mode == ChannelMode.Gercek && code == TrendyolAdapter.Code)
            {
                AddTrendyol(services);
            }
            else
            {
                // TODO: doğrulanmalı — Hepsiburada/Beymen gerçek adaptörleri sırayla eklenecek; softcotton altyapısı bilinmiyor.
                services.AddSingleton<IChannelAdapter>(sp => new MockChannelAdapter(code, sp.GetRequiredService<MockChannelStore>()));
            }
        }
        return services;
    }

    /// <summary>
    /// Trendyol HTTP istemcisi: loglama → hız sınırı → yeniden deneme → deneme başına zaman aşımı.
    /// Hız sınırı: dokümana göre aynı uca 10 sn'de en fazla 50 istek; güvenlik payıyla 40 (kayan pencere).
    /// Yeniden deneme: 429/5xx/ağ hatası için 3 deneme, üstel + jitter, Retry-After başlığına uyulur.
    /// Bunun üstünde Hangfire düzeyinde kayıt bazlı yeniden deneme (5 deneme → HATA) vardır.
    /// </summary>
    private static void AddTrendyol(IServiceCollection services)
    {
        services.AddHttpClient<TrendyolAdapter>(client => client.Timeout = TimeSpan.FromMinutes(2))
            .AddHttpMessageHandler(sp => new ChannelCallLoggingHandler(TrendyolAdapter.Code, sp.GetRequiredService<ILogger<ChannelCallLoggingHandler>>()))
            .AddResilienceHandler("trendyol", builder =>
            {
                builder.AddRateLimiter(new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = 40,
                    Window = TimeSpan.FromSeconds(10),
                    SegmentsPerWindow = 10,
                    QueueLimit = 500,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }));
                builder.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = TimeSpan.FromSeconds(2),
                    ShouldRetryAfterHeader = true,
                    ShouldHandle = args => ValueTask.FromResult(args.Outcome switch
                    {
                        { Exception: HttpRequestException } => true,
                        { Result.StatusCode: HttpStatusCode.TooManyRequests } => true,
                        { Result: { } r } when (int)r.StatusCode >= 500 => true,
                        _ => false
                    })
                });
                builder.AddTimeout(TimeSpan.FromSeconds(30));
            });
        services.AddTransient<IChannelAdapter>(sp => sp.GetRequiredService<TrendyolAdapter>());
    }
}
