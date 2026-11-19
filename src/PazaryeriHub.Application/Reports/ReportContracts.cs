using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Orders;

namespace PazaryeriHub.Application.Reports;

/// <summary>
/// Rapor dönemi: Türkiye günleri, iki uç dahil (ADR-034). Tarih verilmezse bugün dahil son 30 gün; en fazla 366 gün.
/// </summary>
public sealed record ReportPeriod(DateOnly From, DateOnly To)
{
    public const int DefaultDays = 30;
    public const int MaxDays = 366;

    public int Days => To.DayNumber - From.DayNumber + 1;

    /// <summary>Panelden gelen isteğe bağlı tarihleri çözer ve doğrular (<see cref="RequestValidationException"/>).</summary>
    public static ReportPeriod Resolve(DateOnly? from, DateOnly? to, DateOnly today)
    {
        var end = to ?? today;
        var start = from ?? end.AddDays(-(DefaultDays - 1));
        var period = new ReportPeriod(start, end);
        new Validator()
            .Must(start <= end, "from", "Başlangıç tarihi bitiş tarihinden sonra olamaz.")
            .Must(start > end || period.Days <= MaxDays, "to", $"En fazla {MaxDays} günlük dönem seçilebilir.")
            .ThrowIfInvalid();
        return period;
    }

    /// <summary>Dönemin UTC karşılığı: [ilk gün 00:00, son günün ertesi 00:00) yerel saatle.</summary>
    public (DateTime FromUtc, DateTime ToUtc) ToUtcRange(TimeZoneInfo zone) =>
        (TimeZoneInfo.ConvertTimeToUtc(From.ToDateTime(TimeOnly.MinValue), zone),
         TimeZoneInfo.ConvertTimeToUtc(To.AddDays(1).ToDateTime(TimeOnly.MinValue), zone));
}

/// <summary>Veritabanından gelen gün × kanal net satış satırı.</summary>
public sealed record DailyChannelSales(DateOnly Day, string ChannelCode, int Orders, decimal Revenue);

/// <summary>Grafikteki bir gün: net sipariş ve ciro, kanal kırılımıyla.</summary>
public sealed record SalesDay(DateOnly Day, int Orders, decimal Revenue, IReadOnlyDictionary<string, decimal> RevenueByChannel);

/// <summary>Rapor hesaplarının saf kısmı (birim testli).</summary>
public static class SalesReportMath
{
    /// <summary>Dönemdeki her gün için bir kayıt üretir; satışsız günler 0. Dönem dışındaki satırlar yok sayılır.</summary>
    public static IReadOnlyList<SalesDay> FillDays(ReportPeriod period, IEnumerable<DailyChannelSales> rows)
    {
        ArgumentNullException.ThrowIfNull(period);
        var byDay = rows.Where(r => r.Day >= period.From && r.Day <= period.To).ToLookup(r => r.Day);
        var result = new List<SalesDay>(period.Days);
        for (var day = period.From; day <= period.To; day = day.AddDays(1))
        {
            var items = byDay[day].ToList();
            result.Add(new SalesDay(day, items.Sum(i => i.Orders), items.Sum(i => i.Revenue),
                items.GroupBy(i => i.ChannelCode).ToDictionary(g => g.Key, g => g.Sum(i => i.Revenue))));
        }
        return result;
    }

    /// <summary>Yüzde oranı (bir ondalık); payda 0 ise 0.</summary>
    public static decimal Percent(int part, int whole) =>
        whole <= 0 ? 0m : Math.Round(part * 100m / whole, 1, MidpointRounding.AwayFromZero);

    /// <summary>Ortalama (kuruşa yuvarlanır); adet 0 ise 0.</summary>
    public static decimal Average(decimal total, int count) =>
        count <= 0 ? 0m : Math.Round(total / count, 2, MidpointRounding.AwayFromZero);
}

/// <summary>Dönem toplamları. Net = İptal ve İade dışındaki siparişler (ADR-034).</summary>
/// <param name="TotalOrders">Dönemde verilen tüm siparişler (her durum).</param>
/// <param name="NetRevenue">İptal ve İade hariç sipariş toplamı.</param>
/// <param name="Units">Net siparişlerdeki ürün adedi.</param>
/// <param name="ReturnedAmount">İade durumundaki siparişlerin toplamı.</param>
/// <param name="CancelRate">İptal / tüm siparişler (%).</param>
/// <param name="ReturnRate">İade / (tüm siparişler − iptal) (%).</param>
public sealed record SalesTotals(
    int TotalOrders, int NetOrders, int CancelledOrders, int ReturnedOrders, decimal NetRevenue, int Units,
    decimal AverageBasket, decimal ReturnedAmount, decimal CancelRate, decimal ReturnRate);

public sealed record ChannelSales(
    string ChannelCode, int TotalOrders, int NetOrders, int CancelledOrders, int ReturnedOrders, decimal NetRevenue, int Units, decimal ReturnRate);

/// <param name="Revenue">Satır tutarı toplamı (adet × birim fiyat).</param>
public sealed record TopSku(int SkuId, string SkuCode, string ProductName, int Units, decimal Revenue, int Orders, int Available);

/// <summary>Stoğu olduğu hâlde dönemde hiç satılmayan SKU.</summary>
public sealed record SlowMover(int SkuId, string SkuCode, string ProductName, int Physical, int Available, DateTime? LastSoldUtc);

public sealed record SalesReport(
    ReportPeriod Period, int? ChannelId, SalesTotals Totals, IReadOnlyList<SalesDay> Daily, IReadOnlyList<ChannelSales> ByChannel,
    IReadOnlyList<TopSku> TopSkus, IReadOnlyList<SlowMover> SlowMovers, int UnmatchedUnits);

public interface ISalesReportService
{
    Task<SalesReport> GetSalesAsync(DateOnly? from, DateOnly? to, int? channelId, CancellationToken ct);

    /// <summary>
    /// Sipariş listesi filtreleriyle satır bazlı Excel (sayfalama yok). Müşteri bilgisi YOK (KVKK; tüm rollere açık).
    /// Satır sayısı sınırı aşılırsa <see cref="RequestValidationException"/>.
    /// </summary>
    Task<byte[]> ExportOrdersAsync(OrderQuery query, CancellationToken ct);
}
