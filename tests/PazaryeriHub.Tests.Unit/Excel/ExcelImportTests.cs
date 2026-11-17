using ClosedXML.Excel;
using PazaryeriHub.Application.Excel;
using PazaryeriHub.Infrastructure.Excel;

namespace PazaryeriHub.Tests.Unit.Excel;

/// <summary>Excel okuma ve satır doğrulama (veritabanısız).</summary>
public sealed class ExcelImportTests
{
    private static MemoryStream Workbook(string[] headers, params object?[][] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Veri");
        for (var c = 0; c < headers.Length; c++)
        {
            ws.Cell(1, c + 1).Value = headers[c];
        }
        for (var r = 0; r < rows.Length; r++)
        {
            for (var c = 0; c < rows[r].Length; c++)
            {
                ws.Cell(r + 2, c + 1).Value = rows[r][c] switch
                {
                    null => Blank.Value,
                    double d => d,
                    int i => i,
                    var o => o.ToString()
                };
            }
        }
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    private static readonly string[] ProductHeaders = ["SKU Kodu", "Ürün Adı", "Marka", "Kategori", "Barkod", "Renk", "Beden"];

    [Theory]
    [InlineData("Ürün Adı", "URUNADI")]
    [InlineData("  ürün adı * ", "URUNADI")]
    [InlineData("URUN_ADI", "URUNADI")]
    [InlineData("İşlem", "ISLEM")]
    [InlineData("giriş", "GIRIS")]
    [InlineData("Güvenlik Stoğu", "GUVENLIKSTOGU")]
    public void Baslik_anahtari_turkce_ve_bicim_duyarsiz(string header, string expected) =>
        ExcelText.ToKey(header).Should().Be(expected);

    [Fact]
    public void Sutun_sirasi_serbest_bilinmeyen_sutun_yok_sayilir_bos_satir_atlanir()
    {
        using var file = Workbook(["Barkod", "Not", "Ürün Adı", "SKU Kodu"],
            ["123", "x", "Tişört", "A-1"],
            [null, null, null, null],
            [null, "sadece not", null, null],
            [null, null, "Gömlek", "B-1"]);

        var result = ExcelWorkbook.Read(file, ExcelLayouts.Products);

        result.FileErrors.Should().BeEmpty();
        result.Rows.Select(r => r.Row).Should().Equal(2, 5); // satır 3 ve 4 (yalnızca bilinmeyen sütun dolu) atlanır
        result.Rows[0][ExcelLayouts.Products[0]].Should().Be("A-1");
        result.Rows[0][ExcelLayouts.Products[4]].Should().Be("123");
    }

    [Fact]
    public void Sayisal_barkod_bilimsel_gosterime_dusmez()
    {
        using var file = Workbook(ProductHeaders, ["A-1", "Tişört", null, null, 8690000000017d, null, null]);

        var row = ExcelWorkbook.Read(file, ExcelLayouts.Products).Rows.Single();

        row[ExcelLayouts.Products[4]].Should().Be("8690000000017");
    }

    [Fact]
    public void Zorunlu_sutun_yoksa_dosya_hatasi()
    {
        using var file = Workbook(["SKU Kodu", "Marka"], ["A-1", "X"]);

        var result = ExcelWorkbook.Read(file, ExcelLayouts.Products);

        result.Rows.Should().BeEmpty();
        result.FileErrors.Should().ContainSingle().Which.Should().Contain("Ürün Adı");
    }

    [Fact]
    public void Gecersiz_dosya_hatasi_doner()
    {
        using var notExcel = new MemoryStream("bu bir excel değil"u8.ToArray());

        ExcelWorkbook.Read(notExcel, ExcelLayouts.Products).FileErrors.Should().ContainSingle().Which.Should().Contain(".xlsx");
    }

    [Fact]
    public void Urun_dogrulama_satir_numarasiyla_hata_verir()
    {
        using var file = Workbook(ProductHeaders,
            ["A-1", "Tişört", null, null, "111", null, null],        // 2: geçerli
            [null, "Ürünsüz SKU", null, null, null, null, null],     // 3: SKU boş
            ["A-3", null, null, null, null, null, null],             // 4: ad boş
            [new string('X', 101), "Uzun", null, null, null, null, null], // 5: SKU çok uzun
            ["A-5", "Tişört", null, null, null, null, null],         // 6: SKU tekrar (8 ile)
            ["A-6", "Tişört", null, null, "111", null, null],        // 7: barkod tekrar (2 ile)
            ["A-5", "Tişört", null, null, null, null, null],         // 8: SKU tekrar
            ["A-8", "Tişört", null, null, null, null, "M"]);         // 9: geçerli

        var rows = ExcelWorkbook.Read(file, ExcelLayouts.Products).Rows;
        var outcome = ImportValidators.ValidateProducts(rows);

        outcome.Valid.Select(r => r.Row).Should().Equal(9);
        outcome.Errors.Select(e => e.Row).Distinct().Should().Equal(2, 3, 4, 5, 6, 7, 8);
        outcome.Errors.Should().Contain(e => e.Row == 3 && e.Column == "SKU Kodu" && e.Message.Contains("Zorunlu"));
        outcome.Errors.Should().Contain(e => e.Row == 5 && e.Message.Contains("100"));
        outcome.Errors.Should().Contain(e => e.Row == 6 && e.Message.Contains("6, 8"));
        outcome.Errors.Should().Contain(e => e.Row == 2 && e.Column == "Barkod" && e.Message.Contains("2, 7"));
    }

    [Fact]
    public void Sku_kodu_buyuk_kucuk_harf_duyarli_tekrar_sayilmaz()
    {
        using var file = Workbook(ProductHeaders, ["abc", "X", null, null, null, null, null], ["ABC", "X", null, null, null, null, null]);

        ImportValidators.ValidateProducts(ExcelWorkbook.Read(file, ExcelLayouts.Products).Rows).Valid.Should().HaveCount(2);
    }

    [Fact]
    public void Stok_dogrulama_islem_ve_adet_kurallari()
    {
        using var file = Workbook(["SKU Kodu", "Depo", "İşlem", "Adet"],
            ["A", null, "giriş", 5],        // 2: geçerli (Türkçe yazım)
            ["B", null, "SAYIM", 0],        // 3: geçerli (sayım 0 olabilir)
            ["C", null, "GIRIS", 0],        // 4: giriş 0 olamaz
            ["D", null, "TRANSFER", 1],     // 5: geçersiz işlem
            ["E", null, "SAYIM", "beş"],    // 6: sayı değil
            ["F", null, "SAYIM", -1],       // 7: negatif
            ["G", "Ana Depo", "SAYIM", 1],  // 8: tekrar (9 ile; depo boş = aynı değil → farklı anahtar)
            ["G", "ana depo", "GIRIS", 1]); // 9: aynı SKU + depo (depo adı harf duyarsız)

        var outcome = ImportValidators.ValidateStock(ExcelWorkbook.Read(file, ExcelLayouts.Stock).Rows);

        outcome.Valid.Select(r => (r.Row, r.Operation, r.Quantity)).Should().Equal(
            (2, StockImportOperation.Receive, 5), (3, StockImportOperation.Count, 0));
        outcome.Errors.Select(e => e.Row).Distinct().Should().Equal(4, 5, 6, 7, 8, 9);
    }

    [Fact]
    public void Eslestirme_dogrulama_kod_ve_durum_kurallari()
    {
        using var file = Workbook(["SKU Kodu", "Kanal", "Kanal Ürün Kodu", "Kanal Barkod", "Durum"],
            ["A", "trendyol", "TY-1", null, null],   // 2: geçerli, durum varsayılan AKTIF
            ["B", "TRENDYOL", null, null, null],     // 3: kod/barkod yok
            ["C", "TRENDYOL", "TY-3", null, "SİLİNDİ"], // 4: geçersiz durum
            ["A", "TRENDYOL", "TY-9", null, "pasif"],   // 5: A'nın ikinci Trendyol ilanı (ADR-023: geçerli)
            ["D", "TRENDYOL", "TY-9", null, null]);     // 6: aynı kanal + kanal kodu tekrar (5 ile)

        var outcome = ImportValidators.ValidateListings(ExcelWorkbook.Read(file, ExcelLayouts.Listings).Rows);

        outcome.Valid.Select(r => r.Row).Should().Equal(2);
        outcome.Errors.Select(e => e.Row).Distinct().Should().Equal(3, 4, 5, 6);
    }

    [Fact]
    public void Sablon_basliklari_okuyucu_ile_uyumlu()
    {
        foreach (var kind in Enum.GetValues<ExcelKind>())
        {
            var bytes = ExcelWorkbook.Write(ExcelLayouts.SheetName(kind), ExcelLayouts.For(kind), ["Bilgi"], []);
            using var stream = new MemoryStream(bytes);

            var result = ExcelWorkbook.Read(stream, ExcelLayouts.For(kind));

            result.FileErrors.Should().BeEmpty(because: kind.ToString());
            result.Rows.Should().BeEmpty();
        }
    }
}
