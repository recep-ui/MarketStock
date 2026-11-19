using ClosedXML.Excel;
using Dapper;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Application.Reports;
using PazaryeriHub.Domain.Orders;
using PazaryeriHub.Infrastructure.Orders;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.Reports;

/// <summary>
/// Satış raporu ve sipariş Excel'i (ADR-034). Günler Türkiye saatine göre; net = İptal ve İade dışındaki siparişler.
/// Ciro <c>Order.Toplam</c>'dan (kanalın bildirdiği sipariş tutarı), SKU cirosu satır tutarından (adet × birim fiyat) hesaplanır.
/// </summary>
public sealed class SalesReportService(SqlConnectionFactory connectionFactory, TimeProvider clock) : ISalesReportService
{
    /// <summary>Excel'e aktarılabilecek en fazla satır (dosya ve bellek sınırı).</summary>
    public const int MaxExportRows = 50_000;

    private static readonly TimeZoneInfo Turkey = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    private const string Net = "o.Durum NOT IN ('Iptal', 'Iade')";
    private const string InPeriod = "o.SiparisTarihi >= @fromUtc AND o.SiparisTarihi < @toUtc AND (@channelId IS NULL OR o.ChannelId = @channelId)";

    public async Task<SalesReport> GetSalesAsync(DateOnly? from, DateOnly? to, int? channelId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, Turkey));
        var period = ReportPeriod.Resolve(from, to, today);
        var (fromUtc, toUtc) = period.ToUtcRange(Turkey);
        var args = new { fromUtc, toUtc, channelId };

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        using var multi = await connection.QueryMultipleAsync(new CommandDefinition($"""
            SELECT c.Kod AS ChannelCode,
                   COUNT(o.Id) AS TotalOrders,
                   SUM(CASE WHEN {Net} THEN 1 ELSE 0 END) AS NetOrders,
                   SUM(CASE WHEN o.Durum = 'Iptal' THEN 1 ELSE 0 END) AS CancelledOrders,
                   SUM(CASE WHEN o.Durum = 'Iade' THEN 1 ELSE 0 END) AS ReturnedOrders,
                   ISNULL(SUM(CASE WHEN {Net} THEN o.Toplam END), 0) AS NetRevenue,
                   ISNULL(SUM(CASE WHEN o.Durum = 'Iade' THEN o.Toplam END), 0) AS ReturnedAmount
              FROM dbo.Channel c
              LEFT JOIN dbo.[Order] o ON o.ChannelId = c.Id AND {InPeriod}
             WHERE (@channelId IS NULL OR c.Id = @channelId)
             GROUP BY c.Id, c.Kod
             ORDER BY c.Id;

            SELECT c.Kod AS ChannelCode, ISNULL(SUM(l.Adet), 0) AS Units, ISNULL(SUM(CASE WHEN l.SkuId IS NULL THEN l.Adet END), 0) AS Unmatched
              FROM dbo.OrderLine l JOIN dbo.[Order] o ON o.Id = l.OrderId JOIN dbo.Channel c ON c.Id = o.ChannelId
             WHERE {InPeriod} AND {Net}
             GROUP BY c.Kod;

            SELECT CAST((CAST(o.SiparisTarihi AS DATETIME2) AT TIME ZONE 'UTC') AT TIME ZONE 'Turkey Standard Time' AS DATE) AS Day,
                   c.Kod AS ChannelCode, COUNT(*) AS Orders, SUM(o.Toplam) AS Revenue
              FROM dbo.[Order] o JOIN dbo.Channel c ON c.Id = o.ChannelId
             WHERE {InPeriod} AND {Net}
             GROUP BY CAST((CAST(o.SiparisTarihi AS DATETIME2) AT TIME ZONE 'UTC') AT TIME ZONE 'Turkey Standard Time' AS DATE), c.Kod;

            SELECT TOP (10) s.Id AS SkuId, s.SkuKodu AS SkuCode, p.Ad AS ProductName, SUM(l.Adet) AS Units,
                   SUM(l.Adet * l.BirimFiyat) AS Revenue, COUNT(DISTINCT o.Id) AS Orders,
                   ISNULL((SELECT SUM(b.Satilabilir) FROM dbo.StockBalance b WHERE b.SkuId = s.Id), 0) AS Available
              FROM dbo.OrderLine l JOIN dbo.[Order] o ON o.Id = l.OrderId JOIN dbo.Sku s ON s.Id = l.SkuId JOIN dbo.Product p ON p.Id = s.ProductId
             WHERE {InPeriod} AND {Net}
             GROUP BY s.Id, s.SkuKodu, p.Ad
             ORDER BY SUM(l.Adet) DESC, SUM(l.Adet * l.BirimFiyat) DESC, s.SkuKodu;

            SELECT TOP (10) s.Id AS SkuId, s.SkuKodu AS SkuCode, p.Ad AS ProductName, SUM(b.Fiziksel) AS Physical, SUM(b.Satilabilir) AS Available,
                   (SELECT MAX(o.SiparisTarihi) FROM dbo.OrderLine l JOIN dbo.[Order] o ON o.Id = l.OrderId
                     WHERE l.SkuId = s.Id AND {Net} AND (@channelId IS NULL OR o.ChannelId = @channelId)) AS LastSoldUtc
              FROM dbo.Sku s JOIN dbo.Product p ON p.Id = s.ProductId JOIN dbo.StockBalance b ON b.SkuId = s.Id
             WHERE NOT EXISTS (SELECT 1 FROM dbo.OrderLine l JOIN dbo.[Order] o ON o.Id = l.OrderId
                                WHERE l.SkuId = s.Id AND {InPeriod} AND {Net})
             GROUP BY s.Id, s.SkuKodu, p.Ad
            HAVING SUM(b.Satilabilir) > 0
             ORDER BY SUM(b.Fiziksel) DESC, s.SkuKodu;
            """, args, cancellationToken: ct));

        var channels = (await multi.ReadAsync<ChannelRow>()).ToList();
        var units = (await multi.ReadAsync<UnitsRow>()).ToDictionary(u => u.ChannelCode);
        var daily = (await multi.ReadAsync<DailyRow>())
            .Select(d => new DailyChannelSales(DateOnly.FromDateTime(d.Day), d.ChannelCode, d.Orders, d.Revenue)).ToList();
        var top = (await multi.ReadAsync<TopSku>()).ToList();
        var slow = (await multi.ReadAsync<SlowRow>()).Select(r => new SlowMover(r.SkuId, r.SkuCode, r.ProductName, r.Physical, r.Available,
            r.LastSoldUtc is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null)).ToList();

        var byChannel = channels.Select(c => new ChannelSales(c.ChannelCode, c.TotalOrders, c.NetOrders, c.CancelledOrders, c.ReturnedOrders,
            c.NetRevenue, units.TryGetValue(c.ChannelCode, out var u) ? u.Units : 0,
            SalesReportMath.Percent(c.ReturnedOrders, c.TotalOrders - c.CancelledOrders))).ToList();

        var total = channels.Sum(c => c.TotalOrders);
        var cancelled = channels.Sum(c => c.CancelledOrders);
        var returned = channels.Sum(c => c.ReturnedOrders);
        var netOrders = channels.Sum(c => c.NetOrders);
        var netRevenue = channels.Sum(c => c.NetRevenue);
        var totals = new SalesTotals(total, netOrders, cancelled, returned, netRevenue, byChannel.Sum(c => c.Units),
            SalesReportMath.Average(netRevenue, netOrders), channels.Sum(c => c.ReturnedAmount),
            SalesReportMath.Percent(cancelled, total), SalesReportMath.Percent(returned, total - cancelled));

        return new SalesReport(period, channelId, totals, SalesReportMath.FillDays(period, daily), byChannel, top, slow,
            units.Values.Sum(u => u.Unmatched));
    }

    public async Task<byte[]> ExportOrdersAsync(OrderQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var parameters = OrderService.FilterParameters(query);
        parameters.Add("max", MaxExportRows + 1);

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        // Müşteri özeti / kargo bilgisi BİLEREK seçilmez (KVKK; dosya tüm rollere açık).
        var rows = (await connection.QueryAsync<ExportRow>(new CommandDefinition($"""
            SELECT TOP (@max) c.Ad AS ChannelName, o.KanalSiparisNo AS ChannelOrderNo, o.SiparisTarihi AS OrderDateUtc, o.Durum AS Status,
                   o.Toplam AS Total, o.StokYetersiz AS InsufficientStock, s.SkuKodu AS SkuCode, p.Ad AS ProductName,
                   l.Adet AS Quantity, l.BirimFiyat AS UnitPrice, l.Komisyon AS Commission
              FROM dbo.[Order] AS o
              JOIN dbo.Channel AS c ON c.Id = o.ChannelId
              LEFT JOIN dbo.OrderLine AS l ON l.OrderId = o.Id
              LEFT JOIN dbo.Sku AS s ON s.Id = l.SkuId
              LEFT JOIN dbo.Product AS p ON p.Id = s.ProductId
            {OrderService.FilterSql}
             ORDER BY o.SiparisTarihi DESC, o.Id DESC, l.Id
            """, parameters, cancellationToken: ct))).ToList();

        if (rows.Count > MaxExportRows)
        {
            new Validator().Must(false, "filter", $"En fazla {MaxExportRows:N0} satır dışa aktarılabilir. Tarih aralığını daraltın.").ThrowIfInvalid();
        }

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Siparisler");
        string[] headers = ["Kanal", "Sipariş no", "Sipariş tarihi", "Durum", "Sipariş toplamı", "Stok yetersiz",
            "SKU", "Ürün", "Adet", "Birim fiyat", "Komisyon", "Satır tutarı"];
        for (var i = 0; i < headers.Length; i++)
        {
            sheet.Cell(1, i + 1).Value = headers[i];
        }
        var header = sheet.Range(1, 1, 1, headers.Length);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#DDEBF7");
        sheet.Column(2).Style.NumberFormat.Format = "@"; // sipariş no metin: Excel sayıya çevirmesin
        sheet.Column(3).Style.NumberFormat.Format = "dd.mm.yyyy hh:mm";
        foreach (var col in new[] { 5, 10, 11, 12 })
        {
            sheet.Column(col).Style.NumberFormat.Format = "#,##0.00";
        }

        var r = 2;
        foreach (var row in rows)
        {
            sheet.Cell(r, 1).Value = row.ChannelName;
            sheet.Cell(r, 2).Value = row.ChannelOrderNo;
            sheet.Cell(r, 3).Value = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(row.OrderDateUtc, DateTimeKind.Utc), Turkey);
            sheet.Cell(r, 4).Value = StatusLabel(row.Status);
            sheet.Cell(r, 5).Value = row.Total;
            sheet.Cell(r, 6).Value = row.InsufficientStock ? "Evet" : "Hayır";
            if (row.Quantity is { } qty)
            {
                sheet.Cell(r, 7).Value = row.SkuCode ?? "(eşleşmedi)";
                sheet.Cell(r, 8).Value = row.ProductName ?? "";
                sheet.Cell(r, 9).Value = qty;
                sheet.Cell(r, 10).Value = row.UnitPrice ?? 0m;
                if (row.Commission is { } commission)
                {
                    sheet.Cell(r, 11).Value = commission;
                }
                sheet.Cell(r, 12).Value = qty * (row.UnitPrice ?? 0m);
            }
            r++;
        }
        sheet.SheetView.FreezeRows(1);
        if (rows.Count > 0)
        {
            sheet.Range(1, 1, r - 1, headers.Length).SetAutoFilter();
        }
        sheet.Columns(1, headers.Length).AdjustToContents(1, Math.Min(r - 1, 500));

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    internal static string StatusLabel(string status) => Enum.TryParse<OrderStatus>(status, out var s)
        ? s switch
        {
            OrderStatus.Yeni => "Yeni",
            OrderStatus.Onaylandi => "Onaylandı",
            OrderStatus.Hazirlaniyor => "Hazırlanıyor",
            OrderStatus.Kargoda => "Kargoda",
            OrderStatus.TeslimEdildi => "Teslim edildi",
            OrderStatus.Iptal => "İptal",
            OrderStatus.Iade => "İade",
            _ => status
        }
        : status;

    private sealed class ChannelRow
    {
        public string ChannelCode { get; init; } = "";
        public int TotalOrders { get; init; }
        public int NetOrders { get; init; }
        public int CancelledOrders { get; init; }
        public int ReturnedOrders { get; init; }
        public decimal NetRevenue { get; init; }
        public decimal ReturnedAmount { get; init; }
    }

    private sealed class UnitsRow
    {
        public string ChannelCode { get; init; } = "";
        public int Units { get; init; }
        public int Unmatched { get; init; }
    }

    private sealed class DailyRow
    {
        public DateTime Day { get; init; }
        public string ChannelCode { get; init; } = "";
        public int Orders { get; init; }
        public decimal Revenue { get; init; }
    }

    private sealed class SlowRow
    {
        public int SkuId { get; init; }
        public string SkuCode { get; init; } = "";
        public string ProductName { get; init; } = "";
        public int Physical { get; init; }
        public int Available { get; init; }
        public DateTime? LastSoldUtc { get; init; }
    }

    private sealed class ExportRow
    {
        public string ChannelName { get; init; } = "";
        public string ChannelOrderNo { get; init; } = "";
        public DateTime OrderDateUtc { get; init; }
        public string Status { get; init; } = "";
        public decimal Total { get; init; }
        public bool InsufficientStock { get; init; }
        public string? SkuCode { get; init; }
        public string? ProductName { get; init; }
        public int? Quantity { get; init; }
        public decimal? UnitPrice { get; init; }
        public decimal? Commission { get; init; }
    }
}
