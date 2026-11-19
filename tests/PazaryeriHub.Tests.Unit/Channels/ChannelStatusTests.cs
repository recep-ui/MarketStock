using FluentAssertions;
using PazaryeriHub.Application.Channels;
using Xunit;

namespace PazaryeriHub.Tests.Unit.Channels;

/// <summary>ADR-033: hata sınıflandırma ve kanal genel durumu (saf fonksiyonlar). Metinler adaptörlerin ürettiği sadeleştirilmiş mesajlardır.</summary>
public sealed class ChannelStatusTests
{
    [Theory]
    [InlineData("Trendyol API bilgileri hatalı (satıcı no, API Key ve API Secret'ı kontrol edin).", ChannelErrorKinds.Credentials, false)]
    [InlineData("Beymen API bilgileri girilmemiş (Ayarlar → Kanallar).", ChannelErrorKinds.Credentials, false)]
    [InlineData("Kanal API bilgileri okunamadı; Ayarlar ekranından yeniden girin.", ChannelErrorKinds.Credentials, false)]
    [InlineData("Trendyol isteği reddetti (erişim/User-Agent).", ChannelErrorKinds.AccessDenied, false)]
    [InlineData("Trendyol test ortamına erişilemedi (IP adresinin beyaz listeye eklenmesi gerekir).", ChannelErrorKinds.AccessDenied, false)]
    [InlineData("Trendyol istek sınırı aşıldı; daha sonra yeniden denenecek.", ChannelErrorKinds.RateLimit, true)]
    [InlineData("Kanal hata döndü (HTTP 503): Servis geçici olarak kullanılamıyor", ChannelErrorKinds.Unreachable, true)]
    [InlineData("Hepsiburada zamanında yanıt vermedi.", ChannelErrorKinds.Unreachable, true)]
    [InlineData("Beymen'e ulaşılamadı.", ChannelErrorKinds.Unreachable, true)]
    [InlineData("TRENDYOL kanalına yazma engellendi (STOK): Global yazma kilidi açık (Kanallar:YazmaKapali; deneme modu).", ChannelErrorKinds.WriteLocked, false)]
    [InlineData("Aynı istek 15 dakika içinde tekrar gönderilemez (Trendyol); değer değişince yeniden gönderilecek.", ChannelErrorKinds.Duplicate, true)]
    [InlineData("Soru cevaplama Hepsiburada'da desteklenmiyor.", ChannelErrorKinds.NotSupported, false)]
    [InlineData("Trendyol isteği reddetti: Barkod bulunamadı", ChannelErrorKinds.InvalidData, false)]
    [InlineData("Beklenmeyen bir şey", ChannelErrorKinds.Unknown, false)]
    public void Explain_classifies_known_adapter_messages(string error, string kind, bool transient)
    {
        var info = ChannelErrorExplainer.Explain(error)!;
        info.Kind.Should().Be(kind);
        info.Transient.Should().Be(transient);
        info.Title.Should().NotBeNullOrWhiteSpace();
        info.Advice.Should().NotBeNullOrWhiteSpace();
        info.Problems.Should().ContainSingle().Which.Should().Be(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Explain_returns_null_for_empty(string? error) => ChannelErrorExplainer.Explain(error).Should().BeNull();

    [Fact]
    public void Explain_splits_joined_problems_and_counts_the_rest()
    {
        var joined = string.Join(" | ", Enumerable.Range(1, 10).Select(i => $"Sipariş {i}: geçersiz satır")) + " (+4)";
        var info = ChannelErrorExplainer.Explain(joined)!;
        info.Problems.Should().Equal("Sipariş 1: geçersiz satır", "Sipariş 2: geçersiz satır", "Sipariş 3: geçersiz satır");
        info.MoreCount.Should().Be(7 + 4);
    }

    private static ChannelJobStatus Job(string type, int failures = 0, string? error = null, bool succeeded = true) =>
        new(type, succeeded ? DateTime.UtcNow.AddMinutes(-5) : null, DateTime.UtcNow.AddMinutes(-1), failures, error);

    [Fact]
    public void Health_inactive_channel_is_off() =>
        ChannelHealth.Evaluate(false, "GERCEK", true, true, [Job("SIPARIS_CEK")]).Health.Should().Be(ChannelHealthStates.Off);

    [Fact]
    public void Health_mock_mode_is_reported_even_with_credentials()
    {
        var (health, reason) = ChannelHealth.Evaluate(true, "MOCK", true, true, [Job("SIPARIS_CEK")]);
        health.Should().Be(ChannelHealthStates.Mock);
        reason.Should().Contain("mock");
    }

    [Fact]
    public void Health_real_mode_without_credentials_is_error() =>
        ChannelHealth.Evaluate(true, "GERCEK", true, false, []).Health.Should().Be(ChannelHealthStates.Error);

    [Fact]
    public void Health_transient_failures_are_warning_permanent_are_error()
    {
        ChannelHealth.Evaluate(true, "GERCEK", true, true, [Job("SIPARIS_CEK", 2, "Trendyol zamanında yanıt vermedi.")])
            .Should().Be((ChannelHealthStates.Warning, "Sipariş çekme: Kanala şu an ulaşılamıyor"));
        ChannelHealth.Evaluate(true, "GERCEK", true, true, [Job("SIPARIS_CEK", 1, "Trendyol API bilgileri hatalı (satıcı no, API Key ve API Secret'ı kontrol edin).")])
            .Health.Should().Be(ChannelHealthStates.Error);
    }

    [Fact]
    public void Health_never_fetched_is_warning_and_success_is_working()
    {
        ChannelHealth.Evaluate(true, "GERCEK", true, true, []).Health.Should().Be(ChannelHealthStates.Warning);
        ChannelHealth.Evaluate(true, "GERCEK", true, true, [Job("SIPARIS_CEK"), Job("SORU_CEK")]).Health.Should().Be(ChannelHealthStates.Working);
    }

    [Fact]
    public void Job_error_info_only_when_failing()
    {
        Job("SIPARIS_CEK", 0, "eski hata").ErrorInfo.Should().BeNull();
        Job("SIPARIS_CEK", 1, "Beymen'e ulaşılamadı.").ErrorInfo!.Kind.Should().Be(ChannelErrorKinds.Unreachable);
    }
}
