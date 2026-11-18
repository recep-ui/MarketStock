using Microsoft.Extensions.Configuration;
using PazaryeriHub.Application.Orders;

namespace PazaryeriHub.Tests.Unit.Orders;

/// <summary>Kanal başına SiparisCek aralığı (ADR-030).</summary>
public sealed class OrderFetchScheduleTests
{
    private static OrderOptions Bind(params (string Key, string? Value)[] values)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>($"{OrderOptions.SectionName}:{v.Key}", v.Value)))
            .Build();
        var options = new OrderOptions();
        config.GetSection(OrderOptions.SectionName).Bind(options);
        return options;
    }

    [Fact]
    public void Varsayilan_Trendyol_ve_Hepsiburada_1_dk_Beymen_2_dk()
    {
        var o = new OrderOptions();
        o.FetchCronFor(ChannelCodes.Trendyol).Should().Be("* * * * *");
        o.FetchCronFor(ChannelCodes.Hepsiburada).Should().Be("* * * * *");
        o.FetchCronFor(ChannelCodes.Beymen).Should().Be("*/2 * * * *", "Beymen hız limiti dokümanda yok");
        o.FetchCronFor(ChannelCodes.Softcotton).Should().Be("*/2 * * * *");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]  // deneme modu Windows'ta tek boşluk verir
    public void Genel_ayar_bossa_hicbir_kanalda_job_yok(string fetchCron)
    {
        var o = Bind(("FetchCron", fetchCron));
        foreach (var code in ChannelCodes.All)
        {
            o.FetchCronFor(code).Should().BeNull();
        }
    }

    [Fact]
    public void Kanal_ayari_yapilandirmadan_degistirilebilir_bos_kanal_ayari_genel_ayara_duser()
    {
        var o = Bind(("FetchCron", "*/3 * * * *"), ("ChannelFetchCron:TRENDYOL", "*/2 * * * *"), ("ChannelFetchCron:HEPSIBURADA", ""));
        o.FetchCronFor(ChannelCodes.Trendyol).Should().Be("*/2 * * * *");
        o.FetchCronFor(ChannelCodes.Hepsiburada).Should().Be("*/3 * * * *");
        o.FetchCronFor(ChannelCodes.Beymen).Should().Be("*/3 * * * *");
    }
}
