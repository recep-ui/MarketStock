using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.Panel;
using PazaryeriHub.Tests.Integration.Excel;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Panel;

/// <summary>
/// ADR-036: kanal stok listesinde uyarıyı "doğru eşleşme" olarak kalıcı onaylama. Onay, eşleşme bilgisinin imzasına bağlıdır;
/// bilgi değişince geçersizleşir. Geri alma kaydı silmez (geçmiş korunur). Eşleştirmeyi değiştirmez.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ListingMatchApprovalTests(TestDatabase db)
{
    private const string Channel = "SOFTCOTTON"; // mock kanal
    private static readonly JsonSerializerOptions Json = ExcelImportApiTests.Json;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique() => Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
    private static string Barcode() => "869" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999);

    private sealed record Setup(string Tag, int ChannelId, int SkuId, int CleanId, int SuspectId);

    /// <summary>Bir SKU ve iki ilan: biri sorunsuz (aynı barkod), biri şüpheli (başka barkod + başka renk).</summary>
    private async Task<Setup> SeedAsync()
    {
        var tag = Unique();
        var skuBarcode = Barcode();
        await using var c = db.Open();
        var channelId = await c.ExecuteScalarAsync<int>("SELECT Id FROM dbo.Channel WHERE Kod = @Channel", new { Channel });
        var skuId = await c.ExecuteScalarAsync<int>("""
            DECLARE @p INT;
            INSERT dbo.Product (Ad, Marka) VALUES (@name, N'Softcotton');
            SET @p = SCOPE_IDENTITY();
            INSERT dbo.Sku (ProductId, SkuKodu, Barkod, Renk, Beden) VALUES (@p, @code, @skuBarcode, N'Beyaz', N'50x90');
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """, new { name = "Onay Havlu " + tag, code = "ONY-" + tag, skuBarcode });
        const string insert = """
            INSERT dbo.ChannelListing (SkuId, ChannelId, KanalUrunKodu, KanalBarkod, Durum, KanalVerisi)
            VALUES (@skuId, @channelId, @code, @barcode, 'AKTIF', @data);
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """;
        var clean = await c.ExecuteScalarAsync<int>(insert, new
        {
            skuId, channelId, code = "ONY-OK-" + tag, barcode = skuBarcode,
            data = JsonSerializer.Serialize(new { urunAdi = "Onay Havlu " + tag, renk = "Beyaz" })
        });
        var suspect = await c.ExecuteScalarAsync<int>(insert, new
        {
            skuId, channelId, code = "ONY-X-" + tag, barcode = Barcode(),
            data = JsonSerializer.Serialize(new { urunAdi = "Onay Havlu " + tag, renk = "Lacivert" })
        });
        return new Setup(tag, channelId, skuId, clean, suspect);
    }

    private async Task<ChannelStockList> ListAsync(Setup s, string extra = "") =>
        (await db.Api.GetFromJsonAsync<ChannelStockList>($"/api/stock-overview/channels/{s.ChannelId}?search={s.Tag}{extra}", Json, Ct))!;

    private async Task<ChannelStockListRow> RowAsync(Setup s, int listingId) =>
        (await ListAsync(s)).Rows.Items.Single(r => r.ListingId == listingId);

    private Task<HttpResponseMessage> ApproveAsync(int listingId, HttpClient? client = null) =>
        (client ?? db.Api).PostAsync($"/api/listings/{listingId}/match-approval", null, Ct);

    private async Task<int> HistoryCountAsync(int listingId, bool activeOnly)
    {
        await using var c = db.Open();
        return await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.ListingMatchApproval WHERE ListingId = @listingId AND (@activeOnly = 0 OR GeriAlmaTarihi IS NULL)",
            new { listingId, activeOnly });
    }

    [Fact]
    public async Task Onaylanan_uyari_kontrol_listesinden_cikar_uyarilar_ve_onaylayan_gorunur()
    {
        db.SkipIfUnavailable();
        var s = await SeedAsync();
        var before = await ListAsync(s);
        (await RowAsync(s, s.SuspectId)).NeedsReview.Should().BeTrue();

        var response = await ApproveAsync(s.SuspectId);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var approved = (await response.Content.ReadFromJsonAsync<ChannelStockListRow>(Json, Ct))!;

        approved.Approval.Should().NotBeNull();
        approved.Approval!.Valid.Should().BeTrue();
        approved.Approval.ApprovedBy.Should().Be("test-yonetici");
        approved.Approval.ApprovedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(2));
        approved.NeedsReview.Should().BeFalse();
        approved.Warnings.Select(w => w.Code).Should().Contain([MatchWarnings.BarcodeDiffers, MatchWarnings.ColorDiffers], "uyarılar silinmez, onaylı gösterilir");

        var after = await ListAsync(s);
        after.ReviewCount.Should().Be(before.ReviewCount - 1);
        after.ApprovedCount.Should().Be(before.ApprovedCount + 1);
        (await ListAsync(s, "&onlyReview=true")).Rows.Items.Should().NotContain(r => r.ListingId == s.SuspectId);

        // Tekrar onay: yeni kayıt açılmaz
        (await ApproveAsync(s.SuspectId)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await HistoryCountAsync(s.SuspectId, activeOnly: false)).Should().Be(1);

        // Uyarısı olmayan ilan onaylanamaz; eşleştirme değişmedi
        (await ApproveAsync(s.CleanId)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await using var c = db.Open();
        (await c.ExecuteScalarAsync<int>("SELECT SkuId FROM dbo.ChannelListing WHERE Id = @SuspectId", new { s.SuspectId })).Should().Be(s.SkuId);
    }

    [Fact]
    public async Task Eslesme_bilgisi_degisince_onay_gecersizlesir_yeniden_onay_ve_geri_alma_gecmisi_korur()
    {
        db.SkipIfUnavailable();
        var s = await SeedAsync();
        (await ApproveAsync(s.SuspectId)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Kanal adı değişti (ör. yeni katalog çekimi) → onay artık bu eşleşmeyi kapsamaz
        await using (var c = db.Open())
        {
            await c.ExecuteAsync("UPDATE dbo.ChannelListing SET KanalVerisi = JSON_MODIFY(KanalVerisi, '$.urunAdi', N'Kadın Saten Gecelik') WHERE Id = @SuspectId",
                new { s.SuspectId });
        }
        var outdated = await RowAsync(s, s.SuspectId);
        outdated.Approval.Should().NotBeNull();
        outdated.Approval!.Valid.Should().BeFalse();
        outdated.NeedsReview.Should().BeTrue();
        outdated.Warnings.Select(w => w.Code).Should().Contain(MatchWarnings.NameDiffers);

        // Yeniden onay: eski onay kapanır, yenisi açılır
        (await ApproveAsync(s.SuspectId)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await RowAsync(s, s.SuspectId)).Approval!.Valid.Should().BeTrue();
        (await HistoryCountAsync(s.SuspectId, activeOnly: true)).Should().Be(1);

        // Geri alma: kayıt silinmez, kapanır
        var revoke = await db.Api.DeleteAsync($"/api/listings/{s.SuspectId}/match-approval", Ct);
        revoke.StatusCode.Should().Be(HttpStatusCode.OK, await revoke.Content.ReadAsStringAsync(Ct));
        var revoked = (await revoke.Content.ReadFromJsonAsync<ChannelStockListRow>(Json, Ct))!;
        revoked.Approval.Should().BeNull();
        revoked.NeedsReview.Should().BeTrue();
        (await HistoryCountAsync(s.SuspectId, activeOnly: true)).Should().Be(0);
        (await HistoryCountAsync(s.SuspectId, activeOnly: false)).Should().Be(2);
        await using (var c = db.Open())
        {
            (await c.QuerySingleAsync<string>("SELECT TOP (1) GeriAlan FROM dbo.ListingMatchApproval WHERE ListingId = @SuspectId ORDER BY Id DESC",
                new { s.SuspectId })).Should().Be("test-yonetici");
        }
        (await db.Api.DeleteAsync($"/api/listings/{s.SuspectId}/match-approval", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Onay_yalnizca_Yonetici_diger_roller_gorur_ilan_silinince_onay_da_silinir()
    {
        db.SkipIfUnavailable();
        var s = await SeedAsync();
        foreach (var role in new[] { Roles.Warehouse, Roles.Accounting, Roles.Support })
        {
            using var client = await db.ApiHost.CreateClientAsAsync(role);
            (await ApproveAsync(s.SuspectId, client)).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
        }
        (await ApproveAsync(s.SuspectId)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var support = await db.ApiHost.CreateClientAsAsync(Roles.Support);
        var seen = (await support.GetFromJsonAsync<ChannelStockList>($"/api/stock-overview/channels/{s.ChannelId}?search={s.Tag}", Json, Ct))!;
        seen.Rows.Items.Single(r => r.ListingId == s.SuspectId).Approval!.Valid.Should().BeTrue();

        (await ApproveAsync(999_999_999)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await db.Api.DeleteAsync($"/api/listings/{s.SuspectId}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await HistoryCountAsync(s.SuspectId, activeOnly: false)).Should().Be(0);
    }
}
