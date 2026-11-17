using ClosedXML.Excel;
using PazaryeriHub.Infrastructure.ChannelCatalog;

namespace PazaryeriHub.Tests.Unit.ChannelCatalog;

/// <summary>Kanal ürün dosyası okuyucusu: biçim tanıma, kodların metin okunması, bilimsel gösterim ve tekrar kontrolü.</summary>
public sealed class ChannelCatalogFileReaderTests
{
    internal static readonly string[] TrendyolHeaders =
    [
        "Partner ID", "Barkod", "Komisyon Oranı", "Model Kodu", "Ürün Rengi", "Beden", "Marka", "Kategori İsmi",
        "Tedarikçi Stok Kodu", "Ürün Adı", "Ürün Stok Adedi", "Durum"
    ];

    internal static readonly string[] BeymenHeaders =
    [
        "Id", "Satış Kanalı", "Beymen Kategori", "Ürün Adı", "Barkod", "Model Kodu", "Stok Kodu", "Marka", "Beymen Renk",
        "Satıcı Renk", "Beden", "Ürün Stok Adedi", "Durum", "Firma Adı", "E posta adresi", "Adres"
    ];

    /// <summary>Başlık + satırlar; hücre değeri <see cref="XLCellValue"/> veya (değer, sayı biçimi) olabilir.</summary>
    internal static MemoryStream Workbook(string[] headers, params Dictionary<string, object?>[] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Ürünler");
        for (var c = 0; c < headers.Length; c++)
        {
            ws.Cell(1, c + 1).Value = headers[c];
        }
        for (var r = 0; r < rows.Length; r++)
        {
            foreach (var (header, value) in rows[r])
            {
                var cell = ws.Cell(r + 2, Array.IndexOf(headers, header) + 1);
                switch (value)
                {
                    case null: break;
                    case (double d, string fmt): cell.Value = d; cell.Style.NumberFormat.Format = fmt; break;
                    case double d: cell.Value = d; break;
                    case string s: cell.Value = s; break;
                    default: throw new ArgumentException(value.ToString());
                }
            }
        }
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    internal static Dictionary<string, object?> TyRow(string? barcode, string? stock = "STK", string model = "MDL", string color = "Siyah", string size = "M",
        string? status = null, string name = "Basic Tişört") => new()
    {
        ["Partner ID"] = "123456", ["Barkod"] = barcode, ["Model Kodu"] = model, ["Ürün Rengi"] = color, ["Beden"] = size,
        ["Marka"] = "Softcotton", ["Kategori İsmi"] = "Tişört", ["Tedarikçi Stok Kodu"] = stock, ["Ürün Adı"] = name, ["Durum"] = status
    };

    [Fact]
    public void Trendyol_dosyasi_taninir_kodlar_metin_ve_kirpilmis_okunur()
    {
        using var file = Workbook(TrendyolHeaders, TyRow("  SC0001  ", status: "Ürün arşivlendi"), TyRow("SC0002"));
        var result = ChannelCatalogFileReader.Read(file, "TRENDYOL");

        result.FileErrors.Should().BeEmpty();
        result.Format!.Code.Should().Be("TRENDYOL_URUNLER");
        result.TotalRows.Should().Be(2);
        result.Items.Should().HaveCount(2);
        result.Items[0].Barcode.Should().Be("SC0001");
        result.Items[0].Passive.Should().BeTrue("'Ürün arşivlendi' PASIF sayılır");
        result.Items[0].ChannelProductCode.Should().BeNull("Trendyol dosyasında kanal ürün kimliği barkoddur");
        result.Items[1].Passive.Should().BeFalse();
    }

    [Fact]
    public void Bilimsel_gosterim_hatali_satir_sayilir_sayi_olarak_kayitli_kod_uyari_verir()
    {
        var sciText = TyRow("8,69E+12");
        var sciNumber = TyRow(null);
        sciNumber["Barkod"] = (8690000000017d, "0.00E+00");
        var plainNumber = TyRow(null);
        plainNumber["Barkod"] = (8690000000024d, "0");
        using var file = Workbook(TrendyolHeaders, sciText, sciNumber, plainNumber);

        var result = ChannelCatalogFileReader.Read(file, "TRENDYOL");

        result.Errors.Select(e => e.Row).Should().Equal(2, 3);
        result.Errors.Should().AllSatisfy(e => e.Message.Should().Contain("bilimsel"));
        result.Items.Single().Barcode.Should().Be("8690000000024");
        result.Warnings.Should().ContainSingle(w => w.Contains("sayı olarak kaydedilmiş"));
    }

    [Fact]
    public void Bos_ve_tekrarlanan_barkod_hatali_satirdir()
    {
        using var file = Workbook(TrendyolHeaders, TyRow("B1"), TyRow(null), TyRow("B1"), TyRow("B2"));
        var result = ChannelCatalogFileReader.Read(file, "TRENDYOL");

        result.Errors.Select(e => e.Row).Distinct().Should().Equal(2, 3, 4);
        result.Errors.Single(e => e.Row == 3).Message.Should().Contain("Barkod boş");
        result.Errors.Single(e => e.Row == 2).Message.Should().Contain("satırlar: 2, 4");
        result.Items.Select(i => i.Barcode).Should().Equal("B2");
    }

    [Fact]
    public void Beymen_dosyasinda_Id_kanal_urun_kodudur_firma_bilgisi_okunmaz()
    {
        var row = new Dictionary<string, object?>
        {
            ["Id"] = "3f2c7a1e-0000-4000-8000-000000000001", ["Satış Kanalı"] = "Beymen", ["Barkod"] = "BY1", ["Model Kodu"] = " MDL ",
            ["Satıcı Renk"] = "Lacivert", ["Beymen Renk"] = "Mavi", ["Beden"] = "L", ["Ürün Adı"] = "Gömlek", ["Durum"] = "Beymen'e Seçilmedi",
            ["Firma Adı"] = "Örnek Firma", ["E posta adresi"] = "ornek@example.com", ["Adres"] = "Örnek adres"
        };
        using var file = Workbook(BeymenHeaders, row);
        var result = ChannelCatalogFileReader.Read(file, "BEYMEN");

        var item = result.Items.Single();
        item.ChannelProductCode.Should().Be("3f2c7a1e-0000-4000-8000-000000000001");
        item.ModelCode.Should().Be("MDL");
        item.Color.Should().Be("Lacivert", "Beymen'de satıcı rengi kullanılır");
        item.Passive.Should().BeTrue();
        item.ToString().Should().NotContain("example.com").And.NotContain("Örnek Firma");
    }

    [Fact]
    public void Secilen_kanalla_uyusmayan_veya_taninmayan_dosya_reddedilir()
    {
        using (var beymen = Workbook(BeymenHeaders))
        {
            ChannelCatalogFileReader.Read(beymen, "TRENDYOL").FileErrors.Single().Should().Contain("uyuşmuyor");
        }
        using var unknown = Workbook(["SKU Kodu", "Ürün Adı"]);
        ChannelCatalogFileReader.Read(unknown, "TRENDYOL").FileErrors.Single().Should().Contain("tanınmadı");
    }
}
