using PazaryeriHub.Application.Channels;

namespace PazaryeriHub.Tests.Unit.ChannelCatalog;

/// <summary>
/// ADR-043: kanalın bildirdiği ürün durumu (ham metin) → satış durumu sınıfı. Yalnızca gerçek kanal yanıtlarında görülen metinler
/// tanınır; tanınmayan metin "bilinmiyor" sayılır (tahmin yapılmaz). İlan durumunu (AKTIF/PASIF) değiştirmez; yalnızca gösterim/filtre.
/// </summary>
public sealed class ChannelSaleStateTests
{
    [Theory]
    // Trendyol (adaptörün ürettiği metinler)
    [InlineData("Satışta", ChannelSaleState.OnSale)]
    [InlineData("Satışta değil", ChannelSaleState.OffSale)]
    [InlineData("Kara listede", ChannelSaleState.OffSale)]
    [InlineData("Kilitli", ChannelSaleState.OffSale)]
    [InlineData("Arşivlendi", ChannelSaleState.OffSale)]
    [InlineData("Ürün arşivlendi", ChannelSaleState.OffSale)]
    // Beymen (status.localizedDescription, deneme modu katalog okuması 2026-11-21)
    [InlineData("Satışta Değil", ChannelSaleState.OffSale)]
    [InlineData("Stoğu Tükendi", ChannelSaleState.OutOfStock)]
    [InlineData("Beymen'e Seçilmedi", ChannelSaleState.OffSale)]
    [InlineData("Revize Bekleniyor", ChannelSaleState.OffSale)]
    // Büyük/küçük harf, Türkçe karakter ve boşluk farkı önemsiz
    [InlineData("  SATIŞTA ", ChannelSaleState.OnSale)]
    [InlineData("stogu tukendi", ChannelSaleState.OutOfStock)]
    public void Bilinen_kanal_durumlari_siniflanir(string status, string expected) =>
        ChannelSaleState.Classify(status).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Onay Bekliyor")]
    [InlineData("Aktif")]
    public void Bos_veya_taninmayan_durum_bilinmiyor_sayilir(string? status) =>
        ChannelSaleState.Classify(status).Should().Be(ChannelSaleState.Unknown);

    [Fact]
    public void Filtre_degeri_yalnizca_tanimli_siniflari_kabul_eder()
    {
        ChannelSaleState.All.Should().BeEquivalentTo([ChannelSaleState.OnSale, ChannelSaleState.OutOfStock, ChannelSaleState.OffSale, ChannelSaleState.Unknown]);
        ChannelSaleState.IsValid("SATISTA").Should().BeTrue();
        ChannelSaleState.IsValid("satista").Should().BeFalse();
        ChannelSaleState.IsValid("HEPSI").Should().BeFalse();
    }
}
