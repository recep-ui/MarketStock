using System.Text.Json;
using ClosedXML.Excel;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Excel;
using PazaryeriHub.Infrastructure.Excel;

namespace PazaryeriHub.Tests.Unit.Excel;

/// <summary>
/// Panel listelerinin genel Excel dışa aktarımı (ADR-060): panel ekrandaki filtreyle çektiği satırları gönderir, sunucu ClosedXML ile
/// çalışma kitabı üretir. Değer türleri korunur; metin hiçbir zaman formül olarak yazılmaz; sınırlar aşılırsa doğrulama hatası.
/// </summary>
public sealed class TableExportWriterTests
{
    private static TableExportRequest Request(string json) =>
        JsonSerializer.Deserialize<TableExportRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));

    [Fact]
    public void Basliklar_kalin_ve_satirlar_turuyle_yazilir()
    {
        var bytes = TableExportWriter.Write(Request("""
            {
              "sheetName": "Stok",
              "columns": [
                { "header": "SKU" }, { "header": "Barkod", "kind": "text" }, { "header": "Adet", "kind": "number" },
                { "header": "Fiyat", "kind": "money" }, { "header": "Tarih", "kind": "datetime" }, { "header": "Sapma", "kind": "bool" }
              ],
              "rows": [
                ["A-1", "08690000000017", 12, 149.9, "2026-11-26T09:30:00Z", true],
                ["B-2", null, -3, null, null, false]
              ]
            }
            """));

        using var wb = Open(bytes);
        var sheet = wb.Worksheet(1);
        sheet.Name.Should().Be("Stok");
        sheet.Cell(1, 1).GetString().Should().Be("SKU");
        sheet.Cell(1, 1).Style.Font.Bold.Should().BeTrue();
        sheet.Cell(2, 1).GetString().Should().Be("A-1");
        sheet.Cell(2, 2).GetString().Should().Be("08690000000017", "barkoddaki baştaki sıfır kaybolmaz");
        sheet.Cell(2, 3).GetDouble().Should().Be(12);
        sheet.Cell(2, 4).GetDouble().Should().Be(149.9);
        sheet.Cell(2, 4).Style.NumberFormat.Format.Should().Be("#,##0.00");
        sheet.Cell(2, 5).GetDateTime().Should().Be(new DateTime(2026, 11, 26, 12, 30, 0), "UTC → Türkiye saati (UTC+3)");
        sheet.Cell(2, 6).GetString().Should().Be("Evet");
        sheet.Cell(3, 2).IsEmpty().Should().BeTrue();
        sheet.Cell(3, 3).GetDouble().Should().Be(-3);
        sheet.Cell(3, 6).GetString().Should().Be("Hayır");
        sheet.SheetView.SplitRow.Should().Be(1, "başlık satırı dondurulur");
    }

    [Fact]
    public void Formul_gibi_gorunen_metin_formul_olarak_yazilmaz()
    {
        var bytes = TableExportWriter.Write(Request("""
            { "columns": [{ "header": "Ad" }], "rows": [["=HYPERLINK(\"http://x\")"], ["+1+1"]] }
            """));

        using var wb = Open(bytes);
        var cell = wb.Worksheet(1).Cell(2, 1);
        cell.HasFormula.Should().BeFalse();
        cell.GetString().Should().Be("=HYPERLINK(\"http://x\")");
        wb.Worksheet(1).Cell(3, 1).HasFormula.Should().BeFalse();
    }

    [Fact]
    public void Sayi_sutununa_metin_gelirse_metin_olarak_yazilir()
    {
        var bytes = TableExportWriter.Write(Request("""
            { "columns": [{ "header": "Kanal stoğu", "kind": "number" }], "rows": [["çekilmedi"], [7]] }
            """));

        using var wb = Open(bytes);
        wb.Worksheet(1).Cell(2, 1).GetString().Should().Be("çekilmedi");
        wb.Worksheet(1).Cell(3, 1).GetDouble().Should().Be(7);
    }

    [Theory]
    [InlineData("""{ "columns": [], "rows": [] }""", "columns")]
    [InlineData("""{ "columns": [{ "header": "" }], "rows": [] }""", "columns")]
    [InlineData("""{ "columns": [{ "header": "A", "kind": "resim" }], "rows": [] }""", "columns")]
    [InlineData("""{ "columns": [{ "header": "A" }], "rows": [["1", "2"]] }""", "rows")]
    public void Gecersiz_istek_dogrulama_hatasi_verir(string json, string field)
    {
        var act = () => TableExportWriter.Write(Request(json));
        act.Should().Throw<RequestValidationException>().Which.Errors.Should().ContainKey(field);
    }

    [Fact]
    public void Satir_siniri_asilirsa_dogrulama_hatasi_verir()
    {
        var rows = string.Join(",", Enumerable.Repeat("[1]", TableExportWriter.MaxRows + 1));
        var act = () => TableExportWriter.Write(Request($$"""{ "columns": [{ "header": "A", "kind": "number" }], "rows": [{{rows}}] }"""));
        act.Should().Throw<RequestValidationException>().Which.Errors.Should().ContainKey("rows");
    }

    [Theory]
    [InlineData("Stok Listesi", "Stok Listesi")]
    [InlineData("a/b\\c:d*e?f[g]", "a-b-c-d-e-f-g-")]
    [InlineData("", "Liste")]
    [InlineData(null, "Liste")]
    [InlineData("Çok uzun bir sayfa adı otuz bir karakteri aşar", "Çok uzun bir sayfa adı otuz bir")]
    public void Sayfa_adi_Excel_kurallarina_uyar(string? name, string expected) =>
        TableExportWriter.SheetName(name).Should().Be(expected);

    [Theory]
    [InlineData("stok-listesi", "stok-listesi")]
    [InlineData("../../gizli", "gizli")]
    [InlineData("sipariş listesi", "sipariş-listesi")]
    [InlineData(null, "liste")]
    public void Dosya_adi_guvenli_hale_getirilir(string? name, string expected) =>
        TableExportWriter.FileStem(name).Should().Be(expected);
}
