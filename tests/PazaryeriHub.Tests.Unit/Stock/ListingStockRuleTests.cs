using PazaryeriHub.Domain.Stock;

namespace PazaryeriHub.Tests.Unit.Stock;

/// <summary>
/// ADR-042: ilan başına stok kuralı (platform bazlı farklı stok). Kural yalnızca AZALTIR; kanala giden değer asla defterdeki
/// satılabilir stoğu ve kanal payı/üst limiti ile hesaplanan değeri aşmaz.
/// </summary>
public sealed class ListingStockRuleTests
{
    [Fact]
    public void Kural_yoksa_allocator_sonucu_aynen_gider() =>
        StockAllocator.Allocate(37, 100m, null, 0, rule: null).Should().Be(37);

    [Theory]
    [InlineData(37, 10, 10)] // üst limit altında: limit
    [InlineData(4, 10, 4)]   // stok limitten az: stok
    [InlineData(37, 0, 0)]   // limit 0: kanala stok gitmez
    public void Ust_limit_hesaplananin_altina_ceker(long available, int limit, int expected) =>
        StockAllocator.Allocate(available, 100m, null, 0, new ListingStockRule(ListingStockRuleType.UpperLimit, limit)).Should().Be(expected);

    [Theory]
    [InlineData(37, 5, 5)]   // sabit 5, stok yeterli
    [InlineData(3, 5, 3)]    // sabit 5 ama defterde 3 var: 3 (defteri aşmaz)
    [InlineData(-2, 5, 0)]   // satılabilir negatif: 0
    public void Sabit_deger_defteri_asmaz(long available, int value, int expected) =>
        StockAllocator.Allocate(available, 100m, null, 0, new ListingStockRule(ListingStockRuleType.Fixed, value)).Should().Be(expected);

    [Fact]
    public void Sabit_deger_kanal_payinin_yerine_gecer_ama_kanal_ust_limitini_asmaz()
    {
        // %50 pay → 10 olurdu; sabit 15 ve defterde 20 var: 15.
        StockAllocator.Allocate(20, 50m, null, 0, new ListingStockRule(ListingStockRuleType.Fixed, 15)).Should().Be(15);
        // Kanal üst limiti 6 → sabit 8 istense de 6.
        StockAllocator.Allocate(20, 100m, 6, 0, new ListingStockRule(ListingStockRuleType.Fixed, 8)).Should().Be(6);
    }

    [Fact]
    public void Ust_limit_kanal_payindan_sonra_uygulanir() =>
        // %50 pay → 10; üst limit 15 → 10.
        StockAllocator.Allocate(20, 50m, null, 0, new ListingStockRule(ListingStockRuleType.UpperLimit, 15)).Should().Be(10);

    [Fact]
    public void Dusuk_stok_esigi_kuraldan_once_uygulanir() =>
        StockAllocator.Allocate(3, 100m, null, 3, new ListingStockRule(ListingStockRuleType.Fixed, 2)).Should().Be(0);

    [Fact]
    public void Negatif_kural_degeri_reddedilir()
    {
        var act = () => new ListingStockRule(ListingStockRuleType.UpperLimit, -1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("UST_LIMIT", ListingStockRuleType.UpperLimit)]
    [InlineData("SABIT", ListingStockRuleType.Fixed)]
    public void Veritabani_kodu_cevrilir(string code, ListingStockRuleType type)
    {
        ListingStockRule.ParseType(code).Should().Be(type);
        ListingStockRule.ToCode(type).Should().Be(code);
    }

    [Fact]
    public void Bilinmeyen_kod_null_doner() => ListingStockRule.ParseType("YUZDE").Should().BeNull();
}
