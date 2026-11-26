using System.Net;
using System.Net.Http.Json;
using Dapper;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using PazaryeriHub.Adapters.Mock;
using PazaryeriHub.Application.ChannelCatalog;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Infrastructure.ChannelCatalog;
using PazaryeriHub.Tests.Integration.Excel;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.ChannelCatalog;

/// <summary>KatalogCek (ADR-022): mock kanal API'sinden sayfalı çekme, önizleme/uygulama, durum kaydı, eşzamanlı istek ve hata.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class CatalogFetchJobTests(TestDatabase db)
{
    private const string Channel = "SOFTCOTTON"; // gerçek adaptörü olmayan, testlerde başka iş yapılmayan mock kanal

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MockChannelStore Mock => db.Services.GetRequiredService<MockChannelStore>();

    [Fact]
    public async Task Kanal_katalogu_sayfali_cekilir_onizlenir_ve_uygulanir()
    {
        db.SkipIfUnavailable();
        var p = "KF" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var channelId = await ChannelIdAsync();
        var known = await db.CreateSkuAsync(barcode: $"{p}-B1");

        Mock.CatalogPageSize = 2;
        Mock.SetCatalog(Channel, [
            Entry($"{p}-B1"),                     // barkodla eşleşir
            Entry($"{p}-B2"),                     // eşleşmez
            Entry($"{p}-B3", passive: true),      // eşleşmez
            Entry(null),                          // kodsuz → hatalı öğe
            Entry($"{p}-B1")                      // tekrar → 1 ve 5 hatalı
        ]);
        try
        {
            // Önizleme: yazılmaz, rapor durum kaydına düşer
            var preview = await RunAsync(channelId, validateOnly: true);
            preview!.Source.Should().Be(CatalogImportSources.Api);
            preview.TotalRows.Should().Be(5, "3 sayfa (2+2+1) birleştirilir");
            preview.Errors.Select(e => e.Row).Distinct().Should().Equal(1, 4, 5);
            preview.Unmatched.Should().Be(2);
            (await ListingCountAsync(channelId, p)).Should().Be(0);

            var status = await db.Api.GetFromJsonAsync<CatalogFetchStatus>($"/api/channels/{channelId}/catalog/fetch-status", ExcelImportApiTests.Json, Ct);
            status!.State.Should().Be(CatalogFetchStates.Completed);
            status.Report!.ValidateOnly.Should().BeTrue();
            status.Report.Unmatched.Should().Be(2);

            // Tekrarı kaldır → uygula: B1 bağlanır, B2/B3 eşleşmeyenlere
            Mock.SetCatalog(Channel, [Entry($"{p}-B1"), Entry($"{p}-B2"), Entry($"{p}-B3", passive: true)]);
            var applied = await RunAsync(channelId, validateOnly: false);
            applied!.MatchedByBarcode.Should().Be(1);
            applied.ListingsCreated.Should().Be(1);
            (await ListingCountAsync(channelId, p)).Should().Be(1);
            await using (var c = db.Open())
            {
                (await c.QueryAsync<(string KanalAnahtar, string Kaynak)>(
                    "SELECT KanalAnahtar, Kaynak FROM dbo.UnmatchedChannelItem WHERE ChannelId = @channelId AND KanalAnahtar LIKE @p",
                    new { channelId, p = p + "%" })).Should().BeEquivalentTo(new[] { ($"{p}-B2", "API"), ($"{p}-B3", "API") });
                (await c.ExecuteScalarAsync<int>("SELECT SkuId FROM dbo.ChannelListing WHERE ChannelId = @channelId AND KanalBarkod = @b",
                    new { channelId, b = $"{p}-B1" })).Should().Be(known);
            }
        }
        finally
        {
            Mock.SetCatalog(Channel, []);
            Mock.CatalogPageSize = 100;
        }
    }

    [Fact]
    public async Task Istek_kuyruga_alinir_calisirken_ikinci_istek_reddedilir_kanal_hatasi_kaydedilir()
    {
        db.SkipIfUnavailable();
        var channelId = await ChannelIdAsync();

        var first = await db.Api.PostAsync($"/api/channels/{channelId}/catalog/fetch?validateOnly=true", null, Ct);
        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobId = (await first.Content.ReadFromJsonAsync<Dictionary<string, string>>(Ct))!["jobId"];
        try
        {
            (await db.Api.PostAsync($"/api/channels/{channelId}/catalog/fetch?validateOnly=true", null, Ct))
                .StatusCode.Should().Be(HttpStatusCode.Conflict, "aynı kanal için iş zaten çalışıyor");
            (await db.Api.GetFromJsonAsync<CatalogFetchStatus>($"/api/channels/{channelId}/catalog/fetch-status", ExcelImportApiTests.Json, Ct))!
                .State.Should().Be(CatalogFetchStates.Running);
        }
        finally
        {
            // Test sunucusu bu kuyruğu dinlemez; iş elle silinir.
            db.Services.GetRequiredService<IBackgroundJobClient>().Delete(jobId);
        }

        Mock.FailNextCatalog[Channel] = new ChannelApiException(Channel, "Kanal geçici olarak yanıt vermiyor.", 503, transient: true);
        var act = () => RunAsync(channelId, validateOnly: true);
        await act.Should().ThrowAsync<ChannelApiException>();

        var status = await db.Api.GetFromJsonAsync<CatalogFetchStatus>($"/api/channels/{channelId}/catalog/fetch-status", ExcelImportApiTests.Json, Ct);
        status!.State.Should().Be(CatalogFetchStates.Failed);
        status.LastError.Should().Be("Kanal geçici olarak yanıt vermiyor.");
    }

    /// <summary>
    /// ADR-031: "Eşleşmeyenlerden ürün oluştur" farklı renk/ebat ürünlerini tek SKU'da birleştirmez. Aynı model kodunda, renk/beden
    /// okunamayan 3 barkod (2 farklı içerik + farklı stok kodlu 1 ürün) → 3 ayrı SKU; aynı içerik + aynı varyantın ikinci ilanı aynı SKU'ya
    /// bağlanır. Ürün linki ve kanal fiyatı ilan kaydına (KanalVerisi) yazılır, eşleştirme listesinde döner.
    /// </summary>
    [Fact]
    public async Task Eslesmeyenlerden_urun_olusturma_farkli_varyantlari_birlestirmez_link_ve_fiyat_yazilir()
    {
        db.SkipIfUnavailable();
        var p = "KV" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        // Ad tamamen rastgele: önceki çalıştırmaların ürünleriyle ad benzerliği önerisi oluşmasın (öneri varsa SKU açılmaz).
        var name = Guid.NewGuid().ToString("N");
        var channelId = await ChannelIdAsync();
        ChannelCatalogEntry E(string barcode, string stock, string variantKey, string? url = null) =>
            new(null, barcode, stock, $"{p}-MDL", null, null, name, "Marka", "Havlu", "Satışta", false, variantKey, null, url, url is null ? null : 349.9m, url is null ? null : 499.9m);
        Mock.SetCatalog(Channel, [
            E($"{p}-001", $"{p}-75150", "c1", "https://www.trendyol.com/chain-havlu-p-1"),
            E($"{p}-002", $"{p}-75150", "c2"),
            E($"{p}-003", $"{p}-50100", "c3"),
            E($"{p}-004", $"{p}-75150", "c1")   // c1'in çoklanan ilanı → 001 ile aynı SKU
        ]);
        try
        {
            using (var scope = db.Services.CreateScope())
            {
                var job = ActivatorUtilities.CreateInstance<CatalogFetchJob>(scope.ServiceProvider);
                var report = await job.RunAsync(channelId, validateOnly: false, createMissing: true, "test", Ct);
                report!.SkusCreated.Should().Be(3);
            }

            await using var c = db.Open();
            var skuByBarcode = (await c.QueryAsync<(string KanalBarkod, int SkuId)>(
                    "SELECT KanalBarkod, SkuId FROM dbo.ChannelListing WHERE ChannelId = @channelId AND KanalBarkod LIKE @p", new { channelId, p = p + "%" }))
                .ToDictionary(x => x.KanalBarkod, x => x.SkuId);
            skuByBarcode.Values.Distinct().Should().HaveCount(3);
            skuByBarcode[$"{p}-001"].Should().Be(skuByBarcode[$"{p}-004"]);
            skuByBarcode[$"{p}-001"].Should().NotBe(skuByBarcode[$"{p}-002"]);
            skuByBarcode[$"{p}-002"].Should().NotBe(skuByBarcode[$"{p}-003"]);

            var listings = await db.Api.GetFromJsonAsync<PazaryeriHub.Application.Common.PagedResult<ListingDto>>(
                $"/api/listings?channelId={channelId}&search={p}-001", ExcelImportApiTests.Json, Ct);
            listings!.Items.Single(l => l.ChannelBarcode == $"{p}-001").ProductUrl.Should().Be("https://www.trendyol.com/chain-havlu-p-1");
            var data = await c.ExecuteScalarAsync<string>("SELECT KanalVerisi FROM dbo.ChannelListing WHERE KanalBarkod = @b", new { b = $"{p}-001" });
            ListingChannelData.Parse(data).Should().Match<ListingChannelData>(d => d.SalePrice == 349.9m && d.ListPrice == 499.9m);
        }
        finally
        {
            Mock.SetCatalog(Channel, []);
        }
    }

    /// <summary>
    /// ADR-056: Hepsiburada önce barkodsuz geliyordu (eşleşmeyen kaydın anahtarı = kanal ürün kodu). Barkod gelmeye başlayınca
    /// eski kayıt kanal ürün koduyla bulunup barkod anahtarına taşınır: aynı ürün ikinci kez açılmaz; eşleşen ürünün eski kaydı bağlandı olur.
    /// </summary>
    [Fact]
    public async Task Barkod_sonradan_gelince_eski_kanal_kodlu_eslesmeyen_kayit_tasinir_ikinci_kez_acilmaz()
    {
        db.SkipIfUnavailable();
        var p = "KH" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var channelId = await ChannelIdAsync();
        var known = await db.CreateSkuAsync(barcode: $"{p}-B1");
        try
        {
            // 1. çekim: barkod yok → anahtar kanal ürün kodu
            Mock.SetCatalog(Channel, [CodeEntry($"{p}-HB1", null), CodeEntry($"{p}-HB2", null)]);
            await RunAsync(channelId, validateOnly: false);
            // 2. çekim: barkodlar geldi; HB1 barkodla eşleşir, HB2 yine eşleşmez
            Mock.SetCatalog(Channel, [CodeEntry($"{p}-HB1", $"{p}-B1"), CodeEntry($"{p}-HB2", $"{p}-B2")]);
            var applied = await RunAsync(channelId, validateOnly: false);
            applied!.MatchedByBarcode.Should().Be(1);

            await using var c = db.Open();
            (await c.QueryAsync<(string KanalAnahtar, string KanalUrunKodu, string? KanalBarkod, string Durum)>(
                "SELECT KanalAnahtar, KanalUrunKodu, KanalBarkod, Durum FROM dbo.UnmatchedChannelItem WHERE ChannelId = @channelId AND KanalUrunKodu LIKE @p",
                new { channelId, p = p + "%" })).Should().BeEquivalentTo(new[]
            {
                ($"{p}-HB1", $"{p}-HB1", (string?)null, "BAGLANDI"),
                ($"{p}-B2", $"{p}-HB2", (string?)$"{p}-B2", "BEKLIYOR")
            });
            (await c.ExecuteScalarAsync<int>("SELECT SkuId FROM dbo.ChannelListing WHERE ChannelId = @channelId AND KanalBarkod = @b",
                new { channelId, b = $"{p}-B1" })).Should().Be(known);
        }
        finally
        {
            Mock.SetCatalog(Channel, []);
        }
    }

    /// <summary>
    /// ADR-057: kanal barkodu değişirse (HB'nin ürettiği barkod → stok kodu) eşleşmeyen kayıt kanal ürün koduyla bulunup yeni anahtara
    /// taşınır; ilanlı öğede ilanın kanal barkodu güncellenir.
    /// </summary>
    [Fact]
    public async Task Kanal_barkodu_degisince_eslesmeyen_kayit_urun_koduyla_tasinir_ilan_barkodu_guncellenir()
    {
        db.SkipIfUnavailable();
        var p = "KG" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var channelId = await ChannelIdAsync();
        var known = await db.CreateSkuAsync(barcode: $"{p}-ESKI1");
        try
        {
            Mock.SetCatalog(Channel, [CodeEntry($"{p}-HB1", $"{p}-ESKI1"), CodeEntry($"{p}-HB2", $"{p}-ESKI2")]);
            await RunAsync(channelId, validateOnly: false);
            Mock.SetCatalog(Channel, [CodeEntry($"{p}-HB1", $"{p}-YENI1"), CodeEntry($"{p}-HB2", $"{p}-YENI2")]);
            var applied = await RunAsync(channelId, validateOnly: false);
            applied!.MatchedExisting.Should().Be(1, "ilan kanal ürün koduyla tanınır");

            await using var c = db.Open();
            (await c.QueryAsync<(string KanalAnahtar, string Durum)>(
                "SELECT KanalAnahtar, Durum FROM dbo.UnmatchedChannelItem WHERE ChannelId = @channelId AND KanalUrunKodu LIKE @p",
                new { channelId, p = p + "%" })).Should().BeEquivalentTo(new[] { ($"{p}-YENI2", "BEKLIYOR") });
            (await c.ExecuteScalarAsync<string>("SELECT KanalBarkod FROM dbo.ChannelListing WHERE ChannelId = @channelId AND SkuId = @known",
                new { channelId, known })).Should().Be($"{p}-YENI1");
        }
        finally
        {
            Mock.SetCatalog(Channel, []);
        }
    }

    private static ChannelCatalogEntry CodeEntry(string code, string? barcode) =>
        new(code, barcode, null, null, null, null, Guid.NewGuid().ToString("N"), null, null, "Satışta", false);

    private static ChannelCatalogEntry Entry(string? barcode, bool passive = false) =>
        new(null, barcode, null, null, null, null, "Katalog test ürünü", null, null, passive ? "Arşivlendi" : "Satışta", passive);

    private async Task<CatalogImportReport?> RunAsync(int channelId, bool validateOnly)
    {
        using var scope = db.Services.CreateScope();
        var job = ActivatorUtilities.CreateInstance<CatalogFetchJob>(scope.ServiceProvider);
        return await job.RunAsync(channelId, validateOnly, createMissing: false, "test", Ct);
    }

    private async Task<int> ChannelIdAsync()
    {
        await using var c = db.Open();
        return await c.ExecuteScalarAsync<int>("SELECT Id FROM dbo.Channel WHERE Kod = @Channel", new { Channel });
    }

    private async Task<int> ListingCountAsync(int channelId, string prefix)
    {
        await using var c = db.Open();
        return await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.ChannelListing WHERE ChannelId = @channelId AND KanalBarkod LIKE @p",
            new { channelId, p = prefix + "%" });
    }
}
