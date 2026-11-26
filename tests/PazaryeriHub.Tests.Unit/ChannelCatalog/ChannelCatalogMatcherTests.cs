using PazaryeriHub.Application.ChannelCatalog;

namespace PazaryeriHub.Tests.Unit.ChannelCatalog;

/// <summary>Kanal kataloğu eşleştiricisi (ADR-020): adım sırası, belirsizlik ve öneri kuralları.</summary>
public sealed class ChannelCatalogMatcherTests
{
    private static ChannelCatalogItem Item(int row, string? barcode = null, string? stock = null, string? model = null, string? color = null,
        string? size = null, string? name = null, string? brand = null, string? code = null) =>
        new(row, code, barcode, stock, model, color, size, name, brand, null, null, false);

    private static CatalogSkuRef Sku(int id, string code, string? barcode = null, string? model = null, string? color = null, string? size = null,
        string name = "Ürün", string? brand = null) =>
        new(id, code, barcode, model, color, size, name, brand);

    [Fact]
    public void Barkod_ilk_adimdir_ve_stok_kodundan_once_gelir()
    {
        var skus = new[] { Sku(1, "A", barcode: "869001"), Sku(2, "STK-1") };
        var result = ChannelCatalogMatcher.Match([Item(2, barcode: "869001", stock: "STK-1")], skus, []);

        result.Single().Sku!.SkuId.Should().Be(1);
        result.Single().Step.Should().Be(CatalogMatchStep.Barcode);
    }

    [Fact]
    public void Mevcut_eslestirme_barkoddan_once_gelir()
    {
        var skus = new[] { Sku(1, "A", barcode: "869001"), Sku(2, "B") };
        var result = ChannelCatalogMatcher.Match([Item(2, barcode: "869001")], skus, [new ExistingListingRef(2, null, "869001")]);

        result.Single().Sku!.SkuId.Should().Be(2);
        result.Single().Step.Should().Be(CatalogMatchStep.ExistingListing);
    }

    [Fact]
    public void Stok_kodu_dosyada_tekilse_eslesir_tekrar_ediyorsa_belirsizdir()
    {
        var skus = new[] { Sku(1, "STK-1"), Sku(2, "STK-2") };
        var result = ChannelCatalogMatcher.Match(
            [Item(2, barcode: "B1", stock: "STK-1"), Item(3, barcode: "B2", stock: "STK-2"), Item(4, barcode: "B3", stock: "STK-2")], skus, []);

        result[0].Step.Should().Be(CatalogMatchStep.StockCode);
        result[1].IsMatched.Should().BeFalse();
        result[1].Reason.Should().Be(UnmatchedReasons.Ambiguous);
        result[1].Detail.Should().Contain("2 satırda");
    }

    [Theory]
    // Hepsiburada satıcı stok kodunu büyük harfe çevirip Türkçe İ kullanıyor (canlı veri, 2026-11-26; ADR-055).
    // Kodlar ikili karşılaştırılır (otomatik bağlama yok); harf farkıyla tek SKU tutuyorsa yalnızca ÖNERİ olur.
    [InlineData("MİCROBORNOZ21", "Microbornoz21")]
    [InlineData("MARINE85150LACIVERT", "Marine85150Lacivert")]
    [InlineData("STRIPEBORNOZ0", "Strıpebornoz0")]
    public void Stok_kodu_harf_farkiyla_tutuyorsa_baglanmaz_oneri_olur(string channelCode, string ourCode)
    {
        var skus = new[] { Sku(1, ourCode), Sku(2, "BASKA-KOD") };
        var r = ChannelCatalogMatcher.Match([Item(2, stock: channelCode, code: "HBV1")], skus, []).Single();

        r.IsMatched.Should().BeFalse();
        r.Suggestion!.SkuId.Should().Be(1);
        r.SuggestionScore.Should().Be(1m);
        r.Detail.Should().Contain("harf farkı");
    }

    [Fact]
    public void Stok_kodu_harf_farkiyla_SKU_barkoduna_uyuyorsa_da_oneri_olur()
    {
        // Trendyol'dan gelen SKU'larda barkod alanı çoğu zaman metin koddur (ör. "AxelBornoz006")
        var skus = new[] { Sku(1, "AXELBORNOZ-L", barcode: "AxelBornoz006") };
        var r = ChannelCatalogMatcher.Match([Item(2, stock: "AXELBORNOZ006", code: "HBV1")], skus, []).Single();

        r.IsMatched.Should().BeFalse();
        r.Suggestion!.SkuId.Should().Be(1);
    }

    [Fact]
    public void Harf_farkiyla_birden_fazla_SKU_tutuyorsa_oneri_yok()
    {
        var skus = new[] { Sku(1, "Abc1"), Sku(2, "ABC1-X", barcode: "aBc1") };
        var r = ChannelCatalogMatcher.Match([Item(2, stock: "ABC1", code: "HBV1")], skus, []).Single();

        r.IsMatched.Should().BeFalse();
        r.Suggestion.Should().BeNull();
    }

    [Fact]
    public void Kod_onerisi_ad_benzerligi_onerisinden_once_gelir()
    {
        var skus = new[] { Sku(1, "Abc1", name: "Başka Ürün"), Sku(2, "X-2", name: "Pamuklu Bornoz") };
        var r = ChannelCatalogMatcher.Match([Item(2, stock: "ABC1", name: "Pamuklu Bornoz", code: "HBV1")], skus, []).Single();

        r.Suggestion!.SkuId.Should().Be(1);
    }

    [Fact]
    public void Model_renk_beden_tek_adayda_eslesir_harf_ve_bosluk_duyarsiz()
    {
        var skus = new[] { Sku(1, "X", model: "MDL-1", color: "Kırmızı", size: "M"), Sku(2, "Y", model: "MDL-1", color: "Kırmızı", size: "L") };
        var result = ChannelCatalogMatcher.Match([Item(2, barcode: "B1", model: "MDL-1", color: "  KIRMIZI ", size: "m")], skus, []);

        result.Single().Sku!.SkuId.Should().Be(1);
        result.Single().Step.Should().Be(CatalogMatchStep.ModelColorSize);
    }

    [Fact]
    public void Model_renk_bedende_birden_fazla_aday_varsa_baglanmaz()
    {
        var skus = new[] { Sku(1, "X", model: "MDL-1", color: "Siyah", size: "M"), Sku(2, "Y", model: "MDL-1", color: "Siyah", size: "M") };
        var result = ChannelCatalogMatcher.Match([Item(2, barcode: "B1", model: "MDL-1", color: "Siyah", size: "M")], skus, []);

        result.Single().IsMatched.Should().BeFalse();
        result.Single().Reason.Should().Be(UnmatchedReasons.Ambiguous);
    }

    [Fact]
    public void Ayni_skuya_dusen_birden_fazla_satir_ayri_ilan_olarak_baglanir()
    {
        var skus = new[] { Sku(1, "STK-1", barcode: "B1") };
        var result = ChannelCatalogMatcher.Match([Item(2, barcode: "B1"), Item(3, barcode: "B9", stock: "STK-1")], skus, []);

        result.Should().AllSatisfy(r => r.Sku!.SkuId.Should().Be(1), "ADR-023: aynı SKU'nun birden fazla ilanı olabilir");
    }

    [Fact]
    public void Coklanan_ilanlar_ayni_model_renk_bedende_tek_adaya_baglanir()
    {
        var skus = new[] { Sku(1, "X", model: "MDL-1", color: "Gri", size: "XL") };
        var result = ChannelCatalogMatcher.Match(
            [Item(2, barcode: "B1", model: "MDL-1", color: "Gri", size: "XL"), Item(3, barcode: "B2", model: "MDL-1", color: "gri", size: "XL")], skus, []);

        result.Should().AllSatisfy(r =>
        {
            r.Sku!.SkuId.Should().Be(1);
            r.Step.Should().Be(CatalogMatchStep.ModelColorSize);
        });
    }

    [Fact]
    public void Barkod_ve_kanal_kodu_farkli_skulara_bagliysa_cakismadir()
    {
        var skus = new[] { Sku(1, "A"), Sku(2, "B") };
        var result = ChannelCatalogMatcher.Match([Item(2, barcode: "B1", code: "HB1")], skus,
            [new ExistingListingRef(1, null, "B1"), new ExistingListingRef(2, "HB1", null)]);

        result.Single().Reason.Should().Be(UnmatchedReasons.Conflict);
    }

    [Fact]
    public void Turkce_i_ayrimi_kodlarda_korunur()
    {
        var skus = new[] { Sku(1, "KIRMIZI-1") };
        var result = ChannelCatalogMatcher.Match([Item(2, barcode: "B1", stock: "KİRMİZİ-1")], skus, []);

        result.Single().IsMatched.Should().BeFalse("SKU ve stok kodu ikili (BIN2) karşılaştırılır");
    }

    [Fact]
    public void Ad_benzerligi_yalnizca_oneri_uretir_asla_baglamaz()
    {
        var skus = new[] { Sku(1, "A", name: "Basic Pamuklu Tişört", brand: "Softcotton", color: "Beyaz", size: "M") };
        var result = ChannelCatalogMatcher.Match([Item(2, barcode: "B1", name: "Basic Pamuklu Tişört Beyaz", brand: "SOFTCOTTON", color: "beyaz", size: "M")], skus, []);

        var r = result.Single();
        r.IsMatched.Should().BeFalse();
        r.Reason.Should().Be(UnmatchedReasons.NoMatch);
        r.Suggestion!.SkuId.Should().Be(1);
        r.SuggestionScore.Should().BeGreaterThanOrEqualTo(ChannelCatalogMatcher.SuggestionThreshold);
    }

    [Fact]
    public void Oneri_farkli_marka_beden_veya_dusuk_benzerlikte_uretilmez()
    {
        var skus = new[] { Sku(1, "A", name: "Basic Pamuklu Tişört", brand: "Softcotton", size: "M") };

        ChannelCatalogMatcher.Match([Item(2, barcode: "B1", name: "Basic Pamuklu Tişört", brand: "Başka", size: "M")], skus, [])
            .Single().Suggestion.Should().BeNull();
        ChannelCatalogMatcher.Match([Item(2, barcode: "B1", name: "Basic Pamuklu Tişört", brand: "Softcotton", size: "L")], skus, [])
            .Single().Suggestion.Should().BeNull();
        ChannelCatalogMatcher.Match([Item(2, barcode: "B1", name: "Deri Ceket", brand: "Softcotton", size: "M")], skus, [])
            .Single().Suggestion.Should().BeNull();
    }

    [Theory]
    [InlineData("  Açık  Mavi ", "AÇIK MAVİ")]
    [InlineData("S/M", "S M")]
    [InlineData("istanbul", "İSTANBUL")]
    [InlineData(null, "")]
    public void Normalize_turkce_buyuk_harf_ve_bosluk(string? input, string expected) =>
        CatalogText.Normalize(input).Should().Be(expected);
}
