using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClosedXML.Excel;
using Dapper;
using PazaryeriHub.Application.Excel;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Excel;

/// <summary>Aşama 2 kabul kriteri: 1.000 satırlık Excel içe aktarılır, hatalı satırlar satır numarasıyla raporlanır.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class ExcelImportApiTests(TestDatabase db)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly string[] ProductHeaders = ["SKU Kodu", "Ürün Adı", "Marka", "Kategori", "Barkod", "Renk", "Beden"];

    [Fact]
    public async Task Bin_satirlik_urun_dosyasi_ice_aktarilir_hatali_satirlar_satir_numarasiyla_raporlanir()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var prefix = "XL" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        // Veritabanında başka bir SKU'ya ait barkod (600. satırda kullanılacak)
        var takenBarcode = prefix + "-TAKEN";
        await db.CreateSkuAsync(takenBarcode);

        var rows = Enumerable.Range(0, 1000).Select(i => new object?[]
        {
            $"{prefix}-{i:D4}", $"Ürün {prefix} {i / 5}", "TestMarka", "Tişört", $"{prefix}-B{i:D4}", "Beyaz", (i % 5) switch { 0 => "XS", 1 => "S", 2 => "M", 3 => "L", _ => "XL" }
        }).ToArray();

        // Excel satır numarası = dizi indeksi + 2 (1. satır başlık)
        void Set(int excelRow, int col, object? value) => rows[excelRow - 2][col] = value;
        Set(10, 0, null);                                // SKU kodu boş
        Set(50, 1, null);                                // ürün adı boş
        Set(100, 0, new string('S', 101));               // SKU kodu 100 karakterden uzun
        Set(300, 0, rows[200 - 2][0]);                   // 200 ve 300: aynı SKU kodu
        Set(500, 4, rows[400 - 2][4]);                   // 400 ve 500: aynı barkod
        Set(600, 4, takenBarcode);                       // barkod veritabanında başka SKU'da
        Set(700, 5, new string('R', 51));                // renk 50 karakterden uzun
        int[] expectedErrorRows = [10, 50, 100, 200, 300, 400, 500, 600, 700];

        var file = BuildWorkbook(ProductHeaders, rows);

        var sw = Stopwatch.StartNew();
        var report = await ImportAsync("products", file, validateOnly: false, ct);
        sw.Stop();

        report.TotalRows.Should().Be(1000);
        report.FileErrors.Should().BeEmpty();
        report.Errors.Select(e => e.Row).Distinct().Order().Should().Equal(expectedErrorRows);
        report.Errors.Should().Contain(e => e.Row == 600 && e.Message.Contains("başka bir SKU"));
        report.ValidRows.Should().Be(991);
        report.Created.Should().Be(991);
        report.Updated.Should().Be(0);

        await using var connection = db.Open();
        (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Sku WHERE SkuKodu LIKE @p", new { p = prefix + "-%" }))
            .Should().Be(991, "yalnızca geçerli satırlar yazılmalı");
        (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Product WHERE Ad LIKE @p", new { p = $"Ürün {prefix} %" }))
            .Should().Be(200, "aynı Ürün Adı + Marka'ya sahip 5 satır tek ürünün varyantları");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60), "1.000 satır makul sürede işlenmeli");
        TestContext.Current.SendDiagnosticMessage($"1.000 satır içe aktarma süresi: {sw.ElapsedMilliseconds} ms");

        // Aynı dosyayı tekrar yüklemek: yeni kayıt yok, değişiklik yok, aynı hatalar
        var again = await ImportAsync("products", file, validateOnly: false, ct);
        again.Created.Should().Be(0);
        again.Updated.Should().Be(0);
        again.Unchanged.Should().Be(991);
        again.Errors.Select(e => e.Row).Distinct().Order().Should().Equal(expectedErrorRows);

        // Yalnızca doğrulama: bir satırı değiştir → 1 güncelleme tahmini, veritabanı değişmez
        Set(2, 5, "Siyah");
        var dryRun = await ImportAsync("products", BuildWorkbook(ProductHeaders, rows), validateOnly: true, ct);
        dryRun.ValidateOnly.Should().BeTrue();
        dryRun.Updated.Should().Be(1);
        (await connection.ExecuteScalarAsync<string>("SELECT Renk FROM dbo.Sku WHERE SkuKodu = @c", new { c = $"{prefix}-0000" }))
            .Should().Be("Beyaz", "validateOnly hiçbir şey yazmamalı");

        // Test veritabanı her çalıştırmada 1.000 satır büyümesin (bu SKU'ların stok/hareket kaydı yok)
        await connection.ExecuteAsync("""
            DELETE s FROM dbo.Sku s WHERE s.SkuKodu LIKE @p;
            DELETE p FROM dbo.Product p WHERE p.Ad LIKE @n AND NOT EXISTS (SELECT 1 FROM dbo.Sku s WHERE s.ProductId = p.Id);
            """, new { p = prefix + "-%", n = $"Ürün {prefix} %" });
    }

    [Fact]
    public async Task Stok_dosyasi_sayim_ve_giris_uygular_hatalari_raporlar()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var (sku1, code1) = await CreateSkuWithCodeAsync();
        var (sku2, code2) = await CreateSkuWithCodeAsync();
        var (_, code3) = await CreateSkuWithCodeAsync();

        string[] headers = ["SKU Kodu", "Depo", "İşlem", "Adet"];
        var file = BuildWorkbook(headers,
            [code1, null, "SAYIM", 10],             // 2
            [code2, "Ana Depo", "GIRIS", 5],        // 3
            ["YOK-" + Guid.NewGuid(), null, "SAYIM", 1], // 4: SKU yok
            [code3, "Olmayan Depo", "SAYIM", 3]);   // 5: depo yok

        var report = await ImportAsync("stock", file, validateOnly: false, ct);

        report.Errors.Select(e => e.Row).Distinct().Should().Equal(4, 5);
        report.Updated.Should().Be(2);
        (await db.GetBalanceAsync(sku1))!.Fiziksel.Should().Be(10);
        (await db.GetBalanceAsync(sku2))!.Fiziksel.Should().Be(5);

        await using (var connection = db.Open())
        {
            var source = await connection.QuerySingleAsync<(string KaynakTip, string KaynakId)>(
                "SELECT TOP 1 KaynakTip, KaynakId FROM dbo.StockMovement WHERE SkuId = @sku1 ORDER BY Id DESC", new { sku1 });
            source.KaynakTip.Should().Be("EXCEL");
            source.KaynakId.Should().EndWith(":2");
        }

        // Tekrar yükleme: SAYIM aynı değer → değişmedi (hareket yok); GIRIS tekrar ekler (şablonda uyarılıyor)
        var again = await ImportAsync("stock", file, validateOnly: false, ct);
        again.Unchanged.Should().Be(1);
        again.Updated.Should().Be(1);
        (await db.CountMovementsAsync(sku1, "SAYIM")).Should().Be(1);
        (await db.GetBalanceAsync(sku2))!.Fiziksel.Should().Be(10);

        // Rezerveden az sayım reddedilir
        await db.Ledger.ReserveAsync(new(sku1, db.DefaultWarehouseId, 8), ct);
        var belowReserved = await ImportAsync("stock", BuildWorkbook(headers, [code1, null, "SAYIM", 5]), validateOnly: false, ct);
        belowReserved.Errors.Should().ContainSingle().Which.Message.Should().Contain("rezerveden");
        (await db.GetBalanceAsync(sku1))!.Fiziksel.Should().Be(10);
    }

    [Fact]
    public async Task Eslestirme_dosyasi_ekler_gunceller_ve_bilinmeyen_kanali_raporlar()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var (_, code) = await CreateSkuWithCodeAsync();
        string[] headers = ["SKU Kodu", "Kanal", "Kanal Ürün Kodu", "Kanal Barkod", "Durum"];

        var first = await ImportAsync("listings", BuildWorkbook(headers,
            [code, "TRENDYOL", "TY-" + code, null, null],
            [code, "hepsiburada", null, "HB-" + code, "AKTIF"],
            [code, "AMAZON", "AMZ-1", null, null]), validateOnly: false, ct);

        first.Created.Should().Be(2);
        first.Errors.Should().ContainSingle().Which.Should().Match<ImportRowError>(e => e.Row == 4 && e.Column == "Kanal");

        var second = await ImportAsync("listings", BuildWorkbook(headers,
            [code, "TRENDYOL", "TY-" + code, null, "PASIF"],
            [code, "HEPSIBURADA", null, "HB-" + code, "AKTIF"]), validateOnly: false, ct);

        second.Created.Should().Be(0);
        second.Updated.Should().Be(1);
        second.Unchanged.Should().Be(1);

        await using var connection = db.Open();
        (await connection.ExecuteScalarAsync<string>("""
            SELECT l.Durum FROM dbo.ChannelListing l JOIN dbo.Sku s ON s.Id = l.SkuId JOIN dbo.Channel c ON c.Id = l.ChannelId
            WHERE s.SkuKodu = @code AND c.Kod = 'TRENDYOL'
            """, new { code })).Should().Be("PASIF");
    }

    [Theory]
    [InlineData("products", "SKU Kodu", "Ürün Adı", "Marka", "Kategori", "Barkod", "Renk", "Beden", "Model Kodu")]
    [InlineData("stock", "SKU Kodu", "Depo", "İşlem", "Adet")]
    [InlineData("listings", "SKU Kodu", "Kanal", "Kanal Ürün Kodu", "Kanal Barkod", "Durum")]
    public async Task Sablon_indirilir_ve_basliklari_dogru(string kind, params string[] expectedHeaders)
    {
        db.SkipIfUnavailable();
        var response = await db.Api.GetAsync($"/api/excel/{kind}/template", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        using var wb = new XLWorkbook(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        wb.Worksheets.First().Row(1).CellsUsed().Select(c => c.GetString()).Should().Equal(expectedHeaders);
        wb.Worksheets.Contains("Aciklama").Should().BeTrue();
    }

    [Fact]
    public async Task Disa_aktarilan_dosya_yeniden_ice_aktarilabilir()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var (skuId, code) = await CreateSkuWithCodeAsync();
        await db.Ledger.ReceiveAsync(new(skuId, db.DefaultWarehouseId, 7), ct);

        var exported = await db.Api.GetByteArrayAsync("/api/excel/products/export", ct);
        using (var wb = new XLWorkbook(new MemoryStream(exported)))
        {
            var sheet = wb.Worksheets.First();
            var headers = sheet.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
            headers.Should().StartWith(ProductHeaders).And.Contain("Fiziksel");
            var row = sheet.RowsUsed().Single(r => r.Cell(1).GetString() == code);
            row.Cell(headers.IndexOf("Fiziksel") + 1).GetValue<int>().Should().Be(7);
        }

        // Dışa aktarılan dosya değiştirilmeden geri yüklenince hiçbir şey değişmemeli (bilgi sütunları yok sayılır)
        var report = await ImportAsync("products", exported, validateOnly: true, ct);
        report.FileErrors.Should().BeEmpty();
        report.Created.Should().Be(0);
        report.Updated.Should().Be(0);
    }

    [Fact]
    public async Task Gecersiz_dosyalar_reddedilir()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;

        var notXlsx = await PostFileAsync("products", "bu bir excel değil"u8.ToArray(), "urunler.csv", validateOnly: false, ct);
        notXlsx.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var missingColumn = await PostFileAsync("products", BuildWorkbook(["SKU Kodu", "Marka"], ["A", "B"]), "eksik.xlsx", validateOnly: false, ct);
        missingColumn.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var report = await missingColumn.Content.ReadFromJsonAsync<ImportReport>(Json, ct);
        report!.FileErrors.Should().ContainSingle().Which.Should().Contain("Ürün Adı");

        (await db.Api.GetAsync("/api/excel/orders/template", ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ yardımcılar

    private async Task<(int Id, string Code)> CreateSkuWithCodeAsync()
    {
        var id = await db.CreateSkuAsync();
        await using var connection = db.Open();
        return (id, await connection.ExecuteScalarAsync<string>("SELECT SkuKodu FROM dbo.Sku WHERE Id = @id", new { id }) ?? "");
    }

    private async Task<ImportReport> ImportAsync(string kind, byte[] file, bool validateOnly, CancellationToken ct)
    {
        var response = await PostFileAsync(kind, file, "test.xlsx", validateOnly, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: body);
        return JsonSerializer.Deserialize<ImportReport>(body, Json)!;
    }

    private Task<HttpResponseMessage> PostFileAsync(string kind, byte[] file, string fileName, bool validateOnly, CancellationToken ct)
    {
        var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(file);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(part, "file", fileName);
        return db.Api.PostAsync($"/api/excel/{kind}/import?validateOnly={validateOnly.ToString().ToLowerInvariant()}", content, ct);
    }

    internal static byte[] BuildWorkbook(string[] headers, params object?[][] rows)
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
                    int i => i,
                    var o => o.ToString()
                };
            }
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
