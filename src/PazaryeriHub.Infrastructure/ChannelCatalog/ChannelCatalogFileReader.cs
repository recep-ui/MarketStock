using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using PazaryeriHub.Application.Catalog;
using PazaryeriHub.Application.ChannelCatalog;
using PazaryeriHub.Application.Excel;

namespace PazaryeriHub.Infrastructure.ChannelCatalog;

public sealed record ChannelFileReadResult(
    ChannelFileFormat? Format,
    int TotalRows,
    IReadOnlyList<ChannelCatalogItem> Items,
    IReadOnlyList<ImportRowError> Errors,
    IReadOnlyList<string> FileErrors,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Satıcı panelinden indirilen kanal ürün dosyasını okur (ClosedXML). İş kuralı yok; yalnızca okuma ve satır doğrulama:
/// <list type="bullet">
/// <item>Biçim başlık imzasıyla tanınır; seçilen kanalla uyuşmazsa dosya reddedilir.</item>
/// <item>Barkod ve kodlar METİN okunur ve kırpılır. Bilimsel gösterim (8,69E+12) veya ondalıklı kod hatalı satırdır.
///       Sayı olarak kaydedilmiş kodlar kabul edilir ama uyarı verilir (baştaki sıfırlar kaybolmuş olabilir).</item>
/// <item>Barkod boşsa, sınırı aşıyorsa veya dosyada tekrar ediyorsa satır hatalıdır.</item>
/// <item>Yalnızca biçimde tanımlı sütunlar okunur (kişisel/firma bilgisi sütunları okunmaz).</item>
/// </list>
/// </summary>
public static partial class ChannelCatalogFileReader
{
    [GeneratedRegex(@"^[+-]?\d+([.,]\d+)?[eE][+-]?\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex ScientificText();

    public static ChannelFileReadResult Read(Stream stream, string channelCode)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Fail("Dosya okunamadı. Dosya .xlsx biçiminde olmalı (eski .xls veya .csv desteklenmez).");
        }

        using (workbook)
        {
            // Biçim tanıma: başlığı (1. satır) bir kanal imzasına uyan ilk sayfa
            foreach (var sheet in workbook.Worksheets)
            {
                var columns = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var cell in sheet.Row(1).CellsUsed())
                {
                    var key = ExcelText.ToKey(cell.GetFormattedString());
                    if (key.Length > 0)
                    {
                        columns.TryAdd(key, cell.Address.ColumnNumber);
                    }
                }
                var format = ChannelFileFormats.Detect(columns.Keys);
                if (format is null)
                {
                    continue;
                }
                if (!string.Equals(format.ChannelCode, channelCode, StringComparison.Ordinal))
                {
                    return Fail($"Dosya '{format.DisplayName}' biçiminde; seçilen kanal ({channelCode}) ile uyuşmuyor.");
                }
                return ReadSheet(sheet, format, columns);
            }
        }

        return Fail($"Kanal ürün dosyası tanınmadı. Beklenen başlıklar — {ChannelFileFormats.Describe()}.");
    }

    private static ChannelFileReadResult ReadSheet(IXLWorksheet sheet, ChannelFileFormat format, Dictionary<string, int> columns)
    {
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        if (lastRow - 1 > ExcelLayouts.MaxDataRows)
        {
            return Fail($"Dosyada en fazla {ExcelLayouts.MaxDataRows:N0} veri satırı olabilir ({lastRow - 1:N0} satır var).", format);
        }

        var errors = new List<ImportRowError>();
        var items = new List<ChannelCatalogItem>();
        var numericCodeCells = 0;
        var sellerIds = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;

        for (var r = 2; r <= lastRow; r++)
        {
            var row = sheet.Row(r);
            if (row.IsEmpty())
            {
                continue;
            }
            total++;
            var rowErrors = errors.Count;

            string? Code(string? header, int maxLength)
            {
                if (header is null || !columns.TryGetValue(ExcelText.ToKey(header), out var c))
                {
                    return null;
                }
                var cell = sheet.Cell(r, c);
                var value = cell.Value;
                if (value.IsBlank)
                {
                    return null;
                }
                string text;
                if (value.IsNumber)
                {
                    var formatted = cell.GetFormattedString();
                    var number = value.GetNumber();
                    if (formatted.Contains('E', StringComparison.OrdinalIgnoreCase) || number != Math.Floor(number) || Math.Abs(number) >= 1e15)
                    {
                        errors.Add(new(r, header, $"'{header}' sayı/bilimsel gösterimle kaydedilmiş ({formatted}); gerçek değer okunamaz. Hücreyi metin biçiminde düzeltin."));
                        return null;
                    }
                    numericCodeCells++;
                    text = ((long)number).ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    text = (value.IsText ? value.GetText() : cell.GetFormattedString()).Trim();
                    if (text.Length == 0)
                    {
                        return null;
                    }
                    if (ScientificText().IsMatch(text))
                    {
                        errors.Add(new(r, header, $"'{header}' bilimsel gösterimde ({text}); gerçek değer okunamaz. Dosyayı kanal panelinden yeniden indirin."));
                        return null;
                    }
                }
                if (text.Length > maxLength)
                {
                    errors.Add(new(r, header, $"'{header}' en fazla {maxLength} karakter olabilir ({text.Length})."));
                    return null;
                }
                return text;
            }

            string? Text(string? header, int maxLength, bool truncate)
            {
                if (header is null || !columns.TryGetValue(ExcelText.ToKey(header), out var c))
                {
                    return null;
                }
                var text = sheet.Cell(r, c).GetFormattedString().Trim();
                if (text.Length == 0)
                {
                    return null;
                }
                if (text.Length > maxLength)
                {
                    if (truncate)
                    {
                        return text[..maxLength];
                    }
                    errors.Add(new(r, header, $"'{header}' en fazla {maxLength} karakter olabilir ({text.Length})."));
                    return null;
                }
                return text;
            }

            var productCode = Code(format.ChannelProductCode, CatalogLimits.ChannelProductCode);
            var barcode = Code(format.Barcode, CatalogLimits.ChannelBarcode);
            var stockCode = Code(format.StockCode, CatalogLimits.SkuCode);
            var modelCode = Code(format.ModelCode, CatalogLimits.ModelCode);
            var sellerId = Code(format.SellerId, 50);
            var color = Text(format.Color, CatalogLimits.Color, truncate: false);
            var size = Text(format.Size, CatalogLimits.Size, truncate: false);
            var name = Text(format.Name, CatalogLimits.ProductName, truncate: true);
            var brand = Text(format.Brand, CatalogLimits.Brand, truncate: true);
            var category = Text(format.Category, CatalogLimits.Category, truncate: true);
            var status = Text(format.Status, 100, truncate: true);

            if (errors.Count == rowErrors)
            {
                if (barcode is null)
                {
                    errors.Add(new(r, format.Barcode, "Barkod boş."));
                }
                if (format.ChannelProductCode is not null && productCode is null)
                {
                    errors.Add(new(r, format.ChannelProductCode, $"'{format.ChannelProductCode}' (kanal ürün kimliği) boş."));
                }
            }
            if (sellerId is not null)
            {
                sellerIds.Add(sellerId);
            }
            if (errors.Count > rowErrors)
            {
                continue;
            }

            items.Add(new ChannelCatalogItem(r, productCode, barcode, stockCode, modelCode, color, size, name, brand, category, status, format.IsPassive(status)));
        }

        // Dosya içi tekrar: barkod ve kanal ürün kodu tekil olmalı (tüm tekrar satırları hatalı)
        var invalid = new HashSet<int>();
        AddDuplicates(items, i => i.Barcode, format.Barcode, errors, invalid);
        AddDuplicates(items, i => i.ChannelProductCode, format.ChannelProductCode ?? "", errors, invalid);
        items.RemoveAll(i => invalid.Contains(i.Row));

        var warnings = new List<string>();
        if (numericCodeCells > 0)
        {
            warnings.Add($"{numericCodeCells} kod hücresi sayı olarak kaydedilmiş; baştaki sıfırlar kaybolmuş olabilir. Mümkünse dosyayı kanal panelinden yeniden indirin.");
        }
        if (sellerIds.Count > 1)
        {
            warnings.Add($"Dosyada {sellerIds.Count} farklı satıcı kimliği (Partner ID) var; tek satıcı hesabının dosyası beklenir.");
        }

        return new ChannelFileReadResult(format, total, items,
            errors.OrderBy(e => e.Row).ThenBy(e => e.Column, StringComparer.Ordinal).ToList(), [], warnings);
    }

    private static void AddDuplicates(List<ChannelCatalogItem> items, Func<ChannelCatalogItem, string?> key, string column, List<ImportRowError> errors, HashSet<int> invalid)
    {
        foreach (var group in items.Where(i => key(i) is not null).GroupBy(i => key(i)!, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            var rows = string.Join(", ", group.Select(i => i.Row));
            foreach (var item in group)
            {
                errors.Add(new(item.Row, column, $"'{group.Key}' dosyada birden fazla satırda var (satırlar: {rows})."));
                invalid.Add(item.Row);
            }
        }
    }

    private static ChannelFileReadResult Fail(string message, ChannelFileFormat? format = null) =>
        new(format, 0, [], [], [message], []);
}
