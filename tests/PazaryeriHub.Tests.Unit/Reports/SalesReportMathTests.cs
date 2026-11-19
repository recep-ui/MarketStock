using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Reports;

namespace PazaryeriHub.Tests.Unit.Reports;

/// <summary>Satış raporu (ADR-034): dönem çözümü, Türkiye günü → UTC aralığı, boş günlerin doldurulması, oranlar.</summary>
public sealed class SalesReportMathTests
{
    private static readonly DateOnly Today = new(2026, 11, 19);
    private static readonly TimeZoneInfo Turkey = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    [Fact]
    public void Tarih_verilmezse_bugun_dahil_son_30_gun()
    {
        var period = ReportPeriod.Resolve(null, null, Today);

        period.From.Should().Be(new DateOnly(2026, 10, 21));
        period.To.Should().Be(Today);
        period.Days.Should().Be(30);
    }

    [Fact]
    public void Yalnizca_baslangic_verilirse_bitis_bugun()
    {
        var period = ReportPeriod.Resolve(new DateOnly(2026, 11, 1), null, Today);

        period.From.Should().Be(new DateOnly(2026, 11, 1));
        period.To.Should().Be(Today);
    }

    [Fact]
    public void Yalnizca_bitis_verilirse_30_gun_geriye()
    {
        var period = ReportPeriod.Resolve(null, new DateOnly(2026, 6, 30), Today);

        period.From.Should().Be(new DateOnly(2026, 6, 1));
        period.Days.Should().Be(30);
    }

    [Fact]
    public void Baslangic_bitisten_sonra_ise_reddedilir()
    {
        var act = () => ReportPeriod.Resolve(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 9), Today);

        act.Should().Throw<RequestValidationException>().Which.Errors.Should().ContainKey("from");
    }

    [Fact]
    public void En_fazla_366_gun_istenebilir()
    {
        ReportPeriod.Resolve(new DateOnly(2025, 11, 19), Today, Today).Days.Should().Be(366);

        var act = () => ReportPeriod.Resolve(new DateOnly(2025, 11, 18), Today, Today);
        act.Should().Throw<RequestValidationException>().Which.Errors.Should().ContainKey("to");
    }

    [Fact]
    public void Turkiye_gunu_UTC_araligina_cevrilir()
    {
        var (fromUtc, toUtc) = new ReportPeriod(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2)).ToUtcRange(Turkey);

        fromUtc.Should().Be(new DateTime(2026, 10, 31, 21, 0, 0, DateTimeKind.Utc));
        toUtc.Should().Be(new DateTime(2026, 11, 2, 21, 0, 0, DateTimeKind.Utc), "bitiş günü dahil: ertesi gün 00:00 (TR) hariç");
        fromUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Satis_olmayan_gunler_sifirla_doldurulur_ve_kanallar_toplanir()
    {
        var period = new ReportPeriod(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 4));
        var rows = new[]
        {
            new DailyChannelSales(new DateOnly(2026, 11, 2), "TRENDYOL", 2, 150m),
            new DailyChannelSales(new DateOnly(2026, 11, 2), "BEYMEN", 1, 50m),
            new DailyChannelSales(new DateOnly(2026, 11, 4), "TRENDYOL", 1, 10m),
            new DailyChannelSales(new DateOnly(2026, 10, 31), "TRENDYOL", 9, 999m) // dönem dışı: yok sayılır
        };

        var days = SalesReportMath.FillDays(period, rows);

        days.Select(d => d.Day).Should().Equal(
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 4));
        days.Select(d => d.Orders).Should().Equal(0, 3, 0, 1);
        days.Select(d => d.Revenue).Should().Equal(0m, 200m, 0m, 10m);
        days[1].RevenueByChannel.Should().BeEquivalentTo(new Dictionary<string, decimal> { ["TRENDYOL"] = 150m, ["BEYMEN"] = 50m });
        days[0].RevenueByChannel.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 3, 33.3)]
    [InlineData(2, 3, 66.7)]
    [InlineData(5, 5, 100)]
    public void Oran_yuzde_olarak_bir_ondalikla_hesaplanir_payda_sifirsa_sifir(int part, int whole, double expected) =>
        SalesReportMath.Percent(part, whole).Should().Be((decimal)expected);

    [Fact]
    public void Ortalama_sepet_siparis_yoksa_sifir() =>
        SalesReportMath.Average(0m, 0).Should().Be(0m);

    [Fact]
    public void Ortalama_sepet_kurusa_yuvarlanir() =>
        SalesReportMath.Average(100m, 3).Should().Be(33.33m);
}
