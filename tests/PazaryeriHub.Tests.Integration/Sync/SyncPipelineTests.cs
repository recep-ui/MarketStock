using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using PazaryeriHub.Adapters.Mock;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Application.Stock;
using PazaryeriHub.Application.Sync;
using PazaryeriHub.Infrastructure.BackgroundJobs;
using PazaryeriHub.Infrastructure.Sync;
using PazaryeriHub.Tests.Integration.Excel;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Sync;

/// <summary>
/// Aşama 4 kabul kriterleri: stok değişimi → ≤30 sn içinde mock kanalda güncel değer; hata durumunda 5 denemeden sonra HATA ve uyarı.
/// Ek: debounce, pay/üst limit, gönderim sırasında değişiklik, kısmi batch hatası, yeniden deneme, kanal yenileme, mutabakat/sapma raporu.
/// Testler SOFTCOTTON kanalını kullanır; her test kanalı temiz bir duruma getirir.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class SyncPipelineTests(TestDatabase db)
{
    private const string Channel = ChannelCodes.Softcotton;
    private static readonly JsonSerializerOptions Json = ExcelImportApiTests.Json;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MockChannelStore Mock => db.Services.GetRequiredService<MockChannelStore>();

    // ------------------------------------------------------------------ hazırlık / yardımcılar

    /// <summary>
    /// Kanalı temiz duruma getirir: eski test eşleştirmeleri pasif, bekleyen kuyruk kayıtları kapatılır, outbox birikimi işlenmiş sayılır
    /// (test veritabanında önceki testlerin binlerce olayı birikir), kanal aktif ve pay %100 yapılır.
    /// </summary>
    private async Task<int> PrepareChannelAsync(decimal share = 100, string? settings = null)
    {
        await using var c = db.Open();
        var channelId = await c.ExecuteScalarAsync<int>("SELECT Id FROM dbo.Channel WHERE Kod = @Channel", new { Channel });
        await c.ExecuteAsync("""
            UPDATE dbo.Outbox SET IslendiAt = SYSUTCDATETIME() WHERE IslendiAt IS NULL;
            UPDATE q SET Durum = 'HATA', Hata = N'test hazırlığı' FROM dbo.SyncQueue q JOIN dbo.ChannelListing l ON l.Id = q.ListingId
             WHERE l.ChannelId = @channelId AND q.Durum = 'BEKLIYOR';
            UPDATE dbo.ChannelListing SET Durum = 'PASIF' WHERE ChannelId = @channelId;
            UPDATE dbo.Channel SET Aktif = 1, PayYuzde = @share, Ayarlar = @settings WHERE Id = @channelId;
            """, new { channelId, share, settings });
        Mock.FailPush.TryRemove(Channel, out _);
        Mock.FailingItems.Clear();
        return channelId;
    }

    private async Task RestoreChannelAsync()
    {
        await using var c = db.Open();
        await c.ExecuteAsync("UPDATE dbo.Channel SET Aktif = 0, PayYuzde = 100, Ayarlar = NULL WHERE Kod = @Channel", new { Channel });
        Mock.FailPush.TryRemove(Channel, out _);
        Mock.FailingItems.Clear();
    }

    private async Task<(int SkuId, int ListingId, string Code)> SkuWithListingAsync(int channelId, int physical, bool viaApi = true)
    {
        var skuId = await db.CreateSkuAsync();
        if (physical > 0)
        {
            await db.Ledger.ReceiveAsync(new(skuId, db.DefaultWarehouseId, physical), Ct);
        }
        else if (physical == 0)
        {
            // Sayımla 0: stok "bilinen 0" (bakiye satırı var) → kanala 0 gönderilir.
            // Hiç stok girilmemiş SKU (physical < 0 ile) gönderilmez (ADR-020).
            await db.Ledger.AdjustToCountAsync(new(skuId, db.DefaultWarehouseId, 0), Ct);
        }
        var code = "SC-" + Guid.NewGuid().ToString("N")[..12];
        if (viaApi)
        {
            var response = await db.Api.PutAsJsonAsync("/api/listings", new ListingInput(skuId, channelId, code, null, null), Json, Ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return (skuId, (await response.Content.ReadFromJsonAsync<ListingDto>(Json, Ct))!.Id, code);
        }
        await using var c = db.Open();
        var listingId = await c.ExecuteScalarAsync<int>(
            "INSERT dbo.ChannelListing (SkuId, ChannelId, KanalUrunKodu) OUTPUT inserted.Id VALUES (@skuId, @channelId, @code)", new { skuId, channelId, code });
        return (skuId, listingId, code);
    }

    private async Task<T> RunAsync<T>(Func<IServiceProvider, Task<T>> run)
    {
        using var scope = db.Services.CreateScope();
        return await run(scope.ServiceProvider);
    }

    private Task<OutboxRunResult> OutboxAsync() => RunAsync(sp => sp.GetRequiredService<OutboxProcessorJob>().RunAsync(Ct));
    private Task<PushRunResult> PushAsync() => RunAsync(sp => sp.GetRequiredService<StockPushJob>().RunAsync(Channel, Ct));
    private Task<BatchCheckRunResult> BatchCheckAsync() => RunAsync(sp => sp.GetRequiredService<BatchCheckJob>().RunAsync(Ct));

    private async Task SyncOnceAsync()
    {
        await OutboxAsync();
        await PushAsync();
        await BatchCheckAsync();
    }

    private async Task<List<QueueRow>> QueueAsync(int listingId)
    {
        await using var c = db.Open();
        return (await c.QueryAsync<QueueRow>(
            "SELECT Id, Durum, Deneme, Hata, BatchId, GonderilenDeger, SonrakiDeneme FROM dbo.SyncQueue WHERE ListingId = @listingId ORDER BY Id",
            new { listingId })).ToList();
    }

    private async Task<int?> LastSentAsync(int listingId)
    {
        await using var c = db.Open();
        return await c.ExecuteScalarAsync<int?>("SELECT SonGonderilenStok FROM dbo.ChannelListing WHERE Id = @listingId", new { listingId });
    }

    private sealed class QueueRow
    {
        public long Id { get; init; }
        public string Durum { get; init; } = "";
        public int Deneme { get; init; }
        public string? Hata { get; init; }
        public string? BatchId { get; init; }
        public int? GonderilenDeger { get; init; }
        public DateTime? SonrakiDeneme { get; init; }
    }

    // ------------------------------------------------------------------ hat (job'lar doğrudan)

    [Fact]
    public async Task Stok_degisimi_kanala_gonderilir_onaylanir_ve_debounce_ile_tek_kayit_olusur()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareChannelAsync();
        try
        {
            var (skuId, listingId, code) = await SkuWithListingAsync(channelId, 10);

            (await OutboxAsync()).Enqueued.Should().BeGreaterThanOrEqualTo(1);
            (await QueueAsync(listingId)).Should().ContainSingle().Which.Durum.Should().Be("BEKLIYOR");

            var push = await PushAsync();
            push.Sent.Should().Be(1);
            Mock.GetChannelStock(Channel, code).Should().Be(10);
            (await QueueAsync(listingId)).Single().BatchId.Should().StartWith("mock-");

            await BatchCheckAsync();
            (await QueueAsync(listingId)).Single().Durum.Should().Be("GONDERILDI");
            (await LastSentAsync(listingId)).Should().Be(10);

            // Üç değişiklik art arda → tek bekleyen kayıt (debounce) → en güncel değer gider
            await db.Ledger.ReserveAsync(new(skuId, db.DefaultWarehouseId, 1), Ct);
            await db.Ledger.ReserveAsync(new(skuId, db.DefaultWarehouseId, 1), Ct);
            await db.Ledger.ReceiveAsync(new(skuId, db.DefaultWarehouseId, 5), Ct);
            await OutboxAsync();
            (await QueueAsync(listingId)).Count(q => q.Durum == "BEKLIYOR").Should().Be(1);

            await PushAsync();
            await BatchCheckAsync();
            Mock.GetChannelStock(Channel, code).Should().Be(13);
            (await LastSentAsync(listingId)).Should().Be(13);
            (await QueueAsync(listingId)).Should().HaveCount(2).And.OnlyContain(q => q.Durum == "GONDERILDI");
        }
        finally
        {
            await RestoreChannelAsync();
        }
    }

    [Fact]
    public async Task Kanal_payi_ve_ust_limit_uygulanir()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareChannelAsync(share: 50, settings: """{"stokUstLimit":4}""");
        try
        {
            var (skuA, listingA, codeA) = await SkuWithListingAsync(channelId, 10);
            await db.Ledger.SetSafetyStockAsync(new(skuA, db.DefaultWarehouseId, 1), Ct); // satılabilir 9 → %50 = 4 → limit 4
            var (_, listingB, codeB) = await SkuWithListingAsync(channelId, 5);            // satılabilir 5 → %50 = 2 (FLOOR)
            var (_, listingC, codeC) = await SkuWithListingAsync(channelId, 0);            // 0 → 0

            await SyncOnceAsync();

            Mock.GetChannelStock(Channel, codeA).Should().Be(4);
            Mock.GetChannelStock(Channel, codeB).Should().Be(2);
            Mock.GetChannelStock(Channel, codeC).Should().Be(0);
            (await LastSentAsync(listingA)).Should().Be(4);
            (await LastSentAsync(listingB)).Should().Be(2);
            (await LastSentAsync(listingC)).Should().Be(0);
        }
        finally
        {
            await RestoreChannelAsync();
        }
    }

    /// <summary>ADR-042: ilan stok kuralı panelden değişince outbox → kuyruk → kanal; sonuç defteri asla aşmaz.</summary>
    [Fact]
    public async Task Ilan_stok_kurali_kanala_gider_ve_defteri_asmaz()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareChannelAsync();
        try
        {
            var (_, listing, code) = await SkuWithListingAsync(channelId, 10);
            await SyncOnceAsync();
            Mock.GetChannelStock(Channel, code).Should().Be(10);

            // Üst limit 3 → kanala 3
            var r = await db.Api.PutAsJsonAsync($"/api/stock/listings/{listing}/rule", new ListingStockRuleInput("UST_LIMIT", 3), Json, Ct);
            r.StatusCode.Should().Be(HttpStatusCode.OK);
            (await r.Content.ReadFromJsonAsync<ListingStockRuleDto>(Json, Ct))!.Target.Should().Be(3);
            await SyncOnceAsync();
            Mock.GetChannelStock(Channel, code).Should().Be(3);

            // Sabit 25 istense de defterde 10 var → 10
            r = await db.Api.PutAsJsonAsync($"/api/stock/listings/{listing}/rule", new ListingStockRuleInput("SABIT", 25), Json, Ct);
            (await r.Content.ReadFromJsonAsync<ListingStockRuleDto>(Json, Ct))!.Should().Match<ListingStockRuleDto>(x => x.Type == "SABIT" && x.Value == 25 && x.Target == 10);
            await SyncOnceAsync();
            Mock.GetChannelStock(Channel, code).Should().Be(10);

            // Geçersiz kural reddedilir; kural kaldırılınca pay ile hesaplanan değer döner
            (await db.Api.PutAsJsonAsync($"/api/stock/listings/{listing}/rule", new ListingStockRuleInput("YUZDE", 5), Json, Ct))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await db.Api.PutAsJsonAsync($"/api/stock/listings/{listing}/rule", new ListingStockRuleInput("SABIT", -1), Json, Ct))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
            r = await db.Api.PutAsJsonAsync($"/api/stock/listings/{listing}/rule", new ListingStockRuleInput(null, null), Json, Ct);
            (await r.Content.ReadFromJsonAsync<ListingStockRuleDto>(Json, Ct))!.Should().Match<ListingStockRuleDto>(x => x.Type == null && x.Target == 10);
        }
        finally
        {
            await RestoreChannelAsync();
        }
    }

    [Fact]
    public async Task Stogu_hic_girilmemis_sku_kanala_gonderilmez_stok_girilince_gonderilir()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareChannelAsync();
        try
        {
            var (skuId, listingId, code) = await SkuWithListingAsync(channelId, physical: -1);

            await SyncOnceAsync();
            Mock.GetChannelStock(Channel, code).Should().BeNull("stoğu hiç girilmemiş SKU pazaryerindeki stoğu 0'a çekmemeli");
            (await QueueAsync(listingId)).Should().BeEmpty();

            await db.Ledger.ReceiveAsync(new(skuId, db.DefaultWarehouseId, 3), Ct);
            await SyncOnceAsync();
            Mock.GetChannelStock(Channel, code).Should().Be(3);
        }
        finally
        {
            await RestoreChannelAsync();
        }
    }

    [Fact]
    public async Task Gonderim_surerken_gelen_degisiklik_kaybolmaz()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareChannelAsync();
        try
        {
            var (skuId, listingId, code) = await SkuWithListingAsync(channelId, 8);
            await OutboxAsync();
            await PushAsync(); // 8 gönderildi, batch sonucu bekleniyor

            await db.Ledger.ReserveAsync(new(skuId, db.DefaultWarehouseId, 3), Ct);
            await OutboxAsync();
            (await QueueAsync(listingId)).Should().ContainSingle("gönderimdeki kayıt bekleyen sayılır; ikinci kayıt açılmaz");

            await BatchCheckAsync(); // 8 onaylandı; güncel değer 5 farklı → yeniden kuyruğa
            var queue = await QueueAsync(listingId);
            queue.Should().HaveCount(2);
            queue[0].Should().Match<QueueRow>(q => q.Durum == "GONDERILDI" && q.GonderilenDeger == 8);
            queue[1].Durum.Should().Be("BEKLIYOR");

            await PushAsync();
            await BatchCheckAsync();
            Mock.GetChannelStock(Channel, code).Should().Be(5);
            (await LastSentAsync(listingId)).Should().Be(5);
        }
        finally
        {
            await RestoreChannelAsync();
        }
    }

    // ------------------------------------------------------------------ Kabul: 5 denemede HATA

    [Fact]
    public async Task Bes_basarisiz_denemeden_sonra_HATA_olur_uyari_gorunur_yeniden_denenince_gonderilir()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareChannelAsync();
        try
        {
            var (_, listingId, code) = await SkuWithListingAsync(channelId, 6);
            await OutboxAsync();
            Mock.FailPush[Channel] = new HttpRequestException("Servis geçici olarak kullanılamıyor", null, HttpStatusCode.ServiceUnavailable);

            var expectedDelays = new[] { 30, 60, 120, 240 };
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                var before = DateTime.UtcNow;
                var result = await PushAsync();
                result.Claimed.Should().Be(1, because: $"{attempt}. deneme");

                var row = (await QueueAsync(listingId)).Single();
                row.Deneme.Should().Be(attempt);
                row.Hata.Should().Contain("HTTP 503");
                if (attempt < 5)
                {
                    row.Durum.Should().Be("BEKLIYOR");
                    row.SonrakiDeneme.Should().BeCloseTo(before.AddSeconds(expectedDelays[attempt - 1]), TimeSpan.FromSeconds(10));
                    (await PushAsync()).Claimed.Should().Be(0, "geri çekilme süresi dolmadan yeniden gönderilmez");

                    // Zamanın geçtiğini taklit et
                    await using var c = db.Open();
                    await c.ExecuteAsync("UPDATE dbo.SyncQueue SET SonrakiDeneme = DATEADD(SECOND, -1, SYSUTCDATETIME()) WHERE Id = @id", new { id = row.Id });
                }
                else
                {
                    row.Durum.Should().Be("HATA", "5 denemeden sonra HATA");
                    row.SonrakiDeneme.Should().BeNull();
                }
            }

            (await PushAsync()).Claimed.Should().Be(0, "HATA kaydı artık gönderilmez");
            Mock.GetChannelStock(Channel, code).Should().BeNull();

            // Uyarı: Senkron Merkezi özeti ve hata listesi
            var summary = await db.Api.GetFromJsonAsync<List<SyncSummaryRow>>("/api/sync/summary", Json, Ct);
            summary!.Single(s => s.ChannelCode == Channel).Error.Should().BeGreaterThanOrEqualTo(1);
            var errors = await db.Api.GetFromJsonAsync<PagedResult<SyncQueueItem>>($"/api/sync/queue?channelId={channelId}&status=HATA", Json, Ct);
            var failed = errors!.Items.Should().ContainSingle(i => i.ListingId == listingId).Subject;
            failed.Error.Should().Contain("Kanal hata döndü (HTTP 503)");

            // Kanal düzelince panelden "yeniden dene"
            Mock.FailPush.TryRemove(Channel, out _);
            var retried = await db.Api.PostAsync($"/api/sync/queue/{failed.Id}/retry", null, Ct);
            retried.StatusCode.Should().Be(HttpStatusCode.OK);
            (await retried.Content.ReadFromJsonAsync<SyncQueueItem>(Json, Ct))!.Should().Match<SyncQueueItem>(i => i.Status == "BEKLIYOR" && i.Attempts == 0);

            await PushAsync();
            await BatchCheckAsync();
            Mock.GetChannelStock(Channel, code).Should().Be(6);
            (await QueueAsync(listingId)).Single().Durum.Should().Be("GONDERILDI");

            (await db.Api.PostAsync($"/api/sync/queue/{failed.Id}/retry", null, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict, "yalnızca HATA kaydı yeniden denenir");
        }
        finally
        {
            await RestoreChannelAsync();
        }
    }

    [Fact]
    public async Task Kismi_batch_hatasinda_yalnizca_hatali_kalem_yeniden_denenir()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareChannelAsync();
        try
        {
            var (_, okListing, okCode) = await SkuWithListingAsync(channelId, 3);
            var (_, badListing, badCode) = await SkuWithListingAsync(channelId, 4);
            Mock.FailingItems[badCode] = "Ürün kanalda onay bekliyor";

            await OutboxAsync();
            (await PushAsync()).Sent.Should().Be(2);
            await BatchCheckAsync();

            (await QueueAsync(okListing)).Single().Durum.Should().Be("GONDERILDI");
            var bad = (await QueueAsync(badListing)).Single();
            bad.Should().Match<QueueRow>(q => q.Durum == "BEKLIYOR" && q.BatchId == null && q.Deneme == 1 && q.SonrakiDeneme != null);
            bad.Hata.Should().Be("Ürün kanalda onay bekliyor");
            Mock.GetChannelStock(Channel, okCode).Should().Be(3);
            Mock.GetChannelStock(Channel, badCode).Should().BeNull();
        }
        finally
        {
            await RestoreChannelAsync();
        }
    }

    [Fact]
    public async Task Pasif_eslestirme_ve_pasif_kanal_kuyruga_alinmaz()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareChannelAsync();
        try
        {
            var (skuId, listingId, _) = await SkuWithListingAsync(channelId, 2, viaApi: false);
            await using (var c = db.Open())
            {
                await c.ExecuteAsync("UPDATE dbo.ChannelListing SET Durum = 'PASIF' WHERE Id = @listingId", new { listingId });
            }
            await db.Ledger.ReceiveAsync(new(skuId, db.DefaultWarehouseId, 1), Ct);
            await OutboxAsync();
            (await QueueAsync(listingId)).Should().BeEmpty("pasif eşleştirme");

            await using (var c = db.Open())
            {
                await c.ExecuteAsync("UPDATE dbo.ChannelListing SET Durum = 'AKTIF' WHERE Id = @listingId; UPDATE dbo.Channel SET Aktif = 0 WHERE Id = @channelId;",
                    new { listingId, channelId });
            }
            await db.Ledger.ReceiveAsync(new(skuId, db.DefaultWarehouseId, 1), Ct);
            await OutboxAsync();
            (await QueueAsync(listingId)).Should().BeEmpty("pasif kanal");
            (await PushAsync()).Skipped.Should().BeTrue();
        }
        finally
        {
            await RestoreChannelAsync();
        }
    }

    [Fact]
    public async Task Kanal_payi_degisince_tum_eslestirmeler_yeniden_gonderilir()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareChannelAsync();
        try
        {
            var (_, listingId, code) = await SkuWithListingAsync(channelId, 10);
            await SyncOnceAsync();
            Mock.GetChannelStock(Channel, code).Should().Be(10);

            var update = await db.Api.PutAsJsonAsync($"/api/channels/{channelId}", new ChannelUpdate("softcotton.com.tr", 30, true, null), Json, Ct);
            update.StatusCode.Should().Be(HttpStatusCode.OK);
            await SyncOnceAsync();

            Mock.GetChannelStock(Channel, code).Should().Be(3);
            (await LastSentAsync(listingId)).Should().Be(3);
        }
        finally
        {
            await RestoreChannelAsync();
        }
    }

    [Fact]
    public async Task Takili_kalan_talep_kurtarilir()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareChannelAsync();
        try
        {
            var (_, listingId, _) = await SkuWithListingAsync(channelId, 1, viaApi: false);
            await using (var c = db.Open())
            {
                await c.ExecuteAsync("""
                    INSERT dbo.SyncQueue (ListingId, Tip, Durum, Deneme, BatchId, SonDeneme)
                    VALUES (@listingId, 'STOK', 'BEKLIYOR', 1, 'claim:coktu', DATEADD(MINUTE, -20, SYSUTCDATETIME()))
                    """, new { listingId });
            }

            (await BatchCheckAsync()).RecoveredClaims.Should().BeGreaterThanOrEqualTo(1);
            (await QueueAsync(listingId)).Single().BatchId.Should().BeNull();
        }
        finally
        {
            await RestoreChannelAsync();
        }
    }

    // ------------------------------------------------------------------ Mutabakat

    [Fact]
    public async Task Mutabakat_kanal_stogunu_kaydeder_ve_sapmalari_raporlar()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareChannelAsync();
        try
        {
            var (_, drifted, driftedCode) = await SkuWithListingAsync(channelId, 10);
            var (_, inSync, _) = await SkuWithListingAsync(channelId, 4);
            await SyncOnceAsync();
            var (_, missing, _) = await SkuWithListingAsync(channelId, 2, viaApi: false); // kanala hiç gönderilmedi

            Mock.SetChannelStock(Channel, driftedCode, 7); // kanalda değer bizim dışımızda değişti

            var result = await RunAsync(sp => sp.GetRequiredService<ReconciliationJob>().RunAsync(Channel, Ct));
            result.Should().BeEquivalentTo(new { Skipped = false, Listings = 3, Matched = 2, Missing = 1, Deviations = 1 });

            var report = (await db.Api.GetFromJsonAsync<List<ReconciliationRow>>($"/api/sync/reconciliation?channelId={channelId}&onlyDeviations=true", Json, Ct))!;
            report.Select(r => r.ListingId).Should().BeEquivalentTo([drifted, missing]);
            report.Single(r => r.ListingId == drifted).Should().Match<ReconciliationRow>(r => r.Expected == 10 && r.ChannelStock == 7 && r.Deviation == -3);
            report.Single(r => r.ListingId == missing).ChannelStock.Should().BeNull();

            var full = await db.Api.GetFromJsonAsync<List<ReconciliationRow>>($"/api/sync/reconciliation?channelId={channelId}", Json, Ct);
            full!.Single(r => r.ListingId == inSync).Deviation.Should().Be(0);

            await using var c = db.Open();
            (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.ChannelStockSnapshot WHERE ListingId IN @ids", new { ids = new[] { drifted, inSync } }))
                .Should().Be(2);

            var enqueue = await db.Api.PostAsync($"/api/sync/reconciliation/{Channel.ToLowerInvariant()}/run", null, Ct);
            enqueue.StatusCode.Should().Be(HttpStatusCode.Accepted, "istek üzerine mutabakat Hangfire job'ı olarak kuyruğa alınır");
        }
        finally
        {
            await RestoreChannelAsync();
        }
    }

    [Fact]
    public async Task Son_cekimde_kanalda_bulunamayan_ilan_eski_degeri_gostermez_ve_sapma_sayilmaz()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareChannelAsync();
        try
        {
            var (skuId, gone, goneCode) = await SkuWithListingAsync(channelId, 10);
            await SyncOnceAsync();
            Mock.SetChannelStock(Channel, goneCode, 100); // ilk çekimde kanalda 100 (sapma)

            var first = await RunAsync(sp => sp.GetRequiredService<ReconciliationJob>().RunAsync(Channel, Ct));
            first.Should().BeEquivalentTo(new { Listings = 1, Matched = 1, Missing = 0, Deviations = 1 });

            Mock.StockOf(Channel).TryRemove(goneCode, out _); // ilan kanaldan kalktı
            var second = await RunAsync(sp => sp.GetRequiredService<ReconciliationJob>().RunAsync(Channel, Ct));
            second.Should().BeEquivalentTo(new { Listings = 1, Matched = 0, Missing = 1, Deviations = 0 });

            // Senkron Merkezi: eski 100 değil, "kanalda yok"; sapma değeri yok
            var row = (await db.Api.GetFromJsonAsync<List<ReconciliationRow>>($"/api/sync/reconciliation?channelId={channelId}", Json, Ct))!
                .Single(r => r.ListingId == gone);
            row.Should().Match<ReconciliationRow>(r => r.ChannelStock == null && r.MissingInChannel && r.Deviation == null && r.FetchedAtUtc != null);

            // Stok Listesi / SKU detay: hücre "kanalda yok", sapma sayılmaz
            var detail = (await db.Api.GetFromJsonAsync<PazaryeriHub.Application.Panel.SkuDetail>($"/api/skus/{skuId}/detail", Json, Ct))!;
            var cell = detail.Channels.Single(c => c.ListingId == gone);
            cell.Should().Match<PazaryeriHub.Application.Panel.ChannelStockCell>(c => c.ChannelStock == null && c.MissingInChannel && !c.HasDeviation);
            detail.HasDeviation.Should().BeFalse();

            // Kanal sekmesi: "kanalda yok" sayılır, "hiç çekilmemiş" sayılmaz
            var list = (await db.Api.GetFromJsonAsync<PazaryeriHub.Application.Panel.ChannelStockList>(
                $"/api/stock-overview/channels/{channelId}?search={goneCode}&pageSize=500", Json, Ct))!;
            // Kalıcı test veritabanında ilan sayısı sayfa sınırını aşabilir: ilan kodu ile aranır (yalnızca ilk sayfaya güvenilmez)
            list.Rows.Items.Single(r => r.ListingId == gone).Should()
                .Match<PazaryeriHub.Application.Panel.ChannelStockListRow>(r => r.MissingInChannel && !r.HasDeviation && r.ChannelStock == null);
            list.MissingCount.Should().BeGreaterThanOrEqualTo(1);

            // Kanalda yeniden görünürse değer geri gelir
            Mock.SetChannelStock(Channel, goneCode, 10);
            (await RunAsync(sp => sp.GetRequiredService<ReconciliationJob>().RunAsync(Channel, Ct)))
                .Should().BeEquivalentTo(new { Matched = 1, Missing = 0, Deviations = 0 });
            (await db.Api.GetFromJsonAsync<List<ReconciliationRow>>($"/api/sync/reconciliation?channelId={channelId}", Json, Ct))!
                .Single(r => r.ListingId == gone).Should().Match<ReconciliationRow>(r => r.ChannelStock == 10 && !r.MissingInChannel);
        }
        finally
        {
            await RestoreChannelAsync();
        }
    }

    [Fact]
    public async Task Pasif_kanalda_mutabakat_istegi_acik_hatayla_reddedilir()
    {
        db.SkipIfUnavailable();
        await RestoreChannelAsync(); // kanal pasif

        var response = await db.Api.PostAsync($"/api/sync/reconciliation/{Channel.ToLowerInvariant()}/run", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, "pasif kanalda job hiçbir şey yapmaz; kullanıcı 'çekiliyor' sanmamalı");
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("pasif");
    }

    // ------------------------------------------------------------------ Kabul: ≤30 sn (gerçek Hangfire zamanlaması)

    [Fact]
    public async Task Uctan_uca_stok_degisimi_30_saniye_icinde_mock_kanala_ulasir()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareChannelAsync();

        // Ayrı bir uygulama: üretimdeki zamanlamalarla (10 sn) tekrarlayan job'lar açık, "default" kuyruğunu işler.
        var e2e = new ApiFactory(db.ConnectionString!, new Dictionary<string, string?>
        {
            ["Hangfire:Queues:0"] = "default",
            ["Senkron:OutboxCron"] = "*/10 * * * * *",
            ["Senkron:PushCron"] = "*/10 * * * * *",
            ["Senkron:BatchCheckCron"] = "*/10 * * * * *"
        });
        try
        {
            using var client = await e2e.CreateClientAsAsync(PazaryeriHub.Application.Auth.Roles.Admin);
            var store = e2e.Services.GetRequiredService<MockChannelStore>();
            var skuId = await db.CreateSkuAsync();
            await db.Ledger.ReceiveAsync(new(skuId, db.DefaultWarehouseId, 10), Ct);
            var code = "E2E-" + Guid.NewGuid().ToString("N")[..12];
            (await client.PutAsJsonAsync("/api/listings", new ListingInput(skuId, channelId, code, null, null), Json, Ct))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            var first = await WaitForAsync(() => store.GetChannelStock(Channel, code) == 10, TimeSpan.FromSeconds(45));
            first.Should().BeLessThan(TimeSpan.FromSeconds(30), "ilk eşleştirme stoğu 30 sn içinde kanala gitmeli");

            await db.Ledger.ReserveAsync(new(skuId, db.DefaultWarehouseId, 3), Ct);
            var change = await WaitForAsync(() => store.GetChannelStock(Channel, code) == 7, TimeSpan.FromSeconds(45));

            TestContext.Current.TestOutputHelper?.WriteLine($"Uçtan uca gecikme: ilk eşleştirme {first.TotalSeconds:F1} sn, stok değişimi {change.TotalSeconds:F1} sn");
            change.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(30), "stok değişimi ≤30 sn içinde mock kanalda görünmeli (kabul kriteri)");
        }
        finally
        {
            var jobs = e2e.Services.GetRequiredService<IRecurringJobManager>();
            foreach (var code in ChannelCodes.All)
            {
                jobs.RemoveIfExists(StockPushJob.RecurringJobId(code));
                jobs.RemoveIfExists(ReconciliationJob.RecurringJobId(code));
                jobs.RemoveIfExists(OrderFetchJob.RecurringJobId(code));
            }
            jobs.RemoveIfExists(OutboxProcessorJob.RecurringJobId);
            jobs.RemoveIfExists(BatchCheckJob.RecurringJobId);
            await e2e.DisposeAsync();
            await RestoreChannelAsync();
        }
    }

    private static async Task<TimeSpan> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
            {
                throw new TimeoutException($"{timeout.TotalSeconds} sn içinde koşul sağlanmadı.");
            }
            await Task.Delay(250, Ct);
        }
        return sw.Elapsed;
    }

    // ------------------------------------------------------------------ çoklu ilan + düşük stok eşiği (ADR-023)

    private async Task<int> AddListingAsync(int skuId, int channelId, string? code, string? barcode = null)
    {
        var response = await db.Api.PutAsJsonAsync("/api/listings", new ListingInput(skuId, channelId, code, barcode, null), Json, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ListingDto>(Json, Ct))!.Id;
    }

    private static string NewCode() => "SC-" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task Ayni_skunun_iki_ilani_ortak_havuzdan_tam_stok_alir_stok_bitince_ikisine_0_gider()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareChannelAsync();
        try
        {
            var (skuId, listingA, codeA) = await SkuWithListingAsync(channelId, 6);
            var codeB = NewCode();
            var listingB = await AddListingAsync(skuId, channelId, codeB);
            listingB.Should().NotBe(listingA, "aynı SKU'nun farklı kanal kodu ikinci ilandır");

            // Debounce ilan bazlı: her ilan için tek bekleyen kayıt, ikinci işleme yeni kayıt açmaz
            await OutboxAsync();
            await OutboxAsync();
            (await QueueAsync(listingA)).Should().ContainSingle(q => q.Durum == "BEKLIYOR");
            (await QueueAsync(listingB)).Should().ContainSingle(q => q.Durum == "BEKLIYOR");

            await PushAsync();
            await BatchCheckAsync();
            Mock.GetChannelStock(Channel, codeA).Should().Be(6);
            Mock.GetChannelStock(Channel, codeB).Should().Be(6, "tam ortak havuz: stok ilanlar arasında bölünmez");

            await db.Ledger.ReserveAsync(new(skuId, db.DefaultWarehouseId, 6), Ct);
            await SyncOnceAsync();
            Mock.GetChannelStock(Channel, codeA).Should().Be(0);
            Mock.GetChannelStock(Channel, codeB).Should().Be(0);
        }
        finally
        {
            await RestoreChannelAsync();
        }
    }

    [Fact]
    public async Task Coklu_ilanda_kismi_hata_yalnizca_hatali_ilani_yeniden_denemeye_birakir()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareChannelAsync();
        try
        {
            var (skuId, listingA, codeA) = await SkuWithListingAsync(channelId, 4);
            var codeB = NewCode();
            var listingB = await AddListingAsync(skuId, channelId, codeB);
            Mock.FailingItems[codeB] = "Kanal: ilan kilitli";

            await SyncOnceAsync();

            Mock.GetChannelStock(Channel, codeA).Should().Be(4);
            Mock.GetChannelStock(Channel, codeB).Should().BeNull();
            (await QueueAsync(listingA)).Last().Durum.Should().Be("GONDERILDI");
            (await QueueAsync(listingB)).Last().Should().Match<QueueRow>(q => q.Durum == "BEKLIYOR" && q.Hata!.Contains("kilitli"));
        }
        finally
        {
            await RestoreChannelAsync();
        }
    }

    [Fact]
    public async Task Ikinci_ilanin_barkoduyla_gelen_siparis_ayni_skudan_rezerve_eder()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareChannelAsync();
        try
        {
            var (skuId, _, _) = await SkuWithListingAsync(channelId, 5);
            var barcodeB = "BRK-" + Guid.NewGuid().ToString("N")[..12];
            await AddListingAsync(skuId, channelId, null, barcodeB);

            using var scope = db.Services.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<IOrderIngestionService>().IngestAsync(Channel,
                new NormalizedOrder("CI-" + Guid.NewGuid().ToString("N")[..10], DateTime.UtcNow, PazaryeriHub.Domain.Orders.OrderStatus.Yeni, 100m,
                    [new NormalizedOrderLine(null, barcodeB, null, 2, 50m)]), Ct);

            result.InsufficientStock.Should().BeFalse();
            (await db.GetBalanceAsync(skuId))!.Rezerve.Should().Be(2, "ikinci ilanın barkodu SKU'ya çözülür (eskiden yok sayılan ilanın siparişi rezerve etmiyordu)");
        }
        finally
        {
            await RestoreChannelAsync();
        }
    }

    [Fact]
    public async Task Mutabakat_ayni_skunun_iki_ilanini_ayri_degerlendirir()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareChannelAsync();
        try
        {
            var (skuId, _, _) = await SkuWithListingAsync(channelId, 3);
            var codeB = NewCode();
            var listingB = await AddListingAsync(skuId, channelId, codeB);
            await SyncOnceAsync();
            Mock.SetChannelStock(Channel, codeB, 9);

            var result = await RunAsync(sp => sp.GetRequiredService<ReconciliationJob>().RunAsync(Channel, Ct));

            result.Should().BeEquivalentTo(new { Listings = 2, Matched = 2, Missing = 0, Deviations = 1 });
            var report = (await db.Api.GetFromJsonAsync<List<ReconciliationRow>>($"/api/sync/reconciliation?channelId={channelId}&onlyDeviations=true", Json, Ct))!;
            report.Should().ContainSingle().Which.ListingId.Should().Be(listingB);
        }
        finally
        {
            await RestoreChannelAsync();
        }
    }

    [Fact]
    public async Task Genel_dusuk_stok_esiginde_kanallara_0_gider_esik_degisince_yeniden_hesaplanir()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareChannelAsync();
        var settings = (await db.Api.GetFromJsonAsync<PazaryeriHub.Application.Panel.AppSettingsDto>("/api/settings", Json, Ct))!;
        try
        {
            var (_, listingId, code) = await SkuWithListingAsync(channelId, 5);
            await SyncOnceAsync();
            Mock.GetChannelStock(Channel, code).Should().Be(5);

            var saved = await db.Api.PutAsJsonAsync("/api/settings", new { criticalStockThreshold = settings.CriticalStockThreshold, lowStockThreshold = 5 }, Json, Ct);
            saved.StatusCode.Should().Be(HttpStatusCode.OK);
            await using (var c = db.Open())
            {
                (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Outbox WHERE OlayTipi = 'StokEsigiDegisti' AND IslendiAt IS NULL"))
                    .Should().BeGreaterThan(0, "eşik değişimi aynı transaction'da outbox olayı yazar");
            }
            await SyncOnceAsync();
            Mock.GetChannelStock(Channel, code).Should().Be(0, "satılabilir (5) <= eşik (5)");
            (await LastSentAsync(listingId)).Should().Be(0);

            await db.Api.PutAsJsonAsync("/api/settings", new { criticalStockThreshold = settings.CriticalStockThreshold, lowStockThreshold = 4 }, Json, Ct);
            await SyncOnceAsync();
            Mock.GetChannelStock(Channel, code).Should().Be(5);

            (await db.Api.PutAsJsonAsync("/api/settings", new { criticalStockThreshold = 3, lowStockThreshold = -1 }, Json, Ct))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
            using var depot = await db.ApiHost.CreateClientAsAsync(PazaryeriHub.Application.Auth.Roles.Warehouse);
            (await depot.PutAsJsonAsync("/api/settings", new { criticalStockThreshold = 3, lowStockThreshold = 9 }, Json, Ct))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            await db.Api.PutAsJsonAsync("/api/settings", new { criticalStockThreshold = settings.CriticalStockThreshold, lowStockThreshold = 0 }, Json, Ct);
            await RestoreChannelAsync();
        }
    }

    // ------------------------------------------------------------------ mutabakat uzlaştırma (ADR-024)

    private async Task<PushListingsResult> PushDeviationsAsync(params int[] listingIds)
    {
        var response = await db.Api.PostAsJsonAsync("/api/sync/reconciliation/push", new { listingIds }, Json, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PushListingsResult>(Json, Ct))!;
    }

    private Task<ReconciliationResult> ReconcileAsync() => RunAsync(sp => sp.GetRequiredService<ReconciliationJob>().RunAsync(Channel, Ct));

    [Fact]
    public async Task Sapmayi_kanala_yaz_hedef_ayni_olsa_da_defter_degerini_gonderir_gonderilemeyenleri_atlar()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareChannelAsync();
        try
        {
            var (_, listingId, code) = await SkuWithListingAsync(channelId, 5);
            await SyncOnceAsync();
            Mock.SetChannelStock(Channel, code, 9); // kanalda elle değişti

            await OutboxAsync();
            (await QueueAsync(listingId)).Should().OnlyContain(q => q.Durum != "BEKLIYOR", "hedef = son gönderilen: kendiliğinden düzeltilmez");

            var (_, noStock, _) = await SkuWithListingAsync(channelId, -1);
            var (_, passive, _) = await SkuWithListingAsync(channelId, 3);
            await using (var c = db.Open())
            {
                await c.ExecuteAsync("UPDATE dbo.ChannelListing SET Durum = 'PASIF' WHERE Id = @passive", new { passive });
            }

            var result = await PushDeviationsAsync(listingId, noStock, passive, int.MaxValue);
            result.Queued.Should().Be(1);
            result.Skipped.Select(s => s.ListingId).Should().BeEquivalentTo([noStock, passive, int.MaxValue]);
            result.Skipped.Single(s => s.ListingId == noStock).Reason.Should().Contain("stok girilmemiş");

            await SyncOnceAsync();
            Mock.GetChannelStock(Channel, code).Should().Be(5, "defterdeki değer kanala yazıldı");

            using var depot = await db.ApiHost.CreateClientAsAsync(PazaryeriHub.Application.Auth.Roles.Warehouse);
            (await depot.PostAsJsonAsync("/api/sync/reconciliation/push", new { listingIds = new[] { listingId } }, Json, Ct))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            await RestoreChannelAsync();
        }
    }

    [Fact]
    public async Task Sapmayi_kanala_yaz_ayni_degeri_15_dk_icinde_trendyol_a_yeniden_gondermez()
    {
        db.SkipIfUnavailable();
        await using var c = db.Open();
        var trendyol = await c.ExecuteScalarAsync<int>("SELECT Id FROM dbo.Channel WHERE Kod = 'TRENDYOL'");
        var wasActive = await c.ExecuteScalarAsync<bool>("SELECT Aktif FROM dbo.Channel WHERE Id = @trendyol", new { trendyol });
        var skuId = await db.CreateSkuAsync();
        await db.Ledger.ReceiveAsync(new(skuId, db.DefaultWarehouseId, 5), Ct);
        var listingId = await c.ExecuteScalarAsync<int>("""
            INSERT dbo.ChannelListing (SkuId, ChannelId, KanalBarkod, SonGonderilenStok, SonSenkron)
            OUTPUT inserted.Id VALUES (@skuId, @trendyol, @barcode, 5, DATEADD(MINUTE, -3, SYSUTCDATETIME()))
            """, new { skuId, trendyol, barcode = "TY15-" + Guid.NewGuid().ToString("N")[..10] });
        try
        {
            await c.ExecuteAsync("UPDATE dbo.Channel SET Aktif = 1 WHERE Id = @trendyol", new { trendyol });

            var recent = await PushDeviationsAsync(listingId);
            recent.Queued.Should().Be(0);
            recent.Skipped.Single().Reason.Should().Contain("dk içinde tekrar gönderilemez");

            await c.ExecuteAsync("UPDATE dbo.ChannelListing SET SonSenkron = DATEADD(MINUTE, -20, SYSUTCDATETIME()) WHERE Id = @listingId", new { listingId });
            (await PushDeviationsAsync(listingId)).Queued.Should().Be(1);
        }
        finally
        {
            await c.ExecuteAsync("""
                UPDATE dbo.Channel SET Aktif = @wasActive WHERE Id = @trendyol;
                UPDATE dbo.ChannelListing SET Durum = 'PASIF' WHERE Id = @listingId;
                UPDATE dbo.Outbox SET IslendiAt = SYSUTCDATETIME() WHERE IslendiAt IS NULL;
                """, new { wasActive, trendyol, listingId });
        }
    }

    [Fact]
    public async Task Kanal_stogu_deftere_alinir_onizleme_yazmaz_tum_ilanlara_yayilir_tekrar_uygulama_fark_yok()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareChannelAsync();
        try
        {
            // Defter: fiziksel 10, güvenlik 1, rezerve 2 → satılabilir 7
            var (skuId, listingA, codeA) = await SkuWithListingAsync(channelId, 10);
            await db.Ledger.SetSafetyStockAsync(new(skuId, db.DefaultWarehouseId, 1), Ct);
            await db.Ledger.ReserveAsync(new(skuId, db.DefaultWarehouseId, 2), Ct);
            var codeB = NewCode();
            var listingB = await AddListingAsync(skuId, channelId, codeB);
            await SyncOnceAsync();

            Mock.SetChannelStock(Channel, codeA, 4); // kanalda elle düşürüldü
            Mock.SetChannelStock(Channel, codeB, 9);
            await ReconcileAsync();

            var preview = (await db.Api.GetFromJsonAsync<IntakePreview>($"/api/stock/channel-intake/preview?sourceChannelId={channelId}", Json, Ct))!;
            var row = preview.Rows.Single(r => r.SkuId == skuId);
            row.Should().Match<IntakePreviewRow>(r => r.SourceValue == 4 && r.ProposedTarget == 4 && r.ProposedPhysical == 7 && r.Physical == 10);
            row.Flags.Should().Contain(IntakeFlags.ListingsDiffer);
            (await db.GetBalanceAsync(skuId))!.Fiziksel.Should().Be(10, "önizleme yazmaz");

            using var depot = await db.ApiHost.CreateClientAsAsync(PazaryeriHub.Application.Auth.Roles.Warehouse);
            (await depot.GetAsync($"/api/stock/channel-intake/preview?sourceChannelId={channelId}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await depot.PostAsJsonAsync("/api/stock/channel-intake/apply",
                    new IntakeApplyRequest(channelId, preview.PreviewedAtUtc, [new IntakeApplyRow(skuId, 4, 10)]), Json, Ct))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden, "uygulama yalnızca Yönetici");

            var applied = await ApplyIntakeAsync(new IntakeApplyRequest(channelId, preview.PreviewedAtUtc, [new IntakeApplyRow(skuId, 4, 10)]));
            applied.Rows.Single().Outcome.Should().Be(IntakeOutcomes.Applied);
            applied.PushQueued.Should().Be(2);
            (await db.GetBalanceAsync(skuId)).Should().Be(new BalanceRow(7, 2, 1, 4), "Fiziksel = 4 + 2 + 1");

            await using (var c = db.Open())
            {
                var movement = await c.QuerySingleAsync<(string Tip, int Adet, string KaynakTip, string KaynakId, string Kullanici)>(
                    "SELECT TOP (1) Tip, Adet, KaynakTip, KaynakId, Kullanici FROM dbo.StockMovement WHERE SkuId = @skuId ORDER BY Id DESC", new { skuId });
                movement.Should().Match<(string Tip, int Adet, string KaynakTip, string KaynakId, string Kullanici)>(m =>
                    m.Tip == "SAYIM" && m.Adet == -3 && m.KaynakTip == "KANAL_ALIM" && m.KaynakId.StartsWith("KANAL_ALIM:" + applied.BatchId) && m.Kullanici == "test-yonetici");
            }

            await SyncOnceAsync();
            Mock.GetChannelStock(Channel, codeA).Should().Be(4);
            Mock.GetChannelStock(Channel, codeB).Should().Be(4, "aynı SKU'nun tüm ilanları defterdeki değeri alır");

            var again = await ApplyIntakeAsync(new IntakeApplyRequest(channelId, preview.PreviewedAtUtc, [new IntakeApplyRow(skuId, 4, 7)]));
            again.Rows.Single().Outcome.Should().Be(IntakeOutcomes.Unchanged);
            (await db.CountMovementsAsync(skuId, "SAYIM")).Should().Be(1, "fark yoksa hareket yazılmaz");
        }
        finally
        {
            await RestoreChannelAsync();
        }
    }

    [Fact]
    public async Task Kanal_stogu_deftere_alma_onizlemeden_sonraki_siparisi_duser_cakisan_harekette_satir_reddedilir()
    {
        db.SkipIfUnavailable();
        var channelId = await PrepareChannelAsync();
        try
        {
            var (skuA, _, codeA) = await SkuWithListingAsync(channelId, 10);
            var (skuB, _, codeB) = await SkuWithListingAsync(channelId, 10);
            await SyncOnceAsync();
            Mock.SetChannelStock(Channel, codeA, 6);
            Mock.SetChannelStock(Channel, codeB, 6);
            await ReconcileAsync();
            var preview = (await db.Api.GetFromJsonAsync<IntakePreview>($"/api/stock/channel-intake/preview?sourceChannelId={channelId}", Json, Ct))!;

            // Önizlemeden sonra: A'ya sipariş (2 rezerve), B'ye stok girişi (fiziksel değişti)
            await db.Ledger.ReserveAsync(new(skuA, db.DefaultWarehouseId, 2), Ct);
            await db.Ledger.ReceiveAsync(new(skuB, db.DefaultWarehouseId, 1), Ct);

            var result = await ApplyIntakeAsync(new IntakeApplyRequest(channelId, preview.PreviewedAtUtc,
                [new IntakeApplyRow(skuA, 6, 10), new IntakeApplyRow(skuB, 6, 10)]));

            result.Rows.Single(r => r.SkuId == skuA).Outcome.Should().Be(IntakeOutcomes.Applied);
            (await db.GetBalanceAsync(skuA))!.Satilabilir.Should().Be(4, "önizlemeden sonraki sipariş hedeften düşülür: 6 − 2");
            result.Rows.Single(r => r.SkuId == skuB).Should().Match<IntakeRowResult>(r =>
                r.Outcome == IntakeOutcomes.Rejected && r.Message!.Contains("Önizlemeden sonra"));
            (await db.GetBalanceAsync(skuB))!.Fiziksel.Should().Be(11, "reddedilen satır defteri değiştirmez");
        }
        finally
        {
            await RestoreChannelAsync();
        }
    }

    private async Task<IntakeApplyResult> ApplyIntakeAsync(IntakeApplyRequest request)
    {
        var response = await db.Api.PostAsJsonAsync("/api/stock/channel-intake/apply", request, Json, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<IntakeApplyResult>(Json, Ct))!;
    }
}
