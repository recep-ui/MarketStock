using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Panel;
using PazaryeriHub.Tests.Integration.Excel;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Panel;

/// <summary>
/// ADR-053: Eşleştirme ekranında "İncelenecek eşleşmeler". Satırlar kanal sekmesiyle aynı uyarı/onay kuralını taşır;
/// süzgeç yalnızca geçerli onayı olmayan uyarılı ilanları getirir. Onaydan sonra barkod veya varyant değişirse ilan yeniden incelemeye düşer.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ListingReviewFilterTests(TestDatabase db)
{
    private const string Channel = "SOFTCOTTON"; // mock kanal
    private static readonly JsonSerializerOptions Json = ExcelImportApiTests.Json;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique() => Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
    private static string Barcode() => "869" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999);

    private sealed record Setup(string Tag, int ChannelId, int OkId, int OtherSizeId, int MissingSizeId);

    /// <summary>SKU 70×140 Antrasit; ilanlar: doğru ebat, 50×90 ebat, ebatsız (yalnızca ad). Kanal barkodu kanalda benzersiz olduğu için
    /// diğer ilanlar ayrı barkod taşır; stok kodu SKU ile aynı (barkod farkı uyarılmaz, ADR-035).</summary>
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
            INSERT dbo.Sku (ProductId, SkuKodu, Barkod, Renk, Beden) VALUES (@p, @code, @skuBarcode, N'Antrasit', N'70x140');
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """, new { name = "İnceleme Havlu " + tag, code = "INC-" + tag, skuBarcode });
        const string insert = """
            INSERT dbo.ChannelListing (SkuId, ChannelId, KanalUrunKodu, KanalBarkod, Durum, KanalVerisi)
            VALUES (@skuId, @channelId, @code, @barcode, 'AKTIF', @data);
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """;
        var ok = await c.ExecuteScalarAsync<int>(insert, new
        {
            skuId, channelId, code = "INC-OK-" + tag, barcode = skuBarcode,
            data = JsonSerializer.Serialize(new { urunAdi = "İnceleme Havlu " + tag, renk = "Antrasit", beden = "70 x 140 cm", varyant = "Renk: Antrasit; Ebat: 70x140" })
        });
        var other = await c.ExecuteScalarAsync<int>(insert, new
        {
            skuId, channelId, code = "INC-5090-" + tag, barcode = Barcode(),
            data = JsonSerializer.Serialize(new { urunAdi = "İnceleme Havlu " + tag, renk = "Antrasit", beden = "50x90", varyant = "Renk: Antrasit; Ebat: 50x90", stokKodu = "INC-" + tag })
        });
        var missing = await c.ExecuteScalarAsync<int>(insert, new
        {
            skuId, channelId, code = "INC-YOK-" + tag, barcode = Barcode(),
            data = JsonSerializer.Serialize(new { urunAdi = "İnceleme Havlu " + tag, renk = "Antrasit", stokKodu = "INC-" + tag })
        });
        return new Setup(tag, channelId, ok, other, missing);
    }

    private async Task<PagedResult<ListingDto>> ListAsync(Setup s, bool onlyReview) =>
        (await db.Api.GetFromJsonAsync<PagedResult<ListingDto>>(
            $"/api/listings?channelId={s.ChannelId}&search={s.Tag}&onlyReview={onlyReview.ToString().ToLowerInvariant()}", Json, Ct))!;

    [Fact]
    public async Task Incelenecekler_suzgeci_ebat_farkli_ve_eksik_ilanlari_getirir_satirlar_uyari_etiketlerini_tasir()
    {
        db.SkipIfUnavailable();
        var s = await SeedAsync();

        var all = (await ListAsync(s, onlyReview: false)).Items.ToDictionary(r => r.Id);
        all.Keys.Should().BeEquivalentTo([s.OkId, s.OtherSizeId, s.MissingSizeId]);
        all[s.OkId].Warnings.Should().BeEmpty("aynı ebat, farklı yazım");
        all[s.OkId].NeedsReview.Should().BeFalse();
        all[s.OtherSizeId].Warnings!.Single().Should().Match<MatchWarning>(w =>
            w.Code == MatchWarnings.DimensionDiffers && w.Message.Contains("50×90") && w.Message.Contains("70×140"));
        all[s.MissingSizeId].Warnings!.Single().Code.Should().Be(MatchWarnings.DimensionMissing);
        all[s.OtherSizeId].WarningCount.Should().Be(1);

        var review = await ListAsync(s, onlyReview: true);
        review.Total.Should().Be(2);
        review.Items.Select(r => r.Id).Should().BeEquivalentTo([s.OtherSizeId, s.MissingSizeId]);
        review.Items.Should().OnlyContain(r => r.NeedsReview);
    }

    [Fact]
    public async Task Onaylanan_ilan_suzgecten_cikar_varyanti_veya_barkodu_degisince_yeniden_incelemeye_duser()
    {
        db.SkipIfUnavailable();
        var s = await SeedAsync();

        (await db.Api.PostAsync($"/api/listings/{s.OtherSizeId}/match-approval", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var afterApproval = await ListAsync(s, onlyReview: true);
        afterApproval.Items.Select(r => r.Id).Should().BeEquivalentTo([s.MissingSizeId]);
        var approved = (await ListAsync(s, onlyReview: false)).Items.Single(r => r.Id == s.OtherSizeId);
        approved.Approval.Should().NotBeNull();
        approved.Approval!.Valid.Should().BeTrue();

        // Kanal varyant metni değişti (ebat aynı kalsa bile): önceki onay geçersiz → yeniden incelemeye düşer
        await using (var c = db.Open())
        {
            await c.ExecuteAsync("UPDATE dbo.ChannelListing SET KanalVerisi = JSON_MODIFY(KanalVerisi, '$.varyant', N'Renk: Antrasit; Ebat: 50x90; Paket: 2li') WHERE Id = @id",
                new { id = s.OtherSizeId });
        }
        var changed = (await ListAsync(s, onlyReview: true)).Items.Single(r => r.Id == s.OtherSizeId);
        changed.Approval!.Valid.Should().BeFalse();
        changed.NeedsReview.Should().BeTrue();

        // Yeniden onay → çıkar; sonra barkod değişti → yine düşer
        (await db.Api.PostAsync($"/api/listings/{s.OtherSizeId}/match-approval", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ListAsync(s, onlyReview: true)).Items.Should().NotContain(r => r.Id == s.OtherSizeId);
        await using (var c = db.Open())
        {
            await c.ExecuteAsync("UPDATE dbo.ChannelListing SET KanalBarkod = @barcode WHERE Id = @id", new { barcode = Barcode(), id = s.OtherSizeId });
        }
        (await ListAsync(s, onlyReview: true)).Items.Should().Contain(r => r.Id == s.OtherSizeId && r.Approval != null && !r.Approval.Valid);
    }
}
