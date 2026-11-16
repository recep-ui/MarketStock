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
}
