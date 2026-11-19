using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using PazaryeriHub.Adapters.Beymen;
using PazaryeriHub.Adapters.Hepsiburada;
using PazaryeriHub.Adapters.Trendyol;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Api.Channels;

/// <summary>
/// Kanal durum kartları (ADR-033). Mod bileşim kökündeki yapılandırmadan ("Kanallar:{KOD}:Mod") okunur; API bilgisinden
/// yalnızca seçilen ortam alınır, başka hiçbir alan yanıta girmez.
/// </summary>
public sealed class ChannelStatusQuery(
    IChannelService channels,
    IChannelCapabilityService capabilities,
    IChannelCredentialStore credentials,
    IConfiguration configuration,
    IOptionsMonitor<ChannelWriteOptions> writeOptions,
    SqlConnectionFactory connectionFactory,
    ILogger<ChannelStatusQuery> logger)
{
    /// <summary>Gerçek adaptörü olan kanallar (softcotton: altyapı teyit bekliyor, her zaman mock).</summary>
    private static readonly HashSet<string> RealAdapters = [TrendyolAdapter.Code, HepsiburadaAdapter.Code, BeymenAdapter.Code];

    public async Task<IReadOnlyList<ChannelStatusDto>> GetAsync(CancellationToken ct)
    {
        var list = await channels.GetChannelsAsync(ct);
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        var jobRows = (await connection.QueryAsync<(int ChannelId, string IsTipi, DateTime? SonBasarili, DateTime? SonDeneme, int ArdisikHata, string? SonHata)>(
            new CommandDefinition("SELECT ChannelId, IsTipi, SonBasarili, SonDeneme, ArdisikHata, SonHata FROM dbo.ChannelJobState", cancellationToken: ct))).ToList();
        var locked = writeOptions.CurrentValue.YazmaKapali;

        var result = new List<ChannelStatusDto>(list.Count);
        foreach (var c in list)
        {
            var real = RealAdapters.Contains(c.Code);
            var mode = real && Enum.TryParse<ChannelMode>(configuration[$"Kanallar:{c.Code}:Mod"], ignoreCase: true, out var m) && m == ChannelMode.Gercek
                ? "GERCEK" : "MOCK";
            var environment = c.HasCredentials ? await ReadEnvironmentAsync(c.Code, ct) : null;
            var jobs = jobRows.Where(j => j.ChannelId == c.Id)
                .OrderBy(j => j.IsTipi, StringComparer.Ordinal)
                .Select(j => new ChannelJobStatus(j.IsTipi, Utc(j.SonBasarili), Utc(j.SonDeneme), j.ArdisikHata, j.SonHata))
                .ToList();
            var (health, reason) = ChannelHealth.Evaluate(c.Active, mode, real, c.HasCredentials, jobs);
            result.Add(new ChannelStatusDto(c.Id, c.Code, c.Name, c.Active, c.HasCredentials, mode, real, environment, locked,
                await capabilities.GetAsync(c.Id, ct), jobs, health, reason));
        }
        return result;
    }

    /// <summary>Şifreli API bilgisinden yalnızca "environment" alanı; çözülemezse null (hata loglanır, içerik loglanmaz).</summary>
    private async Task<string?> ReadEnvironmentAsync(string code, CancellationToken ct)
    {
        try
        {
            var json = await credentials.GetJsonAsync(code, ct);
            if (json is null) return null;
            using var doc = JsonDocument.Parse(json);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name.Equals("environment", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                {
                    return p.Value.GetString();
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Kanal durumu: {Kanal} API bilgisinin ortamı okunamadı ({Tip}).", code, ex.GetType().Name);
            return null;
        }
    }

    private static DateTime? Utc(DateTime? v) => v is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;
}
