using PazaryeriHub.Application.Panel;
using PazaryeriHub.Application.Stock;
using PazaryeriHub.Application.Sync;

namespace PazaryeriHub.Tests.Unit.Sync;

/// <summary>
/// ADR-040: son mutabakatta kanalda bulunamayan ilan (çekim var, değer yok) "kanalda yok" sayılır ve sapma hesabına girmez.
/// Hiç çekilmemiş ilan (çekim yok) "kanalda yok" sayılmaz.
/// </summary>
public sealed class MissingInChannelTests
{
    private static readonly DateTime At = new(2026, 11, 20, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Stok_listesi_hucresi_kanalda_yoksa_sapma_saymaz()
    {
        var missing = new ChannelStockCell("TRENDYOL", 1, "AKTIF", Expected: 3, LastSent: 3, ChannelStock: null, FetchedAtUtc: At);
        missing.MissingInChannel.Should().BeTrue();
        missing.HasDeviation.Should().BeFalse();
        new StockOverviewRow(1, "SKU", "Ürün", 3, 0, 0, 3, [missing]).HasDeviation.Should().BeFalse();

        new ChannelStockCell("TRENDYOL", 1, "AKTIF", 3, 3, null, null).MissingInChannel.Should().BeFalse("hiç çekilmedi");
        var found = new ChannelStockCell("TRENDYOL", 1, "AKTIF", 3, 3, 100, At);
        found.MissingInChannel.Should().BeFalse();
        found.HasDeviation.Should().BeTrue();
    }

    [Fact]
    public void Kanal_listesi_satiri_kanalda_yoksa_sapma_saymaz()
    {
        var side = new ChannelSide("869", "K1", null, null, null, null, null, null);
        ChannelStockListRow Row(int? stock, DateTime? at) =>
            new(1, "AKTIF", side, null, stock, at, 1, "SKU", "869", "Ürün", null, null, 3, 3, "BARKOD", []);

        Row(null, At).Should().Match<ChannelStockListRow>(r => r.MissingInChannel && !r.HasDeviation);
        Row(null, null).MissingInChannel.Should().BeFalse();
        Row(100, At).Should().Match<ChannelStockListRow>(r => !r.MissingInChannel && r.HasDeviation);
    }

    [Fact]
    public void Mutabakat_satiri_kanalda_yoksa_sapma_degeri_yok()
    {
        var missing = new ReconciliationRow(1, "SKU", "TRENDYOL", "K1", "869", 3, 3, null, At);
        missing.MissingInChannel.Should().BeTrue();
        missing.Deviation.Should().BeNull();

        new ReconciliationRow(1, "SKU", "TRENDYOL", "K1", "869", 3, 3, null, null).MissingInChannel.Should().BeFalse();
        new ReconciliationRow(1, "SKU", "TRENDYOL", "K1", "869", 3, 3, 100, At).Deviation.Should().Be(97);
    }

    [Fact]
    public void Deftere_alma_onizlemesinde_kanalda_yok_isaretlenir()
    {
        new IntakeChannelValue(1, "TRENDYOL", "869", "K1", null, At, 3, false).MissingInChannel.Should().BeTrue();
        new IntakeChannelValue(1, "TRENDYOL", "869", "K1", null, null, 3, false).MissingInChannel.Should().BeFalse();
        new IntakeChannelValue(1, "TRENDYOL", "869", "K1", 5, At, 3, false).MissingInChannel.Should().BeFalse();
    }
}
