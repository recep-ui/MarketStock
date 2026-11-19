using PazaryeriHub.Application.Panel;

namespace PazaryeriHub.Tests.Unit.ChannelCatalog;

/// <summary>
/// ADR-036: "doğru eşleşme" onayı, onaylandığı andaki eşleşme bilgisinin imzasına bağlıdır. Bilgi değişirse onay geçersizleşir;
/// yalnızca biçim farkı (büyük/küçük harf, boşluk) onayı bozmaz.
/// </summary>
public sealed class ListingMatchSignatureTests
{
    private static readonly LedgerSide Ledger = new("BAMBU-1", "8690000000017", "Bambu Havlu", "Softcotton", "Beyaz", "50x90");
    private static readonly ChannelSide Channel = new("8690000000999", "TY-1", null, "Bambu Havlu Lacivert", "Softcotton", "Lacivert", "50x90", null);
    private static readonly MatchWarning[] Warnings =
    [
        new(MatchWarnings.BarcodeDiffers, "Barkod farklı"),
        new(MatchWarnings.ColorDiffers, "Renk farklı")
    ];

    private static string Sig(int skuId = 7, ChannelSide? channel = null, LedgerSide? ledger = null, IReadOnlyList<MatchWarning>? warnings = null) =>
        ListingMatchCheck.Signature(skuId, channel ?? Channel, ledger ?? Ledger, warnings ?? Warnings);

    [Fact]
    public void Ayni_bilgi_ayni_imza_64_hex()
    {
        Sig().Should().Be(Sig());
        Sig().Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Uyari_sirasi_ve_mesaj_metni_imzayi_etkilemez() =>
        Sig(warnings: [new(MatchWarnings.ColorDiffers, "başka metin"), new(MatchWarnings.BarcodeDiffers, "x")]).Should().Be(Sig());

    [Fact]
    public void Baska_SKUya_baglanirsa_imza_degisir() =>
        Sig(skuId: 8).Should().NotBe(Sig());

    [Fact]
    public void Kanal_adi_renk_barkod_veya_SKU_bilgisi_degisirse_imza_degisir()
    {
        Sig(channel: Channel with { Name = "Kadın Gecelik" }).Should().NotBe(Sig());
        Sig(channel: Channel with { Color = "Kırmızı" }).Should().NotBe(Sig());
        Sig(channel: Channel with { Barcode = "8690000000888" }).Should().NotBe(Sig());
        Sig(ledger: Ledger with { Barcode = "8690000000555" }).Should().NotBe(Sig());
        Sig(ledger: Ledger with { ProductName = "Pamuk Bornoz" }).Should().NotBe(Sig());
    }

    [Fact]
    public void Uyari_kumesi_degisirse_imza_degisir() =>
        Sig(warnings: [Warnings[0]]).Should().NotBe(Sig());

    [Fact]
    public void Yalnizca_bicim_farki_imzayi_bozmaz() =>
        Sig(channel: Channel with { Name = "  bambu   havlu LACİVERT ", Color = "LACİVERT", Barcode = " 8690000000999 " }).Should().Be(Sig());
}
