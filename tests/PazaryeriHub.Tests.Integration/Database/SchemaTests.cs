using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using PazaryeriHub.Infrastructure.Database;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Database;

/// <summary>Şemanın spesifikasyona uyduğunu gerçek veritabanında doğrular (collation, unique/filtreli indeksler, idempotentlik).</summary>
[Collection(DatabaseCollection.Name)]
public sealed class SchemaTests(TestDatabase db)
{
    [Fact]
    public async Task Veritabani_izolasyon_ayarlari_acik()
    {
        db.SkipIfUnavailable();
        await using var connection = db.Open();

        var (rcsi, snapshot) = await connection.QuerySingleAsync<(bool, int)>(
            "SELECT is_read_committed_snapshot_on, snapshot_isolation_state FROM sys.databases WHERE name = DB_NAME()");

        rcsi.Should().BeTrue();
        snapshot.Should().Be(1);
    }

    [Theory]
    [InlineData("Sku", "SkuKodu", "Latin1_General_100_BIN2")]
    [InlineData("Sku", "Barkod", "Latin1_General_100_BIN2")]
    [InlineData("ChannelListing", "KanalUrunKodu", "Latin1_General_100_BIN2")]
    [InlineData("ChannelListing", "KanalBarkod", "Latin1_General_100_BIN2")]
    [InlineData("Order", "KanalSiparisNo", "Latin1_General_100_BIN2")]
    [InlineData("Product", "Ad", "Turkish_CI_AS")]
    [InlineData("Product", "Marka", "Turkish_CI_AS")]
    public async Task Kolon_collationlari_spesifikasyona_uygun(string table, string column, string expected)
    {
        db.SkipIfUnavailable();
        await using var connection = db.Open();

        var collation = await connection.ExecuteScalarAsync<string>(
            "SELECT collation_name FROM sys.columns WHERE object_id = OBJECT_ID(QUOTENAME('dbo') + '.' + QUOTENAME(@table)) AND name = @column",
            new { table, column });

        collation.Should().Be(expected);
    }

    [Fact]
    public async Task Tum_tablolar_ve_yordamlar_mevcut()
    {
        db.SkipIfUnavailable();
        await using var connection = db.Open();

        var tables = (await connection.QueryAsync<string>("SELECT name FROM sys.tables WHERE schema_id = SCHEMA_ID('dbo')")).ToList();
        tables.Should().Contain(["Product", "Sku", "Warehouse", "StockBalance", "StockMovement", "Channel", "ChannelListing",
            "ChannelStockSnapshot", "Order", "OrderLine", "OrderEvent", "Outbox", "SyncQueue", "UnmatchedChannelItem"]);

        var procs = (await connection.QueryAsync<string>("SELECT name FROM sys.procedures WHERE schema_id = SCHEMA_ID('dbo')")).ToList();
        procs.Should().Contain(["usp_StokRezerve", "usp_StokSevk", "usp_RezerveIptal", "usp_IadeGiris", "usp_StokGiris", "usp_SayimDuzelt"]);
    }

    [Fact]
    public async Task Sku_kodu_buyuk_kucuk_harf_ve_turkce_i_ayrimi_yapar()
    {
        db.SkipIfUnavailable();
        await using var connection = db.Open();
        var productId = await connection.ExecuteScalarAsync<int>("INSERT dbo.Product (Ad) OUTPUT inserted.Id VALUES (N'Collation testi')");
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // BIN2: "I", "İ", "ı", "i" dört farklı değer; Turkish_CI_AS'ta bazıları eşit sayılırdı.
        foreach (var code in new[] { $"KIRMIZI-{suffix}", $"KİRMİZİ-{suffix}", $"kırmızı-{suffix}", $"kirmizi-{suffix}" })
        {
            await connection.ExecuteAsync("INSERT dbo.Sku (ProductId, SkuKodu) VALUES (@productId, @code)", new { productId, code });
        }

        var act = () => connection.ExecuteAsync("INSERT dbo.Sku (ProductId, SkuKodu) VALUES (@productId, @code)",
            new { productId, code = $"KIRMIZI-{suffix}" });
        (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(2627, "aynı SKU kodu ikinci kez eklenemez");
    }

    [Fact]
    public async Task Barkod_benzersiz_ama_null_degerler_serbest()
    {
        db.SkipIfUnavailable();
        await db.CreateSkuAsync(barcode: null);
        await db.CreateSkuAsync(barcode: null);
        var barcode = "869" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999);
        await db.CreateSkuAsync(barcode);

        var act = () => db.CreateSkuAsync(barcode);

        (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(2601);
    }

    [Fact]
    public async Task Ayni_kanal_siparis_no_ikinci_kez_eklenemez()
    {
        db.SkipIfUnavailable();
        await using var connection = db.Open();
        var siparisNo = "TST-" + Guid.NewGuid().ToString("N");
        const string sql = """
            INSERT dbo.[Order] (ChannelId, KanalSiparisNo, Toplam, SiparisTarihi)
            SELECT Id, @siparisNo, 100, SYSUTCDATETIME() FROM dbo.Channel WHERE Kod = 'TRENDYOL'
            """;

        await connection.ExecuteAsync(sql, new { siparisNo });
        var act = () => connection.ExecuteAsync(sql, new { siparisNo });

        (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(2627);
    }

    [Fact]
    public async Task SyncQueue_ayni_listing_icin_tek_bekleyen_kayit_kabul_eder()
    {
        db.SkipIfUnavailable();
        var sku = await db.CreateSkuAsync();
        await using var connection = db.Open();
        var listingId = await connection.ExecuteScalarAsync<int>("""
            INSERT dbo.ChannelListing (SkuId, ChannelId, KanalUrunKodu) OUTPUT inserted.Id
            SELECT @sku, Id, @code FROM dbo.Channel WHERE Kod = 'HEPSIBURADA'
            """, new { sku, code = "SQ-" + Guid.NewGuid().ToString("N") });
        const string insert = "INSERT dbo.SyncQueue (ListingId, Tip, Durum) VALUES (@listingId, @tip, @durum)";

        await connection.ExecuteAsync(insert, new { listingId, tip = "STOK", durum = "BEKLIYOR" });
        await connection.ExecuteAsync(insert, new { listingId, tip = "FIYAT", durum = "BEKLIYOR" });   // farklı tip: serbest
        await connection.ExecuteAsync(insert, new { listingId, tip = "STOK", durum = "GONDERILDI" });  // bekleyen değil: serbest
        await connection.ExecuteAsync(insert, new { listingId, tip = "STOK", durum = "HATA" });

        var act = () => connection.ExecuteAsync(insert, new { listingId, tip = "STOK", durum = "BEKLIYOR" });

        (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(2601, "debounce: aynı listing+tip için ikinci bekleyen kayıt olamaz");
    }

    [Fact]
    public async Task Json_kolonlari_gecersiz_json_kabul_etmez()
    {
        db.SkipIfUnavailable();
        await using var connection = db.Open();

        var act = () => connection.ExecuteAsync("INSERT dbo.Outbox (OlayTipi, Payload) VALUES ('Test', N'{gecersiz')");

        (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(547);
    }

    [Fact]
    public async Task Fiziksel_negatif_ve_rezerve_fizikselden_buyuk_olamaz()
    {
        db.SkipIfUnavailable();
        var sku = await db.CreateSkuAsync();
        await using var connection = db.Open();
        const string insert = "INSERT dbo.StockBalance (SkuId, WarehouseId, Fiziksel, Rezerve) VALUES (@sku, @wh, @f, @r)";

        var negatif = () => connection.ExecuteAsync(insert, new { sku, wh = db.DefaultWarehouseId, f = -1, r = 0 });
        var fazlaRezerve = () => connection.ExecuteAsync(insert, new { sku, wh = db.DefaultWarehouseId, f = 1, r = 2 });

        (await negatif.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(547);
        (await fazlaRezerve.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(547);
    }

    [Fact]
    public void Migration_tekrar_calistirildiginda_surumlu_scriptler_yeniden_calismaz()
    {
        db.SkipIfUnavailable();

        var second = new DatabaseMigrator(NullLogger<DatabaseMigrator>.Instance).Migrate(db.ConnectionString!);

        second.Should().NotContain(s => s.StartsWith("Migrations/", StringComparison.Ordinal));
        second.Should().OnlyContain(s => s.StartsWith("Procedures/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Baslangic_verisi_yuklu()
    {
        db.SkipIfUnavailable();
        await using var connection = db.Open();

        var codes = await connection.QueryAsync<string>("SELECT Kod FROM dbo.Channel");
        codes.Should().BeEquivalentTo(["TRENDYOL", "HEPSIBURADA", "BEYMEN", "SOFTCOTTON"]);
        db.DefaultWarehouseId.Should().BePositive();
    }

    // ------------------------------------------------------------------ ADR-023: çoklu ilan

    [Fact]
    public async Task Ayni_sku_ayni_kanalda_iki_ilana_sahip_olabilir_ama_ayni_barkod_veya_kod_iki_ilanda_olamaz()
    {
        db.SkipIfUnavailable();
        var skuA = await db.CreateSkuAsync();
        var skuB = await db.CreateSkuAsync();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        await using var connection = db.Open();
        const string insert = """
            INSERT dbo.ChannelListing (SkuId, ChannelId, KanalBarkod, KanalUrunKodu)
            SELECT @sku, Id, @barcode, @code FROM dbo.Channel WHERE Kod = 'HEPSIBURADA'
            """;

        await connection.ExecuteAsync(insert, new { sku = skuA, barcode = "B1-" + suffix, code = (string?)null });
        await connection.ExecuteAsync(insert, new { sku = skuA, barcode = "B2-" + suffix, code = (string?)null });   // aynı SKU ikinci ilan
        await connection.ExecuteAsync(insert, new { sku = skuA, barcode = (string?)null, code = "K1-" + suffix });

        var sameBarcode = () => connection.ExecuteAsync(insert, new { sku = skuB, barcode = "B1-" + suffix, code = (string?)null });
        (await sameBarcode.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(2601);
        var sameCode = () => connection.ExecuteAsync(insert, new { sku = skuA, barcode = (string?)null, code = "K1-" + suffix });
        (await sameCode.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(2601);
    }

    /// <summary>
    /// 0008 ön kontrolü: mevcut veride aynı kanal + barkod/kod çakışması varsa script hiçbir şeyi değiştirmeden durur.
    /// Geçici bir veritabanında (adında "Test" geçer) yalnızca ChannelListing'in ilgili kolonlarıyla denenir; sonra silinir.
    /// </summary>
    [Theory]
    [InlineData("KanalBarkod", 50080)]
    [InlineData("KanalUrunKodu", 50081)]
    public async Task Migration_0008_mevcut_cakismada_durur(string column, int expectedError)
    {
        db.SkipIfUnavailable();
        var script = ReadEmbedded("PazaryeriHub.Infrastructure.Database.Migrations.0008_coklu_ilan_ve_esik.sql");
        var name = "PazaryeriHub_Test_0008_" + Guid.NewGuid().ToString("N")[..8];
        var builder = new SqlConnectionStringBuilder(db.ConnectionString) { InitialCatalog = "master", Pooling = false };
        await using (var master = new SqlConnection(builder.ConnectionString))
        {
            await master.OpenAsync(TestContext.Current.CancellationToken);
            await master.ExecuteAsync($"CREATE DATABASE [{name}]");
        }
        try
        {
            builder.InitialCatalog = name;
            await using var temp = new SqlConnection(builder.ConnectionString);
            await temp.OpenAsync(TestContext.Current.CancellationToken);
            await temp.ExecuteAsync($"""
                CREATE TABLE dbo.ChannelListing (Id INT IDENTITY PRIMARY KEY, ChannelId INT NOT NULL,
                    KanalBarkod NVARCHAR(50) NULL, KanalUrunKodu NVARCHAR(100) NULL);
                INSERT dbo.ChannelListing (ChannelId, {column}) VALUES (1, N'AYNI'), (1, N'AYNI');
                """);

            var act = () => temp.ExecuteAsync(script);

            (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(expectedError);
            (await temp.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.ChannelListing")).Should().Be(2, "veri silinmez/birleştirilmez");
        }
        finally
        {
            builder.InitialCatalog = "master";
            await using var master = new SqlConnection(builder.ConnectionString);
            await master.OpenAsync(TestContext.Current.CancellationToken);
            await master.ExecuteAsync($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
        }
    }

    private static string ReadEmbedded(string resource)
    {
        using var stream = typeof(PazaryeriHub.Infrastructure.Database.DatabaseMigrator).Assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
