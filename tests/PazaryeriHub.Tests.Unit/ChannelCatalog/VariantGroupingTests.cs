using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.ChannelCatalog;
using PazaryeriHub.Application.Stock;
using PazaryeriHub.Infrastructure.ChannelCatalog;

namespace PazaryeriHub.Tests.Unit.ChannelCatalog;

/// <summary>
/// ADR-031: farklı renk/ebat ürünleri tek SKU'da (tek stok havuzunda) birleşmez. Birleştirme yalnızca varyant kesin biliniyorsa.
/// </summary>
public sealed class VariantGroupingTests
{
    private static ChannelCatalogItem Item(string barcode, string? stock, string? model, string? color = null, string? size = null, string? variantKey = null) =>
        new(1, null, barcode, stock, model, color, size, "Ürün", "Marka", null, null, false, variantKey);

    [Fact]
    public void Renk_beden_bilinmiyorsa_ayni_modelin_barkodlari_ayri_SKU_olur()
    {
        var a = ChannelCatalogService.GroupKey(Item("Bambu85150000", "Bambu85150", "Bambu85150"));
        var b = ChannelCatalogService.GroupKey(Item("Bambu85150001", "Bambu85150", "Bambu85150"));
        a.Should().NotBe(b);
    }

    [Fact]
    public void Farkli_stok_kodu_hicbir_durumda_birlesmez()
    {
        // Bambu50100 (50x100) ile Bambu85150 aynı model kodunda bile olsa
        ChannelCatalogService.GroupKey(Item("Bambu50100004", "Bambu50100", "Bambu85150", "Beyaz"))
            .Should().NotBe(ChannelCatalogService.GroupKey(Item("Bambu85150000", "Bambu85150", "Bambu85150", "Beyaz")));
    }

    [Fact]
    public void Kanal_varyant_anahtari_farkliysa_ayri_ayniysa_ayni_SKU()
    {
        ChannelCatalogService.GroupKey(Item("C1", "Chain75150", "Chain75150", variantKey: "c501"))
            .Should().NotBe(ChannelCatalogService.GroupKey(Item("C2", "Chain75150", "Chain75150", variantKey: "c502")), "farklı renk içerikleri");
        ChannelCatalogService.GroupKey(Item("C1", "Chain75150", "Chain75150", variantKey: "c501|Ebat=75x150"))
            .Should().Be(ChannelCatalogService.GroupKey(Item("C1-2", "Chain75150", "Chain75150", variantKey: "c501|Ebat=75x150")), "aynı varyantın çoklanan ilanı");
    }

    [Fact]
    public void Renk_ve_beden_bilinen_ayni_varyant_birlesir()
    {
        ChannelCatalogService.GroupKey(Item("X1", "STK", "MDL", "Beyaz", "M"))
            .Should().Be(ChannelCatalogService.GroupKey(Item("X2", "STK", "MDL", "beyaz", "m")));
    }

    [Fact]
    public void Eslestirici_renk_beden_bilinmeyen_satiri_yalnizca_model_koduyla_baglamaz()
    {
        var skus = new[] { new CatalogSkuRef(1, "CHAIN75150", "Chain75150001", "Chain75150", null, null, "Chain Havlu", "Marka") };
        var result = ChannelCatalogMatcher.Match([Item("Chain75150002", "Chain75150", "Chain75150")], skus, []);
        result.Single().IsMatched.Should().BeFalse("yeni bir renk mevcut SKU'nun stok havuzuna bağlanmamalı");
    }

    [Fact]
    public void Bazi_ilanlarin_kanal_degeri_yoksa_ayri_uyari_eski_cekim_degil()
    {
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        var e = ChannelIntakeRules.Evaluate([100, null], [100, null], 0, 0, 100m, null, 0, now.AddMinutes(-5), null, false, now);
        e.Flags.Should().Contain(IntakeFlags.SomeListingsMissing).And.NotContain(IntakeFlags.StaleSnapshot);
        e.DefaultSelected.Should().BeFalse();
    }

    [Fact]
    public void Kanal_verisi_link_fiyat_ve_varyant_okunur_https_disi_link_gosterilmez()
    {
        var d = ListingChannelData.Parse("""{"urunLinki":"https://www.trendyol.com/a-p-1","kanalSatisFiyati":349.9,"kanalListeFiyati":499.9,"varyant":"Ebat: 75x150"}""");
        d.Should().Be(new ListingChannelData("https://www.trendyol.com/a-p-1", 349.9m, 499.9m, "Ebat: 75x150"));
        ListingChannelData.Parse("""{"urunLinki":"javascript:alert(1)"}""").ProductUrl.Should().BeNull();
        ListingChannelData.Parse("bozuk").Should().Be(ListingChannelData.Empty);
    }
}
