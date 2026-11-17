using PazaryeriHub.Domain.Stock;

namespace PazaryeriHub.Tests.Unit.Stock;

/// <summary>Aşama 1 kabul kriteri (d): allocator birim testleri.</summary>
public sealed class StockAllocatorTests
{
    [Fact]
    public void Tam_ortak_havuzda_satilabilirin_tamami_gider() =>
        StockAllocator.Allocate(37, 100m).Should().Be(37);

    [Theory]
    [InlineData(10, 50, 5)]
    [InlineData(11, 50, 5)]      // 5.5 → FLOOR → 5
    [InlineData(100, 33.33, 33)] // 33.33 → 33
    [InlineData(3, 33.33, 0)]    // 0.9999 → 0
    [InlineData(7, 0, 0)]        // pay 0: kanala stok gitmez
    public void Pay_yuzdesi_uygulanir_ve_asagi_yuvarlanir(long available, double percent, int expected) =>
        StockAllocator.Allocate(available, (decimal)percent).Should().Be(expected);

    [Fact]
    public void Guvenlik_stogu_satilabilirden_dusulur()
    {
        var levels = new[] { new StockLevel(Physical: 20, Reserved: 5, SafetyStock: 3) };

        StockAllocator.Allocate(levels, 100m).Should().Be(12);
    }

    [Fact]
    public void Tum_depolar_toplanir()
    {
        var levels = new[]
        {
            new StockLevel(10, 2, 1),   // 7
            new StockLevel(5, 0, 0),    // 5
            new StockLevel(4, 4, 0)     // 0
        };

        StockAllocator.TotalAvailable(levels).Should().Be(12);
        StockAllocator.Allocate(levels, 50m).Should().Be(6);
    }

    [Fact]
    public void Bir_depodaki_negatif_satilabilir_toplami_dusurur()
    {
        // Spesifikasyon: Satılabilir tüm depolar toplamıdır; depo bazında sabitleme yapılmaz.
        var levels = new[] { new StockLevel(10, 0, 0), new StockLevel(2, 0, 5) }; // 10 + (-3)

        StockAllocator.Allocate(levels, 100m).Should().Be(7);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-500)]
    [InlineData(0)]
    public void Negatif_veya_sifir_sonuc_sifira_sabitlenir(long available) =>
        StockAllocator.Allocate(available, 100m).Should().Be(0);

    [Fact]
    public void Guvenlik_stogu_fazlaysa_sonuc_sifir()
    {
        var levels = new[] { new StockLevel(Physical: 5, Reserved: 2, SafetyStock: 10) };

        StockAllocator.Allocate(levels, 100m).Should().Be(0);
    }

    [Theory]
    [InlineData(100, 100, 25, 25)]  // limit uygulanır
    [InlineData(10, 100, 25, 10)]   // limit altında değişmez
    [InlineData(100, 50, 40, 40)]   // önce pay (50), sonra limit (40)
    [InlineData(100, 100, 0, 0)]    // limit 0
    public void Kanal_ust_limiti_uygulanir(long available, double percent, int cap, int expected) =>
        StockAllocator.Allocate(available, (decimal)percent, cap).Should().Be(expected);

    [Fact]
    public void Limit_yoksa_sinir_uygulanmaz() =>
        StockAllocator.Allocate(1_000_000, 100m, channelCap: null).Should().Be(1_000_000);

    [Fact]
    public void Int_tasmasi_sabitlenir() =>
        StockAllocator.Allocate(long.MaxValue / 2, 100m).Should().Be(int.MaxValue);

    [Theory]
    [InlineData(-0.01)]
    [InlineData(100.01)]
    public void Gecersiz_pay_reddedilir(double percent) =>
        FluentActions.Invoking(() => StockAllocator.Allocate(10, (decimal)percent))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void Negatif_limit_reddedilir() =>
        FluentActions.Invoking(() => StockAllocator.Allocate(10, 100m, -1))
            .Should().Throw<ArgumentOutOfRangeException>();

    // ------------------------------------------------------------------ genel düşük stok eşiği (ADR-023)

    [Theory]
    [InlineData(5, 0, 5)]     // eşik 0: mevcut davranış
    [InlineData(0, 0, 0)]
    [InlineData(5, 5, 0)]     // satılabilir == eşik → 0
    [InlineData(6, 5, 6)]     // eşik + 1 → hesaplanan değer
    [InlineData(3, 5, 0)]
    public void Esik_ve_altinda_0_gonderilir(long available, int threshold, int expected) =>
        StockAllocator.Allocate(available, 100m, null, threshold).Should().Be(expected);

    [Fact]
    public void Esik_paydan_once_ortak_havuz_uzerinde_uygulanir()
    {
        // Havuz 10 > eşik 6 → pay %50 uygulanır: 5 (eşiğin altında kalsa da sıfırlanmaz; eşik kanal payına değil havuza bakar)
        StockAllocator.Allocate(10, 50m, null, 6).Should().Be(5);
        StockAllocator.Allocate(6, 50m, null, 6).Should().Be(0);
        StockAllocator.Allocate(100, 100m, 7, 50).Should().Be(7, "üst limit eşikten sonra uygulanır");
    }

    [Fact]
    public void Negatif_esik_reddedilir()
    {
        var act = () => StockAllocator.Allocate(10, 100m, null, -1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
