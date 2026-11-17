using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Dapper;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.ChannelCatalog;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Stock;
using PazaryeriHub.Tests.Integration.Excel;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.ChannelCatalog;

/// <summary>
/// Kanal kataloğu içe alma (ADR-020, ADR-023): Trendyol biçimli sentetik dosya ile önizleme, uygulama, tekrar yükleme (idempotent),
/// aynı SKU'nun ikinci ilanı, çoklanan ilanların tek SKU'da toplanması, eşleşmeyenler, elle bağlama ve stoğu olmayan SKU'nun
/// kanala gönderilmemesi.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ChannelCatalogImportTests(TestDatabase db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Headers =
        ["Partner ID", "Barkod", "Model Kodu", "Ürün Rengi", "Beden", "Marka", "Kategori İsmi", "Tedarikçi Stok Kodu", "Ürün Adı", "Durum"];

    [Fact]
    public async Task Trendyol_dosyasi_onizlenir_uygulanir_ve_tekrar_yuklemede_degismez()
    {
        db.SkipIfUnavailable();
        var p = "KC" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var trendyol = await ChannelIdAsync("TRENDYOL");
        // Çalıştırmaya özel marka: önceki çalıştırmaların ürünleri ad benzerliği önerisine girmesin
        var brand = p + "-MARKA";

        // Mevcut katalog
        var skuA = await SkuAsync(p, $"{p}-A", barcode: $"{p}-B1");                                  // barkodla eşleşecek (stoğu var)
        var skuB = await SkuAsync(p, $"{p}-STK2");                                                   // stok koduyla eşleşecek
        var skuC = await SkuAsync(p, $"{p}-C", model: $"{p}-MDL", color: "Siyah", size: "L");        // model+renk+beden
        var skuD = await SkuAsync(p, $"{p}-D", barcode: $"{p}-B4");                                  // bu kanalda başka barkoda bağlı → ikinci ilan
        var skuE = await SkuAsync(p, $"{p}-E", name: $"{p} Keten Gömlek", brand: brand, size: "M"); // ad benzerliği önerisi
        await db.Ledger.ReceiveAsync(new StockChangeRequest(skuA, db.DefaultWarehouseId, 5, "TEST", p, "test"), Ct);
        await using (var c = db.Open())
        {
            await c.ExecuteAsync("INSERT dbo.ChannelListing (SkuId, ChannelId, KanalBarkod) VALUES (@skuD, @trendyol, @old)",
                new { skuD, trendyol, old = $"{p}-OLD" });
        }

        var file = Workbook(brand,
            Row($"{p}-B1"),                                                                       // 2: barkod
            Row($"{p}-X2", stock: $"{p}-STK2"),                                                   // 3: stok kodu
            Row($"{p}-X3", model: $"{p}-MDL", color: "SİYAH", size: "l"),                         // 4: model+renk+beden
            Row($"{p}-B4"),                                                                       // 5: D'nin ikinci ilanı (ADR-023)
            Row($"{p}-X5", name: $"{p} Keten Gömlek", size: "M"),                                 // 6: öneri (otomatik bağlanmaz/oluşturulmaz)
            Row($"{p}-X6", stock: $"{p}-DUP", model: $"{p}-NEW", color: "Mavi", size: "S", status: "Ürün arşivlendi",
                name: $"{p} Zeytin Sweatshirt"),                                                  // 7: yeni SKU (PASIF)
            Row($"{p}-X7", stock: $"{p}-DUP", model: $"{p}-NEW", color: "Mavi", size: "M", name: $"{p} Zeytin Sweatshirt"), // 8: yeni SKU
            Row("8,69E+12"),                                                                      // 9: bilimsel gösterim → hatalı
            Row($"{p}-X10", model: $"{p}-CIFT", color: "Gri", size: "XL", name: $"{p} Kadife Bornoz"),   // 10-11: çoklanan ilan →
            Row($"{p}-X11", model: $"{p}-CIFT", color: "gri", size: "XL", name: $"{p} Kadife Bornoz")); //        tek SKU, iki ilan

        // 1) Önizleme: hiçbir şey yazılmaz
        var preview = await ImportAsync(trendyol, file, validateOnly: true, createMissing: true);
        preview.Format.Should().Be("TRENDYOL_URUNLER");
        preview.TotalRows.Should().Be(10);
        preview.Errors.Select(e => e.Row).Should().Equal(9);
        (preview.MatchedByBarcode, preview.MatchedByStockCode, preview.MatchedByModelColorSize).Should().Be((2, 1, 1));
        preview.Conflicts.Should().Be(0, "aynı SKU'nun farklı barkodu ikinci ilandır (ADR-023)");
        preview.SkusCreated.Should().Be(3, "NEW-S, NEW-M ve çoklanan CIFT grubu için tek SKU");
        preview.ProductsCreated.Should().Be(2, "model kodu + marka başına tek ürün");
        preview.ListingsCreated.Should().Be(8);
        preview.Unmatched.Should().Be(1);
        preview.Ambiguous.Should().Be(0);
        preview.Rows.Single(r => r.Row == 11).Detail.Should().Contain("ek ilanı");
        preview.Rows.Single(r => r.Row == 11).SkuCode.Should().Be(preview.Rows.Single(r => r.Row == 10).SkuCode);
        preview.WithSuggestion.Should().Be(1);
        preview.Rows.Single(r => r.Row == 6).SuggestedSkuCode.Should().Be($"{p}-E");
        (await CountAsync("SELECT COUNT(*) FROM dbo.Sku WHERE SkuKodu LIKE @p", p)).Should().Be(5, "önizleme yazmaz");
        (await CountAsync("SELECT COUNT(*) FROM dbo.UnmatchedChannelItem WHERE KanalAnahtar LIKE @p", p)).Should().Be(0);

        // 2) Uygula
        var applied = await ImportAsync(trendyol, file, validateOnly: false, createMissing: true);
        applied.ListingsCreated.Should().Be(8);
        applied.PushDeferred.Should().Be(7, "yalnızca stoğu girilmiş SKU (A) kanala gönderilir");

        await using (var c = db.Open())
        {
            var listings = (await c.QueryAsync<(string SkuKodu, string? KanalBarkod, string Durum, int Id)>("""
                SELECT s.SkuKodu, l.KanalBarkod, l.Durum, l.Id FROM dbo.ChannelListing l JOIN dbo.Sku s ON s.Id = l.SkuId
                WHERE l.ChannelId = @trendyol AND s.SkuKodu LIKE @p
                """, new { trendyol, p = p + "%" })).ToList();
            listings.Should().HaveCount(9);
            listings.Where(l => l.SkuKodu == $"{p}-D").Select(l => l.KanalBarkod).Should().BeEquivalentTo([$"{p}-OLD", $"{p}-B4"],
                "mevcut ilanın üzerine yazılmaz; yeni barkod ikinci ilan olur");
            listings.Where(l => l.KanalBarkod == $"{p}-X10" || l.KanalBarkod == $"{p}-X11").Select(l => l.SkuKodu).Distinct().Should().ContainSingle();
            listings.Single(l => l.KanalBarkod == $"{p}-X6").Durum.Should().Be("PASIF");
            listings.Single(l => l.KanalBarkod == $"{p}-X3").SkuKodu.Should().Be($"{p}-C");

            var newSkus = (await c.QueryAsync<(string SkuKodu, string? Barkod, string? ModelKodu)>("""
                SELECT s.SkuKodu, s.Barkod, pr.ModelKodu FROM dbo.Sku s JOIN dbo.Product pr ON pr.Id = s.ProductId WHERE s.Barkod IN (@x6, @x7)
                """, new { x6 = $"{p}-X6", x7 = $"{p}-X7" })).ToList();
            newSkus.Should().HaveCount(2).And.OnlyContain(s => s.ModelKodu == $"{p}-NEW");
            newSkus.Select(s => s.SkuKodu).Should().OnlyContain(code => code.EndsWith("NEW-MAVI-S") || code.EndsWith("NEW-MAVI-M"),
                "stok kodu dosyada tekrar ettiği için MODEL-RENK-BEDEN kullanılır");

            var pushed = await c.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM dbo.Outbox WHERE OlayTipi = 'EslestirmeDegisti'
                AND CAST(JSON_VALUE(Payload, '$.ListingId') AS INT) IN (SELECT Id FROM OPENJSON(@ids) WITH (Id INT '$'))
                """, new { ids = System.Text.Json.JsonSerializer.Serialize(listings.Select(l => l.Id)) });
            pushed.Should().Be(1);

            var unmatched = (await c.QueryAsync<(string KanalAnahtar, string Neden, string Durum, int? OnerilenSkuId)>(
                "SELECT KanalAnahtar, Neden, Durum, OnerilenSkuId FROM dbo.UnmatchedChannelItem WHERE KanalAnahtar LIKE @p", new { p = p + "%" })).ToList();
            unmatched.Should().BeEquivalentTo(new[] { ($"{p}-X5", "ESLESME_YOK", "BEKLIYOR", (int?)skuE) });
        }

        // 3) Aynı dosya tekrar: yeni kayıt yok
        var again = await ImportAsync(trendyol, file, validateOnly: false, createMissing: true);
        again.ListingsCreated.Should().Be(0);
        again.SkusCreated.Should().Be(0);
        again.ListingsUnchanged.Should().Be(8);
        again.MatchedExisting.Should().Be(8);
        (await CountAsync("SELECT COUNT(*) FROM dbo.UnmatchedChannelItem WHERE KanalAnahtar LIKE @p", p)).Should().Be(1);

        // 4) Eşleşmeyenler: listele, öneriyi elle bağla
        var page = await db.Api.GetFromJsonAsync<PagedResult<UnmatchedItemDto>>($"/api/channel-catalog/unmatched?channelId={trendyol}&search={p}-X5",
            ExcelImportApiTests.Json, Ct);
        var suggested = page!.Items.Single();
        suggested.SuggestedSkuCode.Should().Be($"{p}-E");

        var link = await db.Api.PostAsJsonAsync($"/api/channel-catalog/unmatched/{suggested.Id}/link", new { skuId = skuE }, Ct);
        link.StatusCode.Should().Be(HttpStatusCode.OK);
        (await link.Content.ReadFromJsonAsync<ListingDto>(ExcelImportApiTests.Json, Ct))!.ChannelBarcode.Should().Be($"{p}-X5");
        (await db.Api.PostAsJsonAsync($"/api/channel-catalog/unmatched/{suggested.Id}/link", new { skuId = skuE }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        await using (var c = db.Open())
        {
            (await c.QueryAsync<(string KanalAnahtar, string Durum)>("SELECT KanalAnahtar, Durum FROM dbo.UnmatchedChannelItem WHERE KanalAnahtar LIKE @p",
                new { p = p + "%" })).Should().BeEquivalentTo(new[] { ($"{p}-X5", "BAGLANDI") });
        }
    }

    [Fact]
    public async Task Ice_alma_yalnizca_yoneticiye_acik_ve_kanal_uyusmazligi_reddedilir()
    {
        db.SkipIfUnavailable();
        var trendyol = await ChannelIdAsync("TRENDYOL");
        var beymen = await ChannelIdAsync("BEYMEN");
        var file = Workbook("Softcotton", Row("KC-YETKI-1"));

        using var depot = await db.ApiHost.CreateClientAsAsync(Roles.Warehouse);
        (await PostAsync(depot, trendyol, file, true, false)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var wrong = await PostAsync(db.Api, beymen, file, true, false);
        wrong.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await wrong.Content.ReadFromJsonAsync<CatalogImportReport>(ExcelImportApiTests.Json, Ct))!.FileErrors.Single().Should().Contain("uyuşmuyor");
    }

    // ------------------------------------------------------------------ yardımcılar

    private static Dictionary<string, string?> Row(string barcode, string? stock = null, string? model = null, string? color = null, string? size = null,
        string? status = null, string? name = null) => new()
    {
        ["Partner ID"] = "100001", ["Barkod"] = barcode, ["Model Kodu"] = model, ["Ürün Rengi"] = color, ["Beden"] = size,
        ["Marka"] = "Softcotton", ["Kategori İsmi"] = "Test", ["Tedarikçi Stok Kodu"] = stock, ["Ürün Adı"] = name, ["Durum"] = status
    };

    private static byte[] Workbook(string brand, params Dictionary<string, string?>[] rows)
    {
        foreach (var row in rows)
        {
            row["Marka"] = brand;
        }
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Ürünler");
        for (var c = 0; c < Headers.Length; c++)
        {
            ws.Cell(1, c + 1).Value = Headers[c];
        }
        for (var r = 0; r < rows.Length; r++)
        {
            foreach (var (header, value) in rows[r])
            {
                if (value is not null)
                {
                    ws.Cell(r + 2, Array.IndexOf(Headers, header) + 1).Value = value;
                }
            }
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private async Task<CatalogImportReport> ImportAsync(int channelId, byte[] file, bool validateOnly, bool createMissing)
    {
        var response = await PostAsync(db.Api, channelId, file, validateOnly, createMissing);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CatalogImportReport>(ExcelImportApiTests.Json, Ct))!;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, int channelId, byte[] file, bool validateOnly, bool createMissing)
    {
        var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(file);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(part, "file", "trendyol-urunler.xlsx");
        return client.PostAsync(
            $"/api/channels/{channelId}/catalog/import?validateOnly={validateOnly.ToString().ToLowerInvariant()}&createMissing={createMissing.ToString().ToLowerInvariant()}",
            content, Ct);
    }

    private async Task<int> ChannelIdAsync(string code)
    {
        await using var c = db.Open();
        return await c.ExecuteScalarAsync<int>("SELECT Id FROM dbo.Channel WHERE Kod = @code", new { code });
    }

    private async Task<int> CountAsync(string sql, string prefix)
    {
        await using var c = db.Open();
        return await c.ExecuteScalarAsync<int>(sql, new { p = prefix + "%" });
    }

    private async Task<int> SkuAsync(string prefix, string skuCode, string? barcode = null, string? model = null, string? color = null, string? size = null,
        string? name = null, string brand = "TestMarka")
    {
        await using var c = db.Open();
        return await c.ExecuteScalarAsync<int>("""
            DECLARE @pid INT;
            INSERT dbo.Product (Ad, Marka, ModelKodu) VALUES (@name, @brand, @model);
            SET @pid = SCOPE_IDENTITY();
            INSERT dbo.Sku (ProductId, SkuKodu, Barkod, Renk, Beden) VALUES (@pid, @skuCode, @barcode, @color, @size);
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """, new { name = name ?? $"{prefix} ürün {skuCode}", brand, model, skuCode, barcode, color, size });
    }
}
