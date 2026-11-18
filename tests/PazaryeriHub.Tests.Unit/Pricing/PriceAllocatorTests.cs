using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Excel;
using PazaryeriHub.Application.Pricing;
using PazaryeriHub.Domain.Pricing;

namespace PazaryeriHub.Tests.Unit.Pricing;

/// <summary>M4 kabul: PriceAllocator (çarpan, ek, yuvarlama, taban, eşik) ve senkron kararı (ADR-028).</summary>
public sealed class PriceAllocatorTests
{
    [Fact]
    public void Kural_yoksa_SKU_fiyati_aynen_gider_liste_yoksa_satis_kullanilir()
    {
        var t = PriceAllocator.Compute(null, 499.90m, null, PriceRule.Default);
        t.Should().Be(new PriceTarget(499.90m, 499.90m, false, false, null));
    }

    [Theory]
    [InlineData(100, 1.2, 0, 120)]
    [InlineData(100, 1, 15, 115)]
    [InlineData(99.99, 1.15, 0, 114.99)]       // 114.9885 → kuruşa yuvarlanır
    [InlineData(100, 0.9, -5, 85)]
    public void Carpan_ve_sabit_ek_uygulanir(decimal sale, decimal multiplier, decimal add, decimal expected) =>
        PriceAllocator.Compute(null, sale, null, new PriceRule(multiplier, add)).SalePrice.Should().Be(expected);

    [Theory]
    [InlineData(123.40, PriceRounding.None, 123.40)]
    [InlineData(123.45, PriceRounding.Whole, 123)]
    [InlineData(123.50, PriceRounding.Whole, 124)]
    [InlineData(123.40, PriceRounding.Ends99, 123.99)]
    [InlineData(123.00, PriceRounding.Ends99, 123.99)]
    [InlineData(123.40, PriceRounding.Ends90, 123.90)]
    [InlineData(123.95, PriceRounding.Ends90, 124.90)]
    [InlineData(123.90, PriceRounding.Ends90, 123.90)]
    public void Yuvarlama(decimal value, PriceRounding rounding, decimal expected) =>
        PriceAllocator.Round(value, rounding).Should().Be(expected);

    [Fact]
    public void Kurus_99_ve_90_hesaplanan_fiyati_hicbir_zaman_dusurmez()
    {
        for (var cents = 0; cents < 100; cents++)
        {
            var v = 50m + cents / 100m;
            PriceAllocator.Round(v, PriceRounding.Ends99).Should().BeGreaterThanOrEqualTo(v);
            PriceAllocator.Round(v, PriceRounding.Ends90).Should().BeGreaterThanOrEqualTo(v);
        }
    }

    [Fact]
    public void Taban_asla_delinmez_ve_isaretlenir()
    {
        var t = PriceAllocator.Compute(null, 100m, null, new PriceRule(0.5m, 0m, PriceRounding.None, Floor: 80m));
        t.SalePrice.Should().Be(80m);
        t.FloorApplied.Should().BeTrue();
        t.ListPrice.Should().Be(80m, "liste satıştan düşük olamaz");

        var above = PriceAllocator.Compute(null, 200m, null, new PriceRule(0.5m, 0m, PriceRounding.None, Floor: 80m));
        above.SalePrice.Should().Be(100m);
        above.FloorApplied.Should().BeFalse();
    }

    [Fact]
    public void Ilan_fiyati_kurali_atlar_ama_taban_yine_uygulanir()
    {
        var rule = new PriceRule(2m, 10m, PriceRounding.Ends99, Floor: 50m);
        var t = PriceAllocator.Compute(200m, 100m, 75.5m, rule);
        t.SalePrice.Should().Be(75.5m);
        t.FromOverride.Should().BeTrue();
        t.ListPrice.Should().Be(410.99m, "liste kuralla hesaplanır: 200 × 2 + 10 = 410,00 → ,99");

        PriceAllocator.Compute(null, 100m, 40m, rule).SalePrice.Should().Be(50m, "elle fiyat da tabanın altına inemez");
    }

    [Fact]
    public void Liste_fiyati_satistan_dusuk_cikarsa_satisa_esitlenir()
    {
        var t = PriceAllocator.Compute(100m, 120m, null, PriceRule.Default);
        t.ListPrice.Should().Be(120m);
    }

    [Theory]
    [InlineData(null, "SKU satış fiyatı tanımlı değil")]
    [InlineData(-1.0, "0'dan büyük")]
    public void Fiyatsiz_veya_gecersiz_SKU_gonderilemez(double? sale, string problem)
    {
        var t = PriceAllocator.Compute(null, sale is null ? null : (decimal)sale, null, PriceRule.Default);
        t.Sendable.Should().BeFalse();
        t.Problem.Should().Contain(problem);
    }

    [Fact]
    public void Gecersiz_kural_gonderimi_engeller()
    {
        PriceAllocator.Compute(null, 100m, null, new PriceRule(0m)).Problem.Should().Contain("Çarpan");
        PriceAllocator.Compute(null, 100m, null, new PriceRule(1m, -200m)).Problem.Should().Contain("0 veya negatif");
        new PriceRule(11m).Validate().Should().NotBeEmpty();
        new PriceRule(1m, 0m, PriceRounding.None, -1m).Validate().Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(100, 130, 30, false)]   // tam eşik: aşmıyor
    [InlineData(100, 130.01, 30, true)]
    [InlineData(100, 69.99, 30, true)]  // düşüş de sayılır
    [InlineData(100, 70, 30, false)]
    [InlineData(100, 101, 0, true)]     // eşik 0: her değişiklik onay ister
    public void Esik(decimal last, decimal next, decimal threshold, bool exceeds) =>
        PriceAllocator.ExceedsThreshold(last, next, threshold).Should().Be(exceeds);

    [Fact]
    public void Ilk_gonderim_esige_takilmaz()
    {
        PriceAllocator.ExceedsThreshold(null, 1000m, 30m).Should().BeFalse();
        PriceAllocator.ChangePercent(null, 1000m).Should().BeNull();
    }

    // ------------------------------------------------------------------ PriceSyncPolicy

    private static readonly PriceTarget T150 = new(150m, 150m, false, false, null);

    [Fact]
    public void Karar_sirasi()
    {
        PriceSyncPolicy.Decide(PriceTarget.Unpriceable("x"), 100m, 100m, 30m, "ACIK", null, null).Action.Should().Be(PriceSyncAction.Blocked);
        PriceSyncPolicy.Decide(T150, 150m, 150m, 30m, "ACIK", null, null).Action.Should().Be(PriceSyncAction.NoChange);
        PriceSyncPolicy.Decide(T150, 140m, 140m, 30m, "ACIK", null, null).Action.Should().Be(PriceSyncAction.Send);

        var threshold = PriceSyncPolicy.Decide(T150, 100m, 100m, 30m, "ACIK", null, null);
        threshold.Action.Should().Be(PriceSyncAction.NeedsApproval);
        threshold.ApprovalReason.Should().Be(PriceApprovalReasons.Threshold);
        threshold.ChangePercent.Should().Be(50m);

        PriceSyncPolicy.Decide(T150, 100m, 100m, 30m, "ACIK", (150m, 150m), null).Action.Should().Be(PriceSyncAction.Send, "onaylanan değer gider");
        PriceSyncPolicy.Decide(T150, 100m, 100m, 30m, "ACIK", (160m, 160m), null).Action.Should().Be(PriceSyncAction.NeedsApproval, "onay başka bir değer içindi");
        PriceSyncPolicy.Decide(T150, 100m, 100m, 30m, "ACIK", null, (150m, 150m)).Action.Should().Be(PriceSyncAction.Rejected);

        var trial = PriceSyncPolicy.Decide(T150, 149m, 149m, 30m, CapabilityStates.Trial, null, null);
        trial.Action.Should().Be(PriceSyncAction.NeedsApproval, "DENEME: küçük değişiklik de onay ister");
        trial.ApprovalReason.Should().Be(PriceApprovalReasons.Trial);
        PriceSyncPolicy.Decide(T150, null, null, 30m, CapabilityStates.Trial, null, null).Action.Should().Be(PriceSyncAction.NeedsApproval, "DENEME: ilk gönderim de");
    }

    [Fact]
    public void Liste_fiyati_degisimi_de_gonderim_sayilir()
    {
        var t = new PriceTarget(100m, 130m, false, false, null);
        PriceSyncPolicy.Decide(t, 100m, 120m, 30m, "ACIK", null, null).Action.Should().Be(PriceSyncAction.Send);
    }

    // ------------------------------------------------------------------ Kanal kuralı ve yetenek açma

    [Fact]
    public void KDV_dogrulanmamissa_yetenek_acilamaz()
    {
        new PriceUpdateRule(true, null, true, false, "x").CanEnable.Should().BeFalse();
        new PriceUpdateRule(true, null, true, false, "x").BlockReason.Should().Contain("KDV");
        new PriceUpdateRule(true, false, true, false, "x").CanEnable.Should().BeFalse("KDV hariç: KDV oranı tutulmuyor");
        new PriceUpdateRule(true, true, true, false, "x").CanEnable.Should().BeTrue();
        PriceUpdateRule.NotSupported("y").CanEnable.Should().BeFalse();
    }

    // ------------------------------------------------------------------ Excel fiyat

    [Theory]
    [InlineData("499,90", 499.90)]
    [InlineData("499.90", 499.90)]
    [InlineData("1.249,90", 1249.90)]
    [InlineData("1,249.90", 1249.90)]
    [InlineData("₺ 1.249,90", 1249.90)]
    [InlineData("1249 TL", 1249)]
    [InlineData("1249", 1249)]
    public void Fiyat_metni_okunur(string text, decimal expected)
    {
        ImportValidators.TryParsePrice(text, out var value).Should().BeTrue();
        value.Should().Be(expected);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1,234")]     // binlik mi ondalık mı belirsiz
    [InlineData("1.2.3")]
    [InlineData("")]
    public void Belirsiz_veya_gecersiz_fiyat_reddedilir(string text) =>
        ImportValidators.TryParsePrice(text, out _).Should().BeFalse();

    [Fact]
    public void Excel_fiyat_satirlari_dogrulanir()
    {
        var cols = ExcelLayouts.Prices;
        RawRow Row(int n, string? sku, string? sale, string? list) => new(n, new Dictionary<string, string?>
        {
            [cols[0].Key] = sku, [cols[1].Key] = sale, [cols[2].Key] = list
        });

        var outcome = ImportValidators.ValidatePrices(
        [
            Row(2, "A", "100", "120"),
            Row(3, "B", "100", "90"),
            Row(4, "C", "0", null),
            Row(5, "D", "10.555", null),
            Row(6, null, "10", null),
            Row(7, "E", "10", null),
            Row(8, "E", "11", null)
        ]);

        outcome.Valid.Should().ContainSingle().Which.Should().Be(new PriceRow(2, "A", 100m, 120m));
        outcome.Errors.Select(e => e.Row).Distinct().Should().Equal(3, 4, 5, 6, 7, 8);
        outcome.Errors.Should().Contain(e => e.Row == 5 && e.Message.Contains("2 ondalık"));
    }
}
