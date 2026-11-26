using System.Globalization;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Excel;

namespace PazaryeriHub.Infrastructure.Excel;

/// <summary>
/// Panel listelerini Excel'e çevirir (ADR-060). Veritabanına erişmez; yalnızca gelen satırları biçimler.
/// Metin hücreleri her zaman metin olarak yazılır (formül enjeksiyonu yok), kod/barkod sütunlarında baştaki sıfırlar korunur.
/// </summary>
public static class TableExportWriter
{
    public const int MaxRows = 100_000;
    public const int MaxColumns = 100;
    public const int MaxCellLength = 32_000; // Excel hücre sınırı 32.767

    private static readonly TimeZoneInfo Turkey = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    public static byte[] Write(TableExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var columns = request.Columns ?? [];
        var rows = request.Rows ?? [];
        var v = new Validator()
            .Must(columns.Count is > 0 and <= MaxColumns, "columns", $"1-{MaxColumns} sütun olmalı.")
            .Must(columns.All(c => !string.IsNullOrWhiteSpace(c.Header)), "columns", "Sütun başlığı boş olamaz.")
            .Must(columns.All(c => c.Kind is null || TableExportKinds.All.Contains(c.Kind)), "columns",
                $"Sütun türü {string.Join(", ", TableExportKinds.All)} olmalı.")
            .Must(rows.Count <= MaxRows, "rows", $"En fazla {MaxRows:N0} satır dışa aktarılabilir. Filtreyi daraltın.");
        if (rows.Count <= MaxRows)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i] is null || rows[i].Count != columns.Count)
                {
                    v.Add("rows", $"{i + 1}. satırdaki değer sayısı sütun sayısıyla aynı olmalı.");
                    break;
                }
            }
        }
        v.ThrowIfInvalid();

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SheetName(request.SheetName));
        for (var c = 0; c < columns.Count; c++)
        {
            sheet.Cell(1, c + 1).Value = columns[c].Header!.Trim();
            var format = (columns[c].Kind ?? TableExportKinds.Text) switch
            {
                TableExportKinds.Money => "#,##0.00",
                TableExportKinds.Date => "dd.mm.yyyy",
                TableExportKinds.DateTime => "dd.mm.yyyy hh:mm",
                TableExportKinds.Number => null,
                _ => "@"
            };
            if (format is not null)
            {
                sheet.Column(c + 1).Style.NumberFormat.Format = format;
            }
        }
        var header = sheet.Range(1, 1, 1, columns.Count);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#DDEBF7");

        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < columns.Count; c++)
            {
                SetCell(sheet.Cell(r + 2, c + 1), rows[r][c], columns[c].Kind ?? TableExportKinds.Text);
            }
        }

        sheet.SheetView.FreezeRows(1);
        if (rows.Count > 0)
        {
            sheet.Range(1, 1, rows.Count + 1, columns.Count).SetAutoFilter();
        }
        // Büyük listelerde tüm satırları ölçmek yavaş: genişlik ilk satırlara göre
        sheet.Columns(1, columns.Count).AdjustToContents(1, Math.Min(rows.Count + 1, 500));
        foreach (var column in sheet.Columns(1, columns.Count))
        {
            if (column.Width > 60)
            {
                column.Width = 60;
            }
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void SetCell(IXLCell cell, JsonElement value, string kind)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return;
            case JsonValueKind.True or JsonValueKind.False:
                cell.Value = value.GetBoolean() ? "Evet" : "Hayır";
                return;
            case JsonValueKind.Number when value.TryGetDecimal(out var number):
                if (kind is TableExportKinds.Text)
                {
                    SetText(cell, number.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    cell.Value = number;
                }
                return;
            case JsonValueKind.String:
                var text = value.GetString() ?? "";
                if (kind is TableExportKinds.Date or TableExportKinds.DateTime
                    && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                {
                    var local = TimeZoneInfo.ConvertTimeFromUtc(at.UtcDateTime, Turkey);
                    cell.Value = kind == TableExportKinds.Date && text.Length == 10 ? at.Date : local;
                    return;
                }
                SetText(cell, text);
                return;
            default:
                SetText(cell, value.GetRawText());
                return;
        }
    }

    /// <summary>Metin her zaman metin: "=..." formül olarak yorumlanmaz, sayı biçimi "@" olur.</summary>
    private static void SetText(IXLCell cell, string text)
    {
        if (text.Length > MaxCellLength)
        {
            text = text[..MaxCellLength];
        }
        cell.SetValue(text);
        cell.Style.NumberFormat.Format = "@";
    }

    /// <summary>Excel sayfa adı: en fazla 31 karakter, : \ / ? * [ ] yerine "-"; boşsa "Liste".</summary>
    public static string SheetName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Liste";
        }
        var sb = new StringBuilder(name.Trim().Length);
        foreach (var ch in name.Trim())
        {
            sb.Append(ch is ':' or '\\' or '/' or '?' or '*' or '[' or ']' ? '-' : ch);
        }
        var result = sb.ToString();
        return result.Length > 31 ? result[..31].TrimEnd() : result;
    }

    /// <summary>Dosya adı kökü: yalnızca harf, rakam, "-" ve "_"; boşluk "-" olur; yol parçaları atılır.</summary>
    public static string FileStem(string? name)
    {
        var sb = new StringBuilder();
        foreach (var ch in (name ?? "").Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                sb.Append(ch);
            }
            else if (ch is ' ' && sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }
        var result = sb.ToString().Trim('-');
        if (result.Length > 60)
        {
            result = result[..60];
        }
        return result.Length == 0 ? "liste" : result;
    }
}
