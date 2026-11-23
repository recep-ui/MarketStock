using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using PazaryeriHub.Adapters.Mock;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.Catalog;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Excel;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Application.Pricing;
using PazaryeriHub.Application.Sync;
using PazaryeriHub.Infrastructure.Sync;
using PazaryeriHub.Tests.Integration.Excel;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Pricing;

/// <summary>
/// M4 Fiyat (ADR-028) kabul kriterleri: yetenek kapalıyken hiçbir şey gitmez; açıkken mock kanalda görünür; eşik aşımı onaya düşer;
/// onaylanan gider, reddedilen gitmez; taban delinmez; KDV doğrulanmamış kanalda yetenek açılamaz; stok hattı etkilenmez.
/// SOFTCOTTON (mock) kanalı kullanılır; her test kanalı temiz duruma getirir ve sonunda kapatır.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PricePipelineTests(TestDatabase db)
{
    private const string Channel = ChannelCodes.Softcotton;
    private static readonly JsonSerializerOptions Json = ExcelImportApiTests.Json;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MockChannelStore Mock => db.Services.GetRequiredService<MockChannelStore>();

    // ------------------------------------------------------------------ hazırlık / yardımcılar

    private async Task<int> PrepareAsync(string? settings = null)
    {
        await using var c = db.Open();
        var channelId = await c.ExecuteScalarAsync<int>("SELECT Id FROM dbo.Channel WHERE Kod = @Channel", new { Channel });
        await c.ExecuteAsync("""
            UPDATE dbo.Outbox SET IslendiAt = SYSUTCDATETIME() WHERE IslendiAt IS NULL;
            UPDATE q SET Durum = 'HATA', Hata = N'test hazırlığı' FROM dbo.SyncQueue q JOIN dbo.ChannelListing l ON l.Id = q.ListingId
             WHERE l.ChannelId = @channelId AND q.Durum = 'BEKLIYOR';
            UPDATE a SET Durum = 'GECERSIZ' FROM dbo.PriceApproval a JOIN dbo.ChannelListing l ON l.Id = a.ListingId
             WHERE l.ChannelId = @channelId AND a.Durum = 'BEKLIYOR';
            UPDATE dbo.ChannelListing SET Durum = 'PASIF' WHERE ChannelId = @channelId;
            UPDATE dbo.Channel SET Aktif = 1, PayYuzde = 100, Ayarlar = @settings WHERE Id = @channelId;
            UPDATE dbo.ChannelCapability SET Durum = 'KAPALI' WHERE ChannelId = @channelId AND Yetenek = 'FIYAT';
            UPDATE dbo.AppSetting SET Deger = N'30' WHERE Anahtar = 'FiyatSapmaEsigiYuzde';
            -- Stok hattı onaysız (ACIK): bu testler fiyatın stoğu etkilemediğini doğrular (varsayılan "Onaylı", ADR-048)
            MERGE dbo.ChannelCapability AS t USING (SELECT @channelId AS ChannelId, 'STOK' AS Yetenek) AS s
               ON t.ChannelId = s.ChannelId AND t.Yetenek = s.Yetenek
             WHEN MATCHED THEN UPDATE SET Durum = 'ACIK'
             WHEN NOT MATCHED THEN INSERT (ChannelId, Yetenek, Durum) VALUES (@channelId, 'STOK', 'ACIK');
            """, new { channelId, settings });
        Mock.PriceRules.TryRemove(Channel, out _);
        Mock.FailingItems.Clear();
        return channelId;
    }

    private async Task RestoreAsync()
    {
        await using var c = db.Open();
        await c.ExecuteAsync("""
            UPDATE dbo.Channel SET Aktif = 0, PayYuzde = 100, Ayarlar = NULL WHERE Kod = @Channel;
            UPDATE cap SET Durum = 'KAPALI' FROM dbo.ChannelCapability cap JOIN dbo.Channel c ON c.Id = cap.ChannelId
             WHERE c.Kod = @Channel AND cap.Yetenek = 'FIYAT';
            UPDATE dbo.AppSetting SET Deger = N'30' WHERE Anahtar = 'FiyatSapmaEsigiYuzde';
            """, new { Channel });
        Mock.PriceRules.TryRemove(Channel, out _);
        Mock.FailingItems.Clear();
    }

    private static string NewCode() => "FY-" + Guid.NewGuid().ToString("N")[..12];

    private async Task SetSkuPriceAsync(int skuId, decimal? sale, decimal? list = null, HttpClient? client = null)
    {
        var response = await (client ?? db.Api).PutAsJsonAsync($"/api/prices/skus/{skuId}", new SkuPriceInput(list, sale), Json, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<(int SkuId, int ListingId, string Code)> PricedListingAsync(int channelId, decimal sale, decimal? list = null, int stock = 5)
    {
        var skuId = await db.CreateSkuAsync();
        await db.Ledger.ReceiveAsync(new(skuId, db.DefaultWarehouseId, stock), Ct);
        await SetSkuPriceAsync(skuId, sale, list);
        var code = NewCode();
        var response = await db.Api.PutAsJsonAsync("/api/listings", new ListingInput(skuId, channelId, code, null, null), Json, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (skuId, (await response.Content.ReadFromJsonAsync<ListingDto>(Json, Ct))!.Id, code);
    }

    private async Task SetCapabilityAsync(int channelId, string state)
    {
        var response = await db.Api.PutAsJsonAsync($"/api/channels/{channelId}/capabilities/FIYAT", new ChannelCapabilityUpdate(state), Json, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<T> RunAsync<T>(Func<IServiceProvider, Task<T>> run)
    {
        using var scope = db.Services.CreateScope();
        return await run(scope.ServiceProvider);
    }

    private Task<OutboxRunResult> OutboxAsync() => RunAsync(sp => sp.GetRequiredService<OutboxProcessorJob>().RunAsync(Ct));
    private Task<PushRunResult> PricePushAsync() => RunAsync(sp => sp.GetRequiredService<PricePushJob>().RunAsync(Channel, Ct));
    private Task<PushRunResult> StockPushAsync() => RunAsync(sp => sp.GetRequiredService<StockPushJob>().RunAsync(Channel, Ct));
    private Task<BatchCheckRunResult> BatchCheckAsync() => RunAsync(sp => sp.GetRequiredService<BatchCheckJob>().RunAsync(Ct));

    private async Task SyncOnceAsync()
    {
        await OutboxAsync();
        await PricePushAsync();
        await StockPushAsync();
        await BatchCheckAsync();
    }

    private async Task<List<(long Id, string Durum, decimal? GonderilenSatisFiyati)>> PriceQueueAsync(int listingId)
    {
        await using var c = db.Open();
        return (await c.QueryAsync<(long, string, decimal?)>(
            "SELECT Id, Durum, GonderilenSatisFiyati FROM dbo.SyncQueue WHERE ListingId = @listingId AND Tip = 'FIYAT' ORDER BY Id", new { listingId })).ToList();
    }

    private async Task<(decimal? Sale, decimal? List)> LastSentAsync(int listingId)
    {
        await using var c = db.Open();
        return await c.QuerySingleAsync<(decimal?, decimal?)>(
            "SELECT SonGonderilenSatisFiyati, SonGonderilenListeFiyati FROM dbo.ChannelListing WHERE Id = @listingId", new { listingId });
    }

    private async Task<PriceApprovalDto?> PendingApprovalAsync(int channelId, int listingId)
    {
        var page = await db.Api.GetFromJsonAsync<PagedResult<PriceApprovalDto>>($"/api/prices/approvals?state=BEKLIYOR&channelId={channelId}&pageSize=500", Json, Ct);
        return page!.Items.SingleOrDefault(a => a.ListingId == listingId);
    }

    // ------------------------------------------------------------------ kabul

    [Fact]
    public async Task Yetenek_kapaliyken_fiyat_kuyruga_girmez_ve_kanala_hicbir_sey_gitmez()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareAsync();
        try
        {
            var (_, listingId, code) = await PricedListingAsync(channelId, 100m);
            await SyncOnceAsync();

            (await PriceQueueAsync(listingId)).Should().BeEmpty("FIYAT yeteneği KAPALI kanalda fiyat değerlendirilmez");
            (await PricePushAsync()).Should().Match<PushRunResult>(r => r.Skipped && r.SkipReason!.Contains("kapalı"));
            Mock.PricePushesOf(Channel).Should().NotContain(u => u.ChannelProductCode == code);
            Mock.GetChannelStock(Channel, code).Should().Be(5, "stok hattı fiyat yeteneğinden bağımsız çalışır");
        }
        finally
        {
            await RestoreAsync();
        }
    }

    [Fact]
    public async Task Onizleme_sonra_yetenek_acilinca_kurala_gore_fiyat_mock_kanala_bir_kez_gider_ve_gecmise_yazilir()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareAsync();
        try
        {
            var rule = await db.Api.PutAsJsonAsync($"/api/prices/channels/{channelId}/rule", new ChannelPriceRuleDto(1.1m, 0m, "KURUS_90", 50m), Json, Ct);
            rule.StatusCode.Should().Be(HttpStatusCode.OK, await rule.Content.ReadAsStringAsync(Ct));
            var (skuId, listingId, code) = await PricedListingAsync(channelId, 100m, 120m);

            // Önizleme (yetenek kapalı): "açılırsa ne olur"
            var preview = await db.Api.GetFromJsonAsync<PricePreview>($"/api/prices/channels/{channelId}/preview?onlyChanges=true", Json, Ct);
            preview!.CapabilityState.Should().Be("KAPALI");
            preview.CanEnable.Should().BeTrue();
            var row = preview.Rows.Single(r => r.ListingId == listingId);
            row.Status.Should().Be(PricePreviewStatus.Change);
            row.LastSentSale.Should().BeNull("ilk gönderim");
            row.NewSale.Should().Be(110.90m, "100 × 1,1 = 110,00 → ,90");
            row.NewList.Should().Be(132.90m, "120 × 1,1 = 132,00 → ,90");
            preview.FirstSend.Should().BeGreaterThanOrEqualTo(1);
            (await PriceQueueAsync(listingId)).Should().BeEmpty("önizleme kanala/kuyruğa yazmaz");

            await SetCapabilityAsync(channelId, "ACIK");
            await SyncOnceAsync();

            Mock.PricesOf(Channel)[code].Should().Be((132.90m, 110.90m));
            (await LastSentAsync(listingId)).Should().Be((110.90m, 132.90m));
            (await PriceQueueAsync(listingId)).Should().ContainSingle().Which.Durum.Should().Be("GONDERILDI");

            // Değişiklik yokken yeniden çalıştırma hiçbir şey göndermez
            await SyncOnceAsync();
            Mock.PricePushesOf(Channel).Count(u => u.ChannelProductCode == code).Should().Be(1);

            var history = await db.Api.GetFromJsonAsync<PagedResult<PriceHistoryDto>>($"/api/prices/history?listingId={listingId}", Json, Ct);
            history!.Items.Should().Contain(h => h.Type == "KANALA_GONDERILDI" && h.NewSale == 110.90m && h.OldSale == null);
            var skuHistory = await db.Api.GetFromJsonAsync<PagedResult<PriceHistoryDto>>($"/api/prices/history?skuId={skuId}", Json, Ct);
            skuHistory!.Items.Should().Contain(h => h.Type == "SKU_FIYAT" && h.NewSale == 100m && h.User != null);

            Mock.GetChannelStock(Channel, code).Should().Be(5, "stok hattı etkilenmez");
        }
        finally
        {
            await RestoreAsync();
        }
    }

    [Fact]
    public async Task Esik_asan_degisim_onaya_duser_onaylanan_gider_reddedilen_gitmez_ve_Depo_karar_veremez()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareAsync();
        try
        {
            var (skuId, listingId, code) = await PricedListingAsync(channelId, 100m);
            await SetCapabilityAsync(channelId, "ACIK");
            await SyncOnceAsync();
            Mock.PricesOf(Channel)[code].SalePrice.Should().Be(100m);

            // +%50 > %30 → otomatik gitmez, onay bekler
            await SetSkuPriceAsync(skuId, 150m);
            await SyncOnceAsync();
            Mock.PricesOf(Channel)[code].SalePrice.Should().Be(100m, "eşik aşıldı; kanal eski fiyatta kalır");
            var approval = await PendingApprovalAsync(channelId, listingId);
            approval.Should().NotBeNull();
            approval!.Reason.Should().Be(PriceApprovalReasons.Threshold);
            approval.OldSale.Should().Be(100m);
            approval.NewSale.Should().Be(150m);
            approval.ChangePercent.Should().Be(50m);

            using (var depo = await db.ApiHost.CreateClientAsAsync(Roles.Warehouse))
            {
                (await depo.PostAsync($"/api/prices/approvals/{approval.Id}/approve", null, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await depo.PutAsJsonAsync($"/api/prices/skus/{skuId}", new SkuPriceInput(null, 1m), Json, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            }

            var approved = await db.Api.PostAsync($"/api/prices/approvals/{approval.Id}/approve", null, Ct);
            approved.StatusCode.Should().Be(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
            (await approved.Content.ReadFromJsonAsync<PriceApprovalDto>(Json, Ct))!.DecidedBy.Should().NotBeNullOrEmpty();
            (await db.Api.PostAsync($"/api/prices/approvals/{approval.Id}/approve", null, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict, "karar bir kez verilir");
            await SyncOnceAsync();
            Mock.PricesOf(Channel)[code].SalePrice.Should().Be(150m, "onaylanan fiyat gider");

            // %100 artış → reddet → gitmez; aynı değer tekrar onay açmaz
            await SetSkuPriceAsync(skuId, 300m);
            await SyncOnceAsync();
            var second = await PendingApprovalAsync(channelId, listingId);
            second!.NewSale.Should().Be(300m);
            (await db.Api.PostAsync($"/api/prices/approvals/{second.Id}/reject", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
            await SyncOnceAsync();
            Mock.PricesOf(Channel)[code].SalePrice.Should().Be(150m, "reddedilen fiyat gitmez");
            (await PendingApprovalAsync(channelId, listingId)).Should().BeNull("reddedilen değer için yeniden onay açılmaz");

            // Eşik içindeki yeni değer doğrudan gider
            await SetSkuPriceAsync(skuId, 160m);
            await SyncOnceAsync();
            Mock.PricesOf(Channel)[code].SalePrice.Should().Be(160m);
        }
        finally
        {
            await RestoreAsync();
        }
    }

    [Fact]
    public async Task DENEME_yeteneginde_ilk_gonderim_dahil_her_degisiklik_onay_bekler()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareAsync();
        try
        {
            var (_, listingId, code) = await PricedListingAsync(channelId, 80m);
            await SetCapabilityAsync(channelId, "DENEME");
            await SyncOnceAsync();

            Mock.PricesOf(Channel).ContainsKey(code).Should().BeFalse();
            var approval = await PendingApprovalAsync(channelId, listingId);
            approval!.Reason.Should().Be(PriceApprovalReasons.Trial);
            approval.OldSale.Should().BeNull();

            (await db.Api.PostAsync($"/api/prices/approvals/{approval.Id}/approve", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
            await SyncOnceAsync();
            Mock.PricesOf(Channel)[code].SalePrice.Should().Be(80m);
        }
        finally
        {
            await RestoreAsync();
        }
    }

    [Fact]
    public async Task Taban_fiyatin_altina_inilmez_ve_ilan_fiyati_kurali_gecersiz_kilar()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareAsync();
        try
        {
            (await db.Api.PutAsJsonAsync($"/api/prices/channels/{channelId}/rule", new ChannelPriceRuleDto(0.5m, 0m, "YOK", 80m), Json, Ct))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            var (_, floorListing, _) = await PricedListingAsync(channelId, 100m);
            var (_, overrideListing, _) = await PricedListingAsync(channelId, 100m);
            (await db.Api.PutAsJsonAsync($"/api/prices/listings/{overrideListing}/override", new ListingPriceOverrideInput(95.5m), Json, Ct))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            var preview = await db.Api.GetFromJsonAsync<PricePreview>($"/api/prices/channels/{channelId}/preview", Json, Ct);
            var floorRow = preview!.Rows.Single(r => r.ListingId == floorListing);
            floorRow.NewSale.Should().Be(80m, "100 × 0,5 = 50 tabanın (80) altında → 80");
            floorRow.FloorApplied.Should().BeTrue();
            var overrideRow = preview.Rows.Single(r => r.ListingId == overrideListing);
            overrideRow.NewSale.Should().Be(95.5m);
            overrideRow.FromOverride.Should().BeTrue();

            // Geçersiz kural ve geçersiz ilan fiyatı reddedilir
            (await db.Api.PutAsJsonAsync($"/api/prices/channels/{channelId}/rule", new ChannelPriceRuleDto(0m, 0m, "YOK", null), Json, Ct))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await db.Api.PutAsJsonAsync($"/api/prices/channels/{channelId}/rule", new ChannelPriceRuleDto(1m, 0m, "BILINMEYEN", null), Json, Ct))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await db.Api.PutAsJsonAsync($"/api/prices/listings/{overrideListing}/override", new ListingPriceOverrideInput(-1m), Json, Ct))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            await RestoreAsync();
        }
    }

    [Fact]
    public async Task KDV_dogrulanmamis_kanalda_fiyat_yetenegi_acilamaz()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareAsync();
        try
        {
            Mock.PriceRules[Channel] = new PriceUpdateRule(true, null, true, false, "test: KDV dokümanda yok");

            var response = await db.Api.PutAsJsonAsync($"/api/channels/{channelId}/capabilities/FIYAT", new ChannelCapabilityUpdate("ACIK"), Json, Ct);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("KDV");

            var channels = await db.Api.GetFromJsonAsync<List<ChannelPriceInfo>>("/api/prices/channels", Json, Ct);
            channels!.Single(c => c.ChannelId == channelId).Should().Match<ChannelPriceInfo>(c => !c.CanEnable && c.VatIncluded == null && c.CapabilityState == "KAPALI");

            // Yetenek bir şekilde açık kalsa bile FiyatGonder KDV doğrulanmamış kanalda hiçbir şey talep etmez
            await using (var c = db.Open())
            {
                await c.ExecuteAsync("UPDATE dbo.ChannelCapability SET Durum = 'ACIK' WHERE ChannelId = @channelId AND Yetenek = 'FIYAT'", new { channelId });
            }
            (await PricePushAsync()).Should().Match<PushRunResult>(r => r.Skipped && r.SkipReason!.Contains("KDV"));
        }
        finally
        {
            await RestoreAsync();
        }
    }

    [Fact]
    public async Task Ayni_urunun_bedenlerine_ayni_fiyat_isteyen_kanalda_farkli_fiyatli_varyantlar_gonderilmez()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareAsync();
        try
        {
            Mock.PriceRules[Channel] = new PriceUpdateRule(true, true, true, SamePriceForProductVariants: true, "test: Beymen kuralı");
            var created = await db.Api.PostAsJsonAsync("/api/products", new CreateProductRequest("Varyant testi", "Test", null,
                [new SkuInput("V-" + Guid.NewGuid().ToString("N")[..10], null, null, "S"), new SkuInput("V-" + Guid.NewGuid().ToString("N")[..10], null, null, "M")]), Json, Ct);
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            var product = (await created.Content.ReadFromJsonAsync<ProductDto>(Json, Ct))!;
            var listings = new List<int>();
            foreach (var (sku, price) in product.Skus.Zip([100m, 110m]))
            {
                await SetSkuPriceAsync(sku.Id, price);
                var r = await db.Api.PutAsJsonAsync("/api/listings", new ListingInput(sku.Id, channelId, NewCode(), null, null), Json, Ct);
                listings.Add((await r.Content.ReadFromJsonAsync<ListingDto>(Json, Ct))!.Id);
            }

            var preview = await db.Api.GetFromJsonAsync<PricePreview>($"/api/prices/channels/{channelId}/preview", Json, Ct);
            preview!.Rows.Where(r => listings.Contains(r.ListingId)).Should().HaveCount(2)
                .And.OnlyContain(r => r.Status == PricePreviewStatus.Blocked && r.Problem!.Contains("aynı fiyat"));

            // Fiyatlar eşitlenince ikisi de gönderilebilir
            await SetSkuPriceAsync(product.Skus[1].Id, 100m);
            preview = await db.Api.GetFromJsonAsync<PricePreview>($"/api/prices/channels/{channelId}/preview", Json, Ct);
            preview!.Rows.Where(r => listings.Contains(r.ListingId)).Should().OnlyContain(r => r.Status == PricePreviewStatus.Change);
        }
        finally
        {
            await RestoreAsync();
        }
    }

    [Fact]
    public async Task Excel_ile_fiyat_yuklenir_hatali_satirlar_satir_numarasiyla_raporlanir()
    {
        db.SkipIfUnavailable();
        var skuA = await db.CreateSkuAsync();
        var skuB = await db.CreateSkuAsync();
        string codeA, codeB;
        await using (var c = db.Open())
        {
            codeA = await c.ExecuteScalarAsync<string>("SELECT SkuKodu FROM dbo.Sku WHERE Id = @skuA", new { skuA }) ?? "";
            codeB = await c.ExecuteScalarAsync<string>("SELECT SkuKodu FROM dbo.Sku WHERE Id = @skuB", new { skuB }) ?? "";
        }

        var file = ExcelImportApiTests.BuildWorkbook(["SKU Kodu", "Satış Fiyatı", "Liste Fiyatı"],
            [codeA, "1.249,90", "1499,90"],      // satır 2: geçerli (Türkçe biçim)
            [codeB, "200", "150"],               // satır 3: liste < satış
            ["YOK-" + Guid.NewGuid().ToString("N")[..8], "10", null], // satır 4: SKU yok
            [codeB, "abc", null]);              // satır 5: sayı değil

        var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(file);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(part, "file", "fiyat.xlsx");
        var response = await db.Api.PostAsync("/api/excel/prices/import?validateOnly=false", content, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var report = JsonSerializer.Deserialize<ImportReport>(body, Json)!;

        report.Updated.Should().Be(1);
        report.Errors.Select(e => e.Row).Distinct().Order().Should().Equal(3, 4, 5);
        report.Errors.Should().Contain(e => e.Row == 3 && e.Message.Contains("düşük olamaz"));
        report.Errors.Should().Contain(e => e.Row == 4 && e.Message.Contains("bulunamadı"));

        var price = await db.Api.GetFromJsonAsync<SkuPriceDto>($"/api/prices/skus/{skuA}", Json, Ct);
        price!.SalePrice.Should().Be(1249.90m);
        price.ListPrice.Should().Be(1499.90m);
        (await db.Api.GetFromJsonAsync<SkuPriceDto>($"/api/prices/skus/{skuB}", Json, Ct))!.SalePrice.Should().BeNull("hatalı satır uygulanmaz");

        // Aynı dosyayı tekrar yüklemek değişiklik yazmaz
        var again = new MultipartFormDataContent();
        var part2 = new ByteArrayContent(file);
        part2.Headers.ContentType = part.Headers.ContentType;
        again.Add(part2, "file", "fiyat.xlsx");
        var report2 = JsonSerializer.Deserialize<ImportReport>(await (await db.Api.PostAsync("/api/excel/prices/import", again, Ct)).Content.ReadAsStringAsync(Ct), Json)!;
        report2.Updated.Should().Be(0);
        report2.Unchanged.Should().Be(1);
    }

    // ------------------------------------------------------------------ ürün bazında kanal fiyatları (ADR-049)

    private async Task<SkuPriceOverview> SkuPricesAsync(string skuCode) =>
        (await db.Api.GetFromJsonAsync<PagedResult<SkuPriceOverview>>($"/api/prices/skus?search={Uri.EscapeDataString(skuCode)}", Json, Ct))!
            .Items.Single(i => i.SkuCode == skuCode);

    private async Task<HttpResponseMessage> SaveOverridesAsync(params ListingOverrideChange[] changes) =>
        await db.Api.PutAsJsonAsync("/api/prices/listings/overrides", new ListingOverridesRequest(changes), Json, Ct);

    private async Task<(int SkuId, string SkuCode, int Softcotton, int Trendyol)> SkuOnTwoChannelsAsync(int channelId)
    {
        var (skuId, listingId, _) = await PricedListingAsync(channelId, 100m, 120m);
        await using var c = db.Open();
        var trendyolId = await c.ExecuteScalarAsync<int>("SELECT Id FROM dbo.Channel WHERE Kod = 'TRENDYOL'");
        var response = await db.Api.PutAsJsonAsync("/api/listings", new ListingInput(skuId, trendyolId, NewCode(), null, null), Json, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var trendyolListing = (await response.Content.ReadFromJsonAsync<ListingDto>(Json, Ct))!.Id;
        var skuCode = await c.ExecuteScalarAsync<string>("SELECT SkuKodu FROM dbo.Sku WHERE Id = @skuId", new { skuId });
        return (skuId, skuCode!, listingId, trendyolListing);
    }

    [Fact]
    public async Task Ayni_skunun_kanal_fiyatlari_yan_yana_listelenir_ve_kanal_bazinda_ayri_guncellenir()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareAsync();
        try
        {
            var (_, skuCode, softcotton, trendyol) = await SkuOnTwoChannelsAsync(channelId);

            var before = await SkuPricesAsync(skuCode);
            before.SalePrice.Should().Be(100m);
            before.ListPrice.Should().Be(120m);
            before.Channels.Select(x => x.ChannelCode).Should().BeEquivalentTo([ChannelCodes.Softcotton, ChannelCodes.Trendyol]);
            before.Channels.Should().OnlyContain(x => x.Override == null && !x.Preview.FromOverride);
            before.Channels.Single(x => x.ListingId == softcotton).CapabilityState.Should().Be(CapabilityStates.Closed);

            // İki kanala farklı fiyat; tek istekte
            var saved = await SaveOverridesAsync(new ListingOverrideChange(softcotton, 129.90m), new ListingOverrideChange(trendyol, 109.50m));
            saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
            (await saved.Content.ReadFromJsonAsync<ListingOverridesResult>(Json, Ct))!.Changed.Should().Be(2);

            var after = await SkuPricesAsync(skuCode);
            var sc = after.Channels.Single(x => x.ListingId == softcotton);
            sc.Override.Should().Be(129.90m);
            sc.Preview.FromOverride.Should().BeTrue();
            sc.Preview.NewSale.Should().Be(129.90m);
            after.Channels.Single(x => x.ListingId == trendyol).Override.Should().Be(109.50m);
            after.SalePrice.Should().Be(100m, "SKU fiyatı değişmez; yalnızca ilgili kanalın ilanı");

            // Yalnızca bir kanalın elle fiyatı kaldırılır, diğeri kalır
            (await SaveOverridesAsync(new ListingOverrideChange(softcotton, null))).StatusCode.Should().Be(HttpStatusCode.OK);
            var last = await SkuPricesAsync(skuCode);
            last.Channels.Single(x => x.ListingId == softcotton).Override.Should().BeNull();
            last.Channels.Single(x => x.ListingId == trendyol).Override.Should().Be(109.50m);

            await using var c = db.Open();
            (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.PriceHistory WHERE ListingId IN (@softcotton, @trendyol) AND Tip = 'ILAN_FIYATI'",
                new { softcotton, trendyol })).Should().Be(3, "her değişiklik fiyat geçmişine yazılır");
        }
        finally
        {
            await RestoreAsync();
        }
    }

    [Fact]
    public async Task Toplu_ilan_fiyatinda_gecersiz_satir_varsa_hicbiri_yazilmaz_ve_yalnizca_yonetici_kaydeder()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareAsync();
        try
        {
            var (_, skuCode, softcotton, trendyol) = await SkuOnTwoChannelsAsync(channelId);

            var bad = await SaveOverridesAsync(new ListingOverrideChange(softcotton, 150m), new ListingOverrideChange(trendyol, -5m));
            bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await SkuPricesAsync(skuCode)).Channels.Should().OnlyContain(x => x.Override == null, "geçersiz satır varsa hiçbiri yazılmaz");

            using var depo = await db.ApiHost.CreateClientAsAsync(Roles.Warehouse);
            (await depo.PutAsJsonAsync("/api/prices/listings/overrides", new ListingOverridesRequest([new(softcotton, 150m)]), Json, Ct))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            await RestoreAsync();
        }
    }
}
