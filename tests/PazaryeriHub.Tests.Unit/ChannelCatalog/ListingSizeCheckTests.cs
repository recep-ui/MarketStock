using PazaryeriHub.Application.Panel;

namespace PazaryeriHub.Tests.Unit.ChannelCatalog;

/// <summary>
/// ADR-053: şüpheli eşleşmelerde ebat karşılaştırması. Ebat ölçüleri sayı olarak karşılaştırılır ("70x140" = "70 × 140 cm");
/// beden/ebat alanı boşsa varyant metninden, o da yoksa addan okunur. Bir tarafta ebat varken diğerinde hiç yoksa "Ebat bilgisi eksik".
/// </summary>
public sealed class ListingSizeCheckTests
{
    private static readonly LedgerSide Ledger = new("HAV-70140-ANT", "8690000000017", "Pamuk Havlu", "Softcotton", "Antrasit", "70x140");

    private static ChannelSide Channel(string? size = "70x140", string? name = "Softcotton Pamuk Havlu Antrasit", string? variant = null) =>
        new("8690000000017", "TY-1", "HAV-70140-ANT", name, "Softcotton", "Antrasit", size, variant);

    private static IEnumerable<string> Codes(ChannelSide channel, LedgerSide? ledger = null) =>
        ListingMatchCheck.Evaluate(channel, ledger ?? Ledger).Warnings.Select(w => w.Code);

    [Theory]
    [InlineData("70x140", "70x140")]
    [InlineData("70 X 140 cm", "70x140")]
    [InlineData("70×140", "70x140")]
    [InlineData("140*70", "70x140")]
    [InlineData("Pamuk Havlu 50x90 Antrasit", "50x90")]
    [InlineData("Nevresim 200x220x30", "30x200x220")]
    [InlineData("Tablo 1,5x2", "1.5x2")]
    public void Ebat_metinden_sayi_olarak_okunur(string text, string expected) =>
        SizeDimensions.Parse(text).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("M")]
    [InlineData("Tek Ebat")]
    [InlineData("Set: 50x90 + 70x140")]
    public void Ebat_yoksa_veya_birden_fazla_farkli_ebat_varsa_okunmaz(string? text) =>
        SizeDimensions.Parse(text).Should().BeNull();

    [Fact]
    public void Ebat_farkliysa_ebat_farkli_uyarisi_iki_degeri_gosterir()
    {
        var r = ListingMatchCheck.Evaluate(Channel(size: "50x90"), Ledger);

        r.Warnings.Select(w => w.Code).Should().Equal(MatchWarnings.DimensionDiffers);
        r.Warnings.Single().Message.Should().StartWith("Ebat farklı").And.Contain("50×90").And.Contain("70×140");
    }

    [Fact]
    public void Ayni_ebat_farkli_yazimla_uyari_degil()
    {
        Codes(Channel(size: "70 x 140 cm")).Should().BeEmpty();
        Codes(Channel(size: "140X70")).Should().BeEmpty();
    }

    [Fact]
    public void Alan_bossa_ebat_varyanttan_sonra_addan_okunur()
    {
        Codes(Channel(size: null, variant: "Renk: Antrasit; Ebat: 50x90")).Should().Equal(MatchWarnings.DimensionDiffers);
        Codes(Channel(size: null, name: "Pamuk Havlu 50x90 Antrasit")).Should().Equal(MatchWarnings.DimensionDiffers);
        Codes(Channel(size: null, name: "Pamuk Havlu 70x140 Antrasit")).Should().BeEmpty();
    }

    [Fact]
    public void SKUda_beden_bossa_ebat_urun_adindan_okunur()
    {
        var ledger = Ledger with { Size = null, ProductName = "Pamuk Havlu 70x140" };

        Codes(Channel(size: "50x90"), ledger).Should().Equal(MatchWarnings.DimensionDiffers);
        Codes(Channel(size: "70x140"), ledger).Should().BeEmpty();
    }

    [Fact]
    public void Kanalda_ebat_hic_yoksa_ebat_bilgisi_eksik()
    {
        var r = ListingMatchCheck.Evaluate(Channel(size: null), Ledger);

        r.Warnings.Select(w => w.Code).Should().Equal(MatchWarnings.DimensionMissing);
        r.Warnings.Single().Message.Should().StartWith("Ebat bilgisi eksik").And.Contain("kanalda yok").And.Contain("70×140");
    }

    [Fact]
    public void SKUda_ebat_hic_yoksa_ebat_bilgisi_eksik()
    {
        var r = ListingMatchCheck.Evaluate(Channel(size: "50x90"), Ledger with { Size = null });

        r.Warnings.Select(w => w.Code).Should().Equal(MatchWarnings.DimensionMissing);
        r.Warnings.Single().Message.Should().Contain("SKU'da yok").And.Contain("50×90");
    }

    [Fact]
    public void Iki_tarafta_da_ebat_yoksa_uyari_yok() =>
        Codes(Channel(size: null), Ledger with { Size = null }).Should().BeEmpty();

    [Fact]
    public void Kanalin_urun_bilgisi_hic_okunmamissa_eksik_denmez()
    {
        // Elle girilmiş, katalogdan hiç okunmamış ilan: kanal tarafında ad/varyant yok → ebatı bilmemek "eksik" değil, bilinmiyor
        var unknown = new ChannelSide("8690000000017", "TY-1", null, null, null, null, null, null);

        ListingMatchCheck.Evaluate(unknown, Ledger).Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Ebat_olmayan_bedenler_metin_olarak_karsilastirilir()
    {
        var ledger = Ledger with { Size = "M" };

        Codes(Channel(size: "m"), ledger).Should().BeEmpty();
        Codes(Channel(size: "L"), ledger).Should().Equal(MatchWarnings.SizeDiffers);
    }

    [Fact]
    public void Varyant_degisirse_onay_imzasi_degisir()
    {
        var warnings = new[] { new MatchWarning(MatchWarnings.DimensionDiffers, "Ebat farklı") };
        var before = ListingMatchCheck.Signature(7, Channel(variant: "Ebat: 70x140"), Ledger, warnings);

        ListingMatchCheck.Signature(7, Channel(variant: "Ebat: 50x90"), Ledger, warnings).Should().NotBe(before);
        ListingMatchCheck.Signature(7, Channel(variant: " ebat:  70X140 "), Ledger, warnings).Should().Be(before, "yalnızca biçim farkı");
    }
}
