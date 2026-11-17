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
