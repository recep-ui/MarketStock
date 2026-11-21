using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Panel;
using PazaryeriHub.Tests.Integration.Excel;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Panel;

/// <summary>
/// ADR-035: Stok Listesi'nde kanal bazlı görünüm — kanalın bildirdiği kod/ad/stok ↔ bağlandığı SKU ve yanlış eşleştirme uyarıları.
/// Eşleştirme değişmez; yalnızca okunur.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ChannelStockListTests(TestDatabase db)
{
    private const string Channel = "SOFTCOTTON"; // mock kanal
    private static readonly JsonSerializerOptions Json = ExcelImportApiTests.Json;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique() => Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
    private static string Barcode() => "869" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999);

    private sealed record Setup(string Tag, int ChannelId, int SkuId, string SkuCode, string SkuBarcode, int GoodId, int WrongId, int LegacyId);

    private async Task<Setup> SeedAsync()
    {
        var tag = Unique();
        var skuCode = "KNL-" + tag;
        var skuBarcode = Barcode();
        await using var c = db.Open();
        var channelId = await c.ExecuteScalarAsync<int>("SELECT Id FROM dbo.Channel WHERE Kod = @Channel", new { Channel });
        var skuId = await c.ExecuteScalarAsync<int>("""
            DECLARE @p INT;
            INSERT dbo.Product (Ad, Marka) VALUES (@name, N'Softcotton');
            SET @p = SCOPE_IDENTITY();
            INSERT dbo.Sku (ProductId, SkuKodu, Barkod, Renk, Beden) VALUES (@p, @skuCode, @skuBarcode, N'Beyaz', N'50x90');
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """, new { name = "Bambu Havlu " + tag, skuCode, skuBarcode });
        await db.Ledger.ReceiveAsync(new(skuId, db.DefaultWarehouseId, 10), Ct);

        const string insert = """
            INSERT dbo.ChannelListing (SkuId, ChannelId, KanalUrunKodu, KanalBarkod, Durum, KanalVerisi)
            VALUES (@skuId, @channelId, @code, @barcode, 'AKTIF', @data);
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """;
        // 1) Doğru eşleşme: barkod aynı, ad/renk/beden uyumlu; kanal stoğu defterden farklı (sapma)
        var good = await c.ExecuteScalarAsync<int>(insert, new
        {
            skuId, channelId, code = "SC-OK-" + tag, barcode = skuBarcode,
            data = JsonSerializer.Serialize(new { urunAdi = $"Softcotton Bambu Havlu {tag} Beyaz", renk = "Beyaz", beden = "50x90", stokKodu = skuCode })
        });
        await c.ExecuteAsync("INSERT dbo.ChannelStockSnapshot (ListingId, KanalStok) VALUES (@good, 99999)", new { good });
        // 2) Yanlış eşleşme: başka barkod, başka ürün, başka renk/beden
        var wrong = await c.ExecuteScalarAsync<int>(insert, new
        {
            skuId, channelId, code = "SC-BAD-" + tag, barcode = Barcode(),
            data = JsonSerializer.Serialize(new { urunAdi = "Kadın Saten Gecelik", renk = "Kırmızı", beden = "M" })
        });
        // 3) Eski ilan: kanal verisinde ad yok; ad, bağlanmış eşleşmeyen kaydından okunur
        var legacyBarcode = Barcode();
        var legacy = await c.ExecuteScalarAsync<int>(insert, new { skuId, channelId, code = "SC-OLD-" + tag, barcode = legacyBarcode, data = (string?)null });
        await c.ExecuteAsync("""
            INSERT dbo.UnmatchedChannelItem (ChannelId, Kaynak, KanalAnahtar, KanalBarkod, UrunAdi, Marka, Neden, Durum)
            VALUES (@channelId, 'API', @key, @legacyBarcode, @name, N'Softcotton', 'ESLESME_YOK', 'BAGLANDI');
            """, new { channelId, key = "B:" + legacyBarcode, legacyBarcode, name = "Bambu Havlu Eski Kayıt " + tag });
        return new Setup(tag, channelId, skuId, skuCode, skuBarcode, good, wrong, legacy);
    }

    private async Task<ChannelStockList> GetAsync(int channelId, string query) =>
        (await db.Api.GetFromJsonAsync<ChannelStockList>($"/api/stock-overview/channels/{channelId}{query}", Json, Ct))!;

    [Fact]
    public async Task Kanal_listesi_kanalin_bildirdigini_SKU_ile_yan_yana_ve_yanlis_eslesme_uyarisiyla_gosterir()
    {
        db.SkipIfUnavailable();
        var s = await SeedAsync();

        var list = await GetAsync(s.ChannelId, $"?search={s.Tag}");

        list.ChannelCode.Should().Be(Channel);
        list.Rows.Total.Should().Be(3);
        var rows = list.Rows.Items.ToDictionary(r => r.ListingId);

        var good = rows[s.GoodId];
        good.MatchBasis.Should().Be(MatchBases.Barcode);
        good.Warnings.Should().BeEmpty();
        good.SkuCode.Should().Be(s.SkuCode);
        good.Channel.Name.Should().Be($"Softcotton Bambu Havlu {s.Tag} Beyaz");
        good.ChannelStock.Should().Be(99999);
        good.FetchedAtUtc.Should().NotBeNull();
        good.HasDeviation.Should().BeTrue();

        var wrong = rows[s.WrongId];
        wrong.Warnings.Select(w => w.Code).Should().BeEquivalentTo(
            [MatchWarnings.BarcodeDiffers, MatchWarnings.ColorDiffers, MatchWarnings.SizeDiffers, MatchWarnings.NameDiffers]);
        wrong.ChannelStock.Should().BeNull("hiç mutabakat yok");
        wrong.NeedsReview.Should().BeTrue();

        var legacy = rows[s.LegacyId];
        legacy.Channel.Name.Should().Be("Bambu Havlu Eski Kayıt " + s.Tag, "eski ilanda ad eşleşmeyenler kaydından gelir");
        legacy.Channel.Brand.Should().Be("Softcotton");

        legacy.NeedsReview.Should().BeTrue("eski ilanın barkodu SKU'dan farklı");

        // Özet sayılar aramadan bağımsız, kanalın tamamı içindir (ekranın üst özeti)
        list.TotalListings.Should().BeGreaterThanOrEqualTo(3);
        list.DeviationCount.Should().BeGreaterThanOrEqualTo(1);
        list.ReviewCount.Should().BeGreaterThanOrEqualTo(2);
        list.NotFetchedCount.Should().BeGreaterThanOrEqualTo(2);
        list.LastFetchedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Kanal_listesi_sapma_ve_kontrol_filtreleri_eslesmeyen_sayisi_ve_yetki()
    {
        db.SkipIfUnavailable();
        var s = await SeedAsync();
        var before = (await GetAsync(s.ChannelId, "")).UnmatchedCount;
        await using (var c = db.Open())
        {
            await c.ExecuteAsync("""
                INSERT dbo.UnmatchedChannelItem (ChannelId, Kaynak, KanalAnahtar, KanalBarkod, UrunAdi, Neden, Durum)
                VALUES (@ChannelId, 'API', @key, @barcode, N'Eşleşmemiş ürün', 'ESLESME_YOK', 'BEKLIYOR');
                """, new { s.ChannelId, key = "B:" + Barcode(), barcode = Barcode() });
        }

        var deviations = await GetAsync(s.ChannelId, $"?search={s.Tag}&onlyDeviations=true");
        deviations.Rows.Items.Select(r => r.ListingId).Should().Equal(s.GoodId);

        var review = await GetAsync(s.ChannelId, $"?search={s.Tag}&onlyReview=true");
        review.Rows.Items.Select(r => r.ListingId).Should().BeEquivalentTo([s.WrongId, s.LegacyId]);

        // Arama: SKU kodu, kanal ürün kodu ve kanaldaki ad
        (await GetAsync(s.ChannelId, $"?search=sc-bad-{s.Tag.ToLowerInvariant()}")).Rows.Items.Should().ContainSingle(r => r.ListingId == s.WrongId);
        (await GetAsync(s.ChannelId, $"?search={s.SkuCode}")).Rows.Total.Should().Be(3);
        (await GetAsync(s.ChannelId, $"?search=Eski Kayıt {s.Tag}")).Rows.Items.Should().ContainSingle(r => r.ListingId == s.LegacyId);

        (await GetAsync(s.ChannelId, "")).UnmatchedCount.Should().Be(before + 1);

        using var support = await db.ApiHost.CreateClientAsAsync(Roles.Support);
        (await support.GetAsync($"/api/stock-overview/channels/{s.ChannelId}?search={s.Tag}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await db.Api.GetAsync("/api/stock-overview/channels/999999", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Kanal_listesi_kanaldaki_durumu_gosterir_ve_satis_durumuna_gore_filtreler()
    {
        db.SkipIfUnavailable();
        var tag = Unique();
        var skuId = await db.CreateSkuAsync(Barcode());
        int channelId;
        var ids = new Dictionary<string, int>();
        await using (var c = db.Open())
        {
            channelId = await c.ExecuteScalarAsync<int>("SELECT Id FROM dbo.Channel WHERE Kod = @Channel", new { Channel });
            // ADR-043: kanal durumu ham metin olarak KanalVerisi.kanalDurumu'nda; ilan durumu (AKTIF) değişmez
            foreach (var (key, status) in new[] { ("on", "Satışta"), ("oos", "Stoğu Tükendi"), ("off", "Kara listede"), ("none", (string?)null) })
            {
                ids[key] = await c.ExecuteScalarAsync<int>("""
                    INSERT dbo.ChannelListing (SkuId, ChannelId, KanalUrunKodu, KanalBarkod, Durum, KanalVerisi)
                    VALUES (@skuId, @channelId, @code, @barcode, 'AKTIF', @data);
                    SELECT CAST(SCOPE_IDENTITY() AS INT);
                    """, new
                {
                    skuId, channelId, code = $"SC-{key.ToUpperInvariant()}-{tag}", barcode = Barcode(),
                    data = JsonSerializer.Serialize(new { urunAdi = "Durum Testi " + tag, kanalDurumu = status })
                });
            }
        }

        var all = await GetAsync(channelId, $"?search={tag}");
        all.Rows.Total.Should().Be(4);
        var rows = all.Rows.Items.ToDictionary(r => r.ListingId);
        rows[ids["on"]].ChannelStatus.Should().Be("Satışta");
        rows[ids["on"]].SaleState.Should().Be(ChannelSaleState.OnSale);
        rows[ids["oos"]].SaleState.Should().Be(ChannelSaleState.OutOfStock);
        rows[ids["off"]].ChannelStatus.Should().Be("Kara listede");
        rows[ids["off"]].SaleState.Should().Be(ChannelSaleState.OffSale);
        rows[ids["off"]].Status.Should().Be("AKTIF", "kanal durumu ilanı pasif yapmaz");
        rows[ids["none"]].ChannelStatus.Should().BeNull();
        rows[ids["none"]].SaleState.Should().Be(ChannelSaleState.Unknown);

        (await GetAsync(channelId, $"?search={tag}&saleState=SATISTA")).Rows.Items.Select(r => r.ListingId).Should().Equal(ids["on"]);
        (await GetAsync(channelId, $"?search={tag}&saleState=STOK_YOK")).Rows.Items.Select(r => r.ListingId).Should().Equal(ids["oos"]);
        (await GetAsync(channelId, $"?search={tag}&saleState=SATIS_DISI")).Rows.Items.Select(r => r.ListingId).Should().Equal(ids["off"]);
        (await GetAsync(channelId, $"?search={tag}&saleState=BILINMIYOR")).Rows.Items.Select(r => r.ListingId).Should().Equal(ids["none"]);

        // Sayılar kanalın tamamı içindir (filtre kutusundaki adetler)
        all.SaleStateCounts.Should().ContainKeys(ChannelSaleState.All);
        all.SaleStateCounts[ChannelSaleState.OnSale].Should().BeGreaterThanOrEqualTo(1);
        all.SaleStateCounts.Values.Sum().Should().Be(all.TotalListings);

        (await db.Api.GetAsync($"/api/stock-overview/channels/{channelId}?saleState=HEPSI", Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Elle_baglanan_kanal_urununun_adi_ve_markasi_ilan_verisine_yazilir_eslestirme_degismez()
    {
        db.SkipIfUnavailable();
        var tag = Unique();
        var skuId = await db.CreateSkuAsync(Barcode());
        var barcode = Barcode();
        int itemId, channelId;
        await using (var c = db.Open())
        {
            channelId = await c.ExecuteScalarAsync<int>("SELECT Id FROM dbo.Channel WHERE Kod = @Channel", new { Channel });
            itemId = await c.ExecuteScalarAsync<int>("""
                INSERT dbo.UnmatchedChannelItem (ChannelId, Kaynak, KanalAnahtar, KanalBarkod, UrunAdi, Marka, Neden, Durum)
                VALUES (@channelId, 'API', @key, @barcode, @name, N'Softcotton', 'ESLESME_YOK', 'BEKLIYOR');
                SELECT CAST(SCOPE_IDENTITY() AS INT);
                """, new { channelId, key = "B:" + barcode, barcode, name = "Bağlanan Ürün " + tag });
        }

        var response = await db.Api.PostAsJsonAsync($"/api/channel-catalog/unmatched/{itemId}/link", new { skuId }, Json, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        await using (var c = db.Open())
        {
            var row = await c.QuerySingleAsync<(int SkuId, string Data)>(
                "SELECT SkuId, KanalVerisi FROM dbo.ChannelListing WHERE ChannelId = @channelId AND KanalBarkod = @barcode", new { channelId, barcode });
            row.SkuId.Should().Be(skuId);
            using var doc = JsonDocument.Parse(row.Data);
            doc.RootElement.GetProperty("urunAdi").GetString().Should().Be("Bağlanan Ürün " + tag);
            doc.RootElement.GetProperty("marka").GetString().Should().Be("Softcotton");
        }
    }
}
