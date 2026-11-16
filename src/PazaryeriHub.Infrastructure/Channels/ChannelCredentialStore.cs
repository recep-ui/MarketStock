using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.Channels;

/// <summary>
/// Kanal kimlik bilgilerini <see cref="IDataProtector"/> ile şifreleyip <c>Channel.KimlikSifreli</c>'de saklar (ADR-016).
/// Anahtar halkası kaybolursa (ör. farklı makine) bilgiler çözülemez; panelden yeniden girilmesi gerekir.
/// </summary>
public sealed class ChannelCredentialStore(
    SqlConnectionFactory connectionFactory,
    IDataProtectionProvider dataProtection,
    ILogger<ChannelCredentialStore> logger) : IChannelCredentialStore
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("PazaryeriHub.ChannelCredentials.v1");

    public async Task<string?> GetJsonAsync(string channelCode, CancellationToken ct)
    {
        await using var connection = connectionFactory.Create();
        var encrypted = await connection.ExecuteScalarAsync<byte[]?>(new CommandDefinition(
            "SELECT KimlikSifreli FROM dbo.Channel WHERE Kod = @channelCode", new { channelCode }, cancellationToken: ct));
        if (encrypted is null)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(_protector.Unprotect(encrypted));
        }
        catch (CryptographicException)
        {
            logger.LogError("{Kanal} kimlik bilgisi çözülemedi (şifreleme anahtarı değişmiş olabilir); panelden yeniden girilmeli.", channelCode);
            throw new ChannelApiException(channelCode, "Kanal API bilgileri okunamadı; Ayarlar ekranından yeniden girin.");
        }
    }

    public async Task SaveJsonAsync(string channelCode, string json, CancellationToken ct)
    {
        var encrypted = _protector.Protect(Encoding.UTF8.GetBytes(json));
        await using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.Channel SET KimlikSifreli = @encrypted WHERE Kod = @channelCode", new { channelCode, encrypted }, cancellationToken: ct));
        if (affected == 0)
        {
            throw new NotFoundException($"Kanal bulunamadı: {channelCode}");
        }
        logger.LogInformation("{Kanal} API kimlik bilgileri güncellendi.", channelCode);
    }

    public async Task ClearAsync(string channelCode, CancellationToken ct)
    {
        await using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.Channel SET KimlikSifreli = NULL WHERE Kod = @channelCode", new { channelCode }, cancellationToken: ct));
        logger.LogInformation("{Kanal} API kimlik bilgileri silindi.", channelCode);
    }
}
