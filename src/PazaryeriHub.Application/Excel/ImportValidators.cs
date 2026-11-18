using System.Globalization;
using PazaryeriHub.Application.Channels;

namespace PazaryeriHub.Application.Excel;

public sealed record ProductRow(int Row, string SkuCode, string Name, string? Brand, string? Category, string? Barcode, string? Color, string? Size, string? ModelCode = null);

public sealed record StockRow(int Row, string SkuCode, string? WarehouseName, StockImportOperation Operation, int Quantity);

public sealed record ListingRow(int Row, string SkuCode, string ChannelCode, string? ChannelProductCode, string? ChannelBarcode, string Status);

public sealed record PriceRow(int Row, string SkuCode, decimal SalePrice, decimal? ListPrice);

public enum StockImportOperation
{
    /// <summary>Fiziksel += Adet.</summary>
    Receive,

    /// <summary>Fiziksel = Adet.</summary>
    Count
}

public sealed record ValidationOutcome<T>(IReadOnlyList<T> Valid, IReadOnlyList<ImportRowError> Errors);

/// <summary>
/// Excel satırlarının veritabanından bağımsız (saf) doğrulaması: zorunlu alanlar, uzunluklar, sayı biçimi, dosya içi tekrarlar.
/// Veritabanına bağlı kontroller (SKU var mı, barkod başka SKU'da mı...) uygulama sırasında yapılır.
/// Bir satırda birden fazla hata olabilir; hatalı satır uygulanmaz.
/// </summary>
public static class ImportValidators
{
    public const int MaxQuantity = 1_000_000;

    public static ValidationOutcome<ProductRow> ValidateProducts(IReadOnlyList<RawRow> rows)
    {
        var errors = new List<ImportRowError>();
        var candidates = new List<ProductRow>();
        var cols = ExcelLayouts.Products;

        foreach (var raw in rows)
        {
            if (!CheckColumns(raw, cols, errors))
            {
                continue;
            }
            candidates.Add(new ProductRow(raw.Row, raw[cols[0]]!, raw[cols[1]]!, raw[cols[2]], raw[cols[3]], raw[cols[4]], raw[cols[5]], raw[cols[6]], raw[cols[7]]));
        }

        var invalid = new HashSet<int>(errors.Select(e => e.Row));
        AddDuplicateErrors(candidates, r => r.SkuCode, r => r.Row, StringComparer.Ordinal, "SKU Kodu",
            rowsText => $"SKU kodu dosyada birden fazla satırda var (satırlar: {rowsText}).", errors, invalid);
        AddDuplicateErrors(candidates.Where(r => r.Barcode is not null), r => r.Barcode!, r => r.Row, StringComparer.Ordinal, "Barkod",
            rowsText => $"Barkod dosyada birden fazla satırda var (satırlar: {rowsText}).", errors, invalid);

        return new ValidationOutcome<ProductRow>(candidates.Where(r => !invalid.Contains(r.Row)).ToList(), Sorted(errors));
    }

    public static ValidationOutcome<StockRow> ValidateStock(IReadOnlyList<RawRow> rows)
    {
        var errors = new List<ImportRowError>();
        var candidates = new List<StockRow>();
        var cols = ExcelLayouts.Stock;

        foreach (var raw in rows)
        {
            var ok = CheckColumns(raw, cols, errors);

            StockImportOperation? op = null;
            if (raw[cols[2]] is { } opText)
            {
                op = ExcelText.ToKey(opText) switch
                {
                    "GIRIS" => StockImportOperation.Receive,
                    "SAYIM" => StockImportOperation.Count,
                    _ => null
                };
                if (op is null)
                {
                    errors.Add(new(raw.Row, cols[2].Header, $"'{opText}' geçersiz; GIRIS veya SAYIM olmalı."));
                    ok = false;
                }
            }

            int quantity = 0;
            if (raw[cols[3]] is { } qtyText)
            {
                if (!int.TryParse(qtyText, NumberStyles.Integer, CultureInfo.InvariantCulture, out quantity))
                {
                    errors.Add(new(raw.Row, cols[3].Header, $"'{qtyText}' tam sayı değil."));
                    ok = false;
                }
                else if (quantity < 0 || quantity > MaxQuantity)
                {
                    errors.Add(new(raw.Row, cols[3].Header, $"Adet 0 ile {MaxQuantity:N0} arasında olmalı."));
                    ok = false;
                }
                else if (op == StockImportOperation.Receive && quantity == 0)
                {
                    errors.Add(new(raw.Row, cols[3].Header, "GIRIS için adet sıfırdan büyük olmalı."));
                    ok = false;
                }
            }

            if (ok && op is { } operation)
            {
                candidates.Add(new StockRow(raw.Row, raw[cols[0]]!, raw[cols[1]], operation, quantity));
            }
        }

        var invalid = new HashSet<int>(errors.Select(e => e.Row));
        AddDuplicateErrors(candidates, r => (r.SkuCode, ExcelText.ToKey(r.WarehouseName)), r => r.Row, null, "SKU Kodu",
            rowsText => $"Aynı SKU + depo dosyada birden fazla satırda var (satırlar: {rowsText}); tek satırda birleştirin.", errors, invalid);

        return new ValidationOutcome<StockRow>(candidates.Where(r => !invalid.Contains(r.Row)).ToList(), Sorted(errors));
    }

    public static ValidationOutcome<ListingRow> ValidateListings(IReadOnlyList<RawRow> rows)
    {
        var errors = new List<ImportRowError>();
        var candidates = new List<ListingRow>();
        var cols = ExcelLayouts.Listings;

        foreach (var raw in rows)
        {
            var ok = CheckColumns(raw, cols, errors);

            var status = raw[cols[4]] is { } statusText ? ExcelText.ToKey(statusText) : ListingStatus.Active;
            if (!ListingStatus.All.Contains(status))
            {
                errors.Add(new(raw.Row, cols[4].Header, $"'{raw[cols[4]]}' geçersiz; AKTIF veya PASIF olmalı."));
                ok = false;
            }

            if (raw[cols[2]] is null && raw[cols[3]] is null)
            {
                errors.Add(new(raw.Row, cols[2].Header, "Kanal Ürün Kodu veya Kanal Barkod'dan en az biri girilmeli."));
                ok = false;
            }

            if (ok)
            {
                candidates.Add(new ListingRow(raw.Row, raw[cols[0]]!, ExcelText.ToKey(raw[cols[1]]), raw[cols[2]], raw[cols[3]], status));
            }
        }

        var invalid = new HashSet<int>(errors.Select(e => e.Row));
        // İlan kanal içinde barkod / kanal ürün koduyla tanınır (ADR-023); aynı SKU + kanal için birden fazla satır serbesttir.
        AddDuplicateErrors(candidates.Where(r => r.ChannelBarcode is not null), r => (r.ChannelCode, r.ChannelBarcode!), r => r.Row, null, "Kanal Barkod",
            rowsText => $"Aynı kanal + kanal barkodu dosyada birden fazla satırda var (satırlar: {rowsText}).", errors, invalid);
        AddDuplicateErrors(candidates.Where(r => r.ChannelProductCode is not null), r => (r.ChannelCode, r.ChannelProductCode!), r => r.Row, null, "Kanal Ürün Kodu",
            rowsText => $"Aynı kanal + kanal ürün kodu dosyada birden fazla satırda var (satırlar: {rowsText}).", errors, invalid);

        return new ValidationOutcome<ListingRow>(candidates.Where(r => !invalid.Contains(r.Row)).ToList(), Sorted(errors));
    }

    public const decimal MaxPrice = 10_000_000m;

    /// <summary>Fiyat satırları (ADR-028): sayı biçimi (virgül/nokta), &gt; 0, en fazla 2 ondalık, liste ≥ satış, dosya içi tekrar.</summary>
    public static ValidationOutcome<PriceRow> ValidatePrices(IReadOnlyList<RawRow> rows)
    {
        var errors = new List<ImportRowError>();
        var candidates = new List<PriceRow>();
        var cols = ExcelLayouts.Prices;

        foreach (var raw in rows)
        {
            var ok = CheckColumns(raw, cols, errors);
            var sale = ReadPrice(raw, cols[1], errors, ref ok);
            var list = ReadPrice(raw, cols[2], errors, ref ok);
            if (ok && sale is not null && list is not null && list < sale)
            {
                errors.Add(new(raw.Row, cols[2].Header, $"Liste fiyatı ({list}) satış fiyatından ({sale}) düşük olamaz."));
                ok = false;
            }
            if (ok && sale is { } s)
            {
                candidates.Add(new PriceRow(raw.Row, raw[cols[0]]!, s, list));
            }
        }

        var invalid = new HashSet<int>(errors.Select(e => e.Row));
        AddDuplicateErrors(candidates, r => r.SkuCode, r => r.Row, StringComparer.Ordinal, "SKU Kodu",
            rowsText => $"SKU kodu dosyada birden fazla satırda var (satırlar: {rowsText}).", errors, invalid);
        return new ValidationOutcome<PriceRow>(candidates.Where(r => !invalid.Contains(r.Row)).ToList(), Sorted(errors));
    }

    private static decimal? ReadPrice(RawRow raw, ExcelColumn col, List<ImportRowError> errors, ref bool ok)
    {
        if (raw[col] is not { } text)
        {
            return null;
        }
        if (!TryParsePrice(text, out var value))
        {
            errors.Add(new(raw.Row, col.Header, $"'{text}' geçerli bir fiyat değil."));
            ok = false;
            return null;
        }
        if (value <= 0m || value > MaxPrice)
        {
            errors.Add(new(raw.Row, col.Header, "Fiyat 0'dan büyük olmalı."));
            ok = false;
            return null;
        }
        if (decimal.Round(value, 2) != value)
        {
            errors.Add(new(raw.Row, col.Header, "Fiyat en fazla 2 ondalık olabilir."));
            ok = false;
            return null;
        }
        return value;
    }

    /// <summary>
    /// "499,90", "499.90", "1.234,56", "1,234.56", "₺499,90" kabul edilir. Tek ayırıcı varsa ve ardından tam 3 hane geliyorsa
    /// (ör. "1.234") binlik mi ondalık mı belirsizdir → reddedilir.
    /// </summary>
    public static bool TryParsePrice(string? text, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var s = text.Trim().Replace("₺", "", StringComparison.Ordinal).Replace("TL", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "", StringComparison.Ordinal);
        int lastComma = s.LastIndexOf(','), lastDot = s.LastIndexOf('.');
        if (lastComma >= 0 && lastDot >= 0)
        {
            var decimalSep = lastComma > lastDot ? ',' : '.';
            var groupSep = decimalSep == ',' ? '.' : ',';
            s = s.Replace(groupSep.ToString(), "", StringComparison.Ordinal).Replace(decimalSep, '.');
        }
        else if (lastComma >= 0 || lastDot >= 0)
        {
            var sep = lastComma >= 0 ? ',' : '.';
            if (s.Count(ch => ch == sep) > 1)
            {
                return false;
            }
            var after = s.Length - s.IndexOf(sep, StringComparison.Ordinal) - 1;
            if (after == 3 && sep == ',')
            {
                return false; // "1,234" belirsiz
            }
            s = s.Replace(sep, '.');
        }
        return decimal.TryParse(s, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Zorunlu alan ve uzunluk kontrolü. Satır geçerliyse true.</summary>
    private static bool CheckColumns(RawRow raw, IEnumerable<ExcelColumn> columns, List<ImportRowError> errors)
    {
        var ok = true;
        foreach (var col in columns)
        {
            var value = raw[col];
            if (value is null)
            {
                if (col.Required)
                {
                    errors.Add(new(raw.Row, col.Header, "Zorunlu alan boş."));
                    ok = false;
                }
            }
            else if (col.MaxLength is { } max && value.Length > max)
            {
                errors.Add(new(raw.Row, col.Header, $"En fazla {max} karakter olabilir ({value.Length} karakter)."));
                ok = false;
            }
        }
        return ok;
    }

    /// <summary>Aynı anahtara sahip TÜM satırları hatalı işaretler (hangisinin doğru olduğu belirsiz).</summary>
    private static void AddDuplicateErrors<T, TKey>(
        IEnumerable<T> items, Func<T, TKey> key, Func<T, int> row, IEqualityComparer<TKey>? comparer, string column,
        Func<string, string> message, List<ImportRowError> errors, HashSet<int> invalid) where TKey : notnull
    {
        foreach (var group in items.GroupBy(key, comparer).Where(g => g.Count() > 1))
        {
            var rowNumbers = group.Select(row).Order().ToList();
            var text = string.Join(", ", rowNumbers);
            foreach (var r in rowNumbers)
            {
                errors.Add(new(r, column, message(text)));
                invalid.Add(r);
            }
        }
    }

    private static List<ImportRowError> Sorted(List<ImportRowError> errors) =>
        errors.OrderBy(e => e.Row).ThenBy(e => e.Column, StringComparer.Ordinal).ToList();
}
