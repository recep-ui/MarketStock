using System.Globalization;
using ClosedXML.Excel;
using PazaryeriHub.Application.Excel;

namespace PazaryeriHub.Infrastructure.Excel;

public sealed record SheetReadResult(IReadOnlyList<RawRow> Rows, IReadOnlyList<string> FileErrors);

/// <summary>ClosedXML ile şablon okuma/yazma. İş kuralı içermez.</summary>
public static class ExcelWorkbook
{
    private const string ExampleSheet = "Aciklama";

    /// <summary>
    /// İlk çalışma sayfasını okur. 1. satır başlıktır; sütunlar başlık adıyla bulunur (sıra serbest, bilinmeyen sütunlar yok sayılır).
    /// Tamamen boş satırlar atlanır. Hücreler metne çevrilir (sayısal barkodlar bilimsel gösterime düşmeden).
    /// </summary>
    public static SheetReadResult Read(Stream stream, IReadOnlyList<ExcelColumn> layout)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new([], ["Dosya okunamadı. Dosya .xlsx biçiminde olmalı (eski .xls veya .csv desteklenmez)."]);
        }

        using (workbook)
        {
            var sheet = workbook.Worksheets.FirstOrDefault(w => !w.Name.Equals(ExampleSheet, StringComparison.OrdinalIgnoreCase));
            if (sheet is null)
            {
                return new([], ["Dosyada çalışma sayfası yok."]);
            }

            var headerCells = sheet.Row(1).CellsUsed().ToList();
            var columnByKey = new Dictionary<string, int>(StringComparer.Ordinal);
            var fileErrors = new List<string>();
            foreach (var cell in headerCells)
            {
                var key = ExcelText.ToKey(CellText(cell));
                if (key.Length == 0)
                {
                    continue;
                }
                if (!columnByKey.TryAdd(key, cell.Address.ColumnNumber) && layout.Any(c => c.Key == key))
                {
                    fileErrors.Add($"'{CellText(cell)}' sütunu birden fazla kez var.");
                }
            }

            foreach (var col in layout.Where(c => c.Required && !columnByKey.ContainsKey(c.Key)))
            {
                fileErrors.Add($"Zorunlu sütun bulunamadı: '{col.Header}'. Şablonu indirip başlıkları değiştirmeden kullanın.");
            }

            var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
            if (lastRow - 1 > ExcelLayouts.MaxDataRows)
            {
                fileErrors.Add($"Dosyada en fazla {ExcelLayouts.MaxDataRows:N0} veri satırı olabilir ({lastRow - 1:N0} satır var).");
            }

            if (fileErrors.Count > 0)
            {
                return new([], fileErrors);
            }

            var rows = new List<RawRow>(Math.Max(0, lastRow - 1));
            for (var r = 2; r <= lastRow; r++)
            {
                var values = new Dictionary<string, string?>(StringComparer.Ordinal);
                var anyValue = false;
                foreach (var col in layout)
                {
                    string? value = null;
                    if (columnByKey.TryGetValue(col.Key, out var c))
                    {
                        value = Clean(CellText(sheet.Cell(r, c)));
                    }
                    values[col.Key] = value;
                    anyValue |= value is not null;
                }

                if (anyValue)
                {
                    rows.Add(new RawRow(r, values));
                }
            }

            return new(rows, []);
        }
    }

    /// <summary>
    /// Şablon biçiminde çalışma kitabı üretir: veri sayfası (başlık + satırlar) ve "Aciklama" sayfası.
    /// <paramref name="extraHeaders"/> yalnızca bilgi amaçlıdır (içe aktarmada yok sayılır).
    /// </summary>
    public static byte[] Write(string sheetName, IReadOnlyList<ExcelColumn> layout, IReadOnlyList<string> extraHeaders, IEnumerable<object?[]> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(sheetName);
        var totalColumns = layout.Count + extraHeaders.Count;

        for (var i = 0; i < layout.Count; i++)
        {
            sheet.Cell(1, i + 1).Value = layout[i].Header;
            // Kod/barkod sütunları metin: Excel 8690000000017'yi sayıya çevirip bozmasın
            sheet.Column(i + 1).Style.NumberFormat.Format = layout[i].MaxLength is null ? "0" : "@";
        }
        for (var i = 0; i < extraHeaders.Count; i++)
        {
            sheet.Cell(1, layout.Count + i + 1).Value = extraHeaders[i];
        }

        var header = sheet.Range(1, 1, 1, totalColumns);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#DDEBF7");
        if (extraHeaders.Count > 0)
        {
            sheet.Range(1, layout.Count + 1, 1, totalColumns).Style.Fill.BackgroundColor = XLColor.FromHtml("#EDEDED");
        }
        foreach (var col in layout.Select((c, i) => (c, i)).Where(x => x.c.Required))
        {
            sheet.Cell(1, col.i + 1).Style.Font.FontColor = XLColor.FromHtml("#C00000");
        }

        var r = 2;
        foreach (var row in rows)
        {
            for (var c = 0; c < row.Length && c < totalColumns; c++)
            {
                sheet.Cell(r, c + 1).Value = row[c] switch
                {
                    null => Blank.Value,
                    int n => n,
                    decimal d => d,
                    DateTime dt => dt,
                    var other => Convert.ToString(other, CultureInfo.InvariantCulture)
                };
            }
            r++;
        }

        sheet.SheetView.FreezeRows(1);
        for (var c = 1; c <= totalColumns; c++)
        {
            sheet.Column(c).Width = 22;
        }

        var help = workbook.Worksheets.Add(ExampleSheet);
        help.Cell(1, 1).Value = "Sütun";
        help.Cell(1, 2).Value = "Zorunlu";
        help.Cell(1, 3).Value = "Açıklama";
        help.Cell(1, 4).Value = "Örnek";
        help.Range(1, 1, 1, 4).Style.Font.Bold = true;
        for (var i = 0; i < layout.Count; i++)
        {
            help.Cell(i + 2, 1).Value = layout[i].Header;
            help.Cell(i + 2, 2).Value = layout[i].Required ? "Evet" : "Hayır";
            help.Cell(i + 2, 3).Value = layout[i].Description;
            help.Cell(i + 2, 4).SetValue(layout[i].Example);
        }
        var note = layout.Count + 3;
        help.Cell(note, 1).Value = "Notlar";
        help.Cell(note, 1).Style.Font.Bold = true;
        help.Cell(note + 1, 1).Value = "Veriler ilk sayfaya, 2. satırdan itibaren girilir. Başlık adlarını değiştirmeyin; sütun sırası serbesttir.";
        help.Cell(note + 2, 1).Value = "Gri başlıklı sütunlar yalnızca bilgi içindir, içe aktarmada dikkate alınmaz.";
        help.Cell(note + 3, 1).Value = "Hatalı satırlar uygulanmaz ve satır numarasıyla raporlanır; geçerli satırlar uygulanır.";
        help.Column(1).Width = 20;
        help.Column(2).Width = 10;
        help.Column(3).Width = 90;
        help.Column(4).Width = 22;

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    internal static string? CellText(IXLCell cell)
    {
        var value = cell.Value;
        if (value.IsBlank)
        {
            return null;
        }
        if (value.IsText)
        {
            return value.GetText();
        }
        if (value.IsNumber)
        {
            var d = value.GetNumber();
            return d == Math.Floor(d) && Math.Abs(d) < 1e15
                ? ((long)d).ToString(CultureInfo.InvariantCulture)
                : d.ToString("G15", CultureInfo.InvariantCulture);
        }
        if (value.IsBoolean)
        {
            return value.GetBoolean() ? "1" : "0";
        }
        return cell.GetFormattedString();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
