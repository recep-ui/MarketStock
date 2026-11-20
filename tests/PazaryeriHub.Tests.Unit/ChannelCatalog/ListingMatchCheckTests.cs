using PazaryeriHub.Application.ChannelCatalog;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Panel;

namespace PazaryeriHub.Tests.Unit.ChannelCatalog;

/// <summary>
/// ADR-035: kanal bazlı stok listesinde yanlış eşleştirme uyarıları. Uyarı yalnızca bilgi verir; eşleştirmeyi değiştirmez.
/// </summary>
public sealed class ListingMatchCheckTests
{
    private static readonly LedgerSide Ledger = new("BAMBU-5090-BYZ", "8690000000017", "Bambu Havlu 50x90", "Softcotton", "Beyaz", "50x90");

    private static ChannelSide Channel(
        string? barcode = "8690000000017", string? stockCode = "BAMBU-5090-BYZ", string? name = "Softcotton Bambu Havlu 50x90 Beyaz",
        string? color = "Beyaz", string? size = "50x90") =>
        new(barcode, "TY-1", stockCode, name, "Softcotton", color, size, null);

    private static IEnumerable<string> Codes(ListingMatchResult r) => r.Warnings.Select(w => w.Code);

    [Fact]
    public void Her_sey_uyusuyorsa_uyari_yok_ve_dayanak_barkod()
    {
        var r = ListingMatchCheck.Evaluate(Channel(), Ledger);

        r.Basis.Should().Be(MatchBases.Barcode);
        r.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Barkod_farkliysa_ve_stok_kodu_dogrulamiyorsa_uyarilir()
    {
        var r = ListingMatchCheck.Evaluate(Channel(barcode: "8690000000999", stockCode: null), Ledger);

        Codes(r).Should().Contain(MatchWarnings.BarcodeDiffers);
        r.Basis.Should().Be(MatchBases.Other);
    }

    [Fact]
    public void Barkod_farki_stok_kodu_dogruluyorsa_uyari_degil()
    {
        var r = ListingMatchCheck.Evaluate(Channel(barcode: "8690000000999"), Ledger);

        Codes(r).Should().NotContain(MatchWarnings.BarcodeDiffers);
        r.Basis.Should().Be(MatchBases.StockCode, "stok kodu aynı");
    }

    [Fact]
    public void Stok_kodu_farki_barkod_dogruluyorsa_uyari_degil()
    {
        var r = ListingMatchCheck.Evaluate(Channel(stockCode: "TY-STOK-77"), Ledger);

        Codes(r).Should().NotContain(MatchWarnings.StockCodeDiffers);
        r.Basis.Should().Be(MatchBases.Barcode);
    }

    [Fact]
    public void Stok_kodu_farki_barkod_yoksa_uyarilir_ve_dayanak_diger()
    {
        var r = ListingMatchCheck.Evaluate(Channel(barcode: null, stockCode: "BASKA-KOD"), Ledger);

        Codes(r).Should().Contain(MatchWarnings.StockCodeDiffers);
        r.Basis.Should().Be(MatchBases.Other);
    }

    [Fact]
    public void Stok_kodu_buyuk_kucuk_harf_ve_bosluk_farki_ayni_sayilir()
    {
        var r = ListingMatchCheck.Evaluate(Channel(barcode: null, stockCode: " bambu-5090-byz "), Ledger);

        Codes(r).Should().NotContain(MatchWarnings.StockCodeDiffers);
        r.Basis.Should().Be(MatchBases.StockCode);
    }

    [Fact]
    public void Renk_ve_beden_farki_ayri_ayri_uyarilir_buyuk_kucuk_harf_onemsiz()
    {
        Codes(ListingMatchCheck.Evaluate(Channel(color: "BEYAZ", size: "50X90"), Ledger)).Should().BeEmpty();

        var r = ListingMatchCheck.Evaluate(Channel(color: "Lacivert", size: "70x140"), Ledger);
        Codes(r).Should().Contain([MatchWarnings.ColorDiffers, MatchWarnings.SizeDiffers]);
        r.Warnings.Single(w => w.Code == MatchWarnings.ColorDiffers).Message.Should().Contain("Lacivert").And.Contain("Beyaz");
    }

    [Fact]
    public void Ad_benzemiyorsa_uyarilir()
    {
        var r = ListingMatchCheck.Evaluate(Channel(name: "Kadın Saten Gecelik Kırmızı"), Ledger);

        Codes(r).Should().Contain(MatchWarnings.NameDiffers);
    }

    [Fact]
    public void Kanal_bilgisi_bilinmiyorsa_o_alan_icin_uyari_uretilmez()
    {
        var r = ListingMatchCheck.Evaluate(new ChannelSide(null, "HB-1", null, null, null, null, null, null), Ledger);

        r.Warnings.Should().BeEmpty();
        r.Basis.Should().Be(MatchBases.Other);
    }

    [Theory]
    [InlineData("Bambu Havlu 50x90", "Softcotton Bambu Havlu 50x90 Beyaz", true)]
    [InlineData("BAMBU HAVLU", "bambu havlu", true)]
    [InlineData("Bambu Havlu 50x90", "Kadın Saten Gecelik Kırmızı", false)]
    public void Ad_benzerligi_trigram_ile_olculur(string a, string b, bool similar) =>
        (ChannelCatalogMatcher.NameSimilarity(a, b) >= ListingMatchCheck.NameSimilarityThreshold).Should().Be(similar);

    [Fact]
    public void Ad_benzerligi_bos_adda_sifir() =>
        ChannelCatalogMatcher.NameSimilarity("", "Bambu").Should().Be(0m);

    [Fact]
    public void Kanal_verisinden_ad_marka_stok_kodu_renk_beden_okunur()
    {
        var d = ListingChannelData.Parse("""{"urunAdi":"Bambu Havlu","marka":"Softcotton","stokKodu":"B-1","renk":"Beyaz","beden":"50x90"}""");

        (d.Name, d.Brand, d.StockCode, d.Color, d.Size).Should().Be(("Bambu Havlu", "Softcotton", "B-1", "Beyaz", "50x90"));
    }

    [Fact]
    public void Kanal_verisinden_yalnizca_https_gorsel_okunur()
    {
        ListingChannelData.Parse("""{"gorsel":"https://img.example.com/a.jpg"}""").ImageUrl.Should().Be("https://img.example.com/a.jpg");
        ListingChannelData.Parse("""{"gorsel":"http://img.example.com/a.jpg"}""").ImageUrl.Should().BeNull();
        ListingChannelData.Parse("""{"gorsel":"/mediacenter/a.jpg"}""").ImageUrl.Should().BeNull();
    }
}
