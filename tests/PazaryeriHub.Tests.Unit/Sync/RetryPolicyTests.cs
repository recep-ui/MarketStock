using PazaryeriHub.Application.Sync;

namespace PazaryeriHub.Tests.Unit.Sync;

/// <summary>Aşama 4: üstel geri çekilme ve 5 denemeden sonra HATA (ilke 5).</summary>
public sealed class RetryPolicyTests
{
    private static readonly SyncOptions Defaults = new();

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    public void Basarisiz_denemeden_sonra_bekleme_ikiye_katlanir(int failedAttempts, int expectedSeconds) =>
        RetryPolicy.NextDelay(failedAttempts, Defaults).Should().Be(TimeSpan.FromSeconds(expectedSeconds));

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void Besinci_basarisizliktan_sonra_artik_denenmez(int failedAttempts) =>
        RetryPolicy.NextDelay(failedAttempts, Defaults).Should().BeNull();

    [Fact]
    public void Bekleme_ust_siniri_asilmaz()
    {
        var options = new SyncOptions { MaxAttempts = 20, BaseBackoffSeconds = 30, MaxBackoffMinutes = 30 };

        RetryPolicy.NextDelay(15, options).Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Varsayilan_ayarlar_spesifikasyonla_uyumlu()
    {
        Defaults.MaxAttempts.Should().Be(5);
        Defaults.OutboxCron.Should().Be("*/10 * * * * *", "OutboxIsle 10 sn");
    }
}
