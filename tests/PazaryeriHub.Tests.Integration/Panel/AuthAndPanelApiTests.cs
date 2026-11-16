using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using PazaryeriHub.Api.Dev;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Application.Panel;
using PazaryeriHub.Domain.Orders;
using PazaryeriHub.Tests.Integration.Excel;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Panel;

/// <summary>Aşama 5 (API tarafı): oturum, rol yetkileri, Destek maskelemesi ve panel ekran uçları.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class AuthAndPanelApiTests(TestDatabase db)
{
    private static readonly JsonSerializerOptions Json = ExcelImportApiTests.Json;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<DemoDataResult> SeedDemoAsync()
    {
        var response = await db.Api.PostAsync("/api/dev/demo-data", null, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<DemoDataResult>(Json, Ct))!;
    }

    [Fact]
    public async Task Oturumsuz_istek_401_saglik_ucu_acik()
    {
        db.SkipIfUnavailable();
        using var anonymous = db.ApiHost.CreateClient();

        (await anonymous.GetAsync("/api/orders", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/dashboard", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/health/live", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Hatali_parola_401_genel_mesaj_ve_dogru_parola_ile_giris()
    {
        db.SkipIfUnavailable();
        using var client = db.ApiHost.CreateClient();

        var wrong = await client.PostAsJsonAsync("/api/auth/login", new { userName = "test-muhasebe", password = "yanlis-parola-123" }, Ct);
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await wrong.Content.ReadAsStringAsync(Ct)).Should().Contain("Kullanıcı adı veya parola hatalı");

        var unknown = await client.PostAsJsonAsync("/api/auth/login", new { userName = "olmayan-kullanici", password = "yanlis-parola-123" }, Ct);
        static async Task<string?> Detail(HttpResponseMessage r) =>
            (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString();
        (await Detail(unknown)).Should().Be(await Detail(wrong), "kullanıcının var olup olmadığı sızdırılmaz");

        var ok = await client.PostAsJsonAsync("/api/auth/login", new { userName = "TEST-MUHASEBE", password = db.ApiHost.Password }, Ct);
        ok.StatusCode.Should().Be(HttpStatusCode.OK, "kullanıcı adı büyük/küçük harf duyarsız");
        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct);
        me.GetProperty("role").GetString().Should().Be(Roles.Accounting);

        (await client.PostAsync("/api/auth/logout", null, Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync("/api/auth/me", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Bes_hatali_giristen_sonra_hesap_kilitlenir()
    {
        db.SkipIfUnavailable();
        var userName = "kilit-" + Guid.NewGuid().ToString("N")[..8];
        var password = "Gecici!" + Guid.NewGuid().ToString("N");
        (await db.Api.PostAsJsonAsync("/api/users", new CreateUserRequest(userName, "Kilit Test", Roles.Support, password), Json, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        using var client = db.ApiHost.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            await client.PostAsJsonAsync("/api/auth/login", new { userName, password = "yanlis" + i }, Ct);
        }
        var locked = await client.PostAsJsonAsync("/api/auth/login", new { userName, password }, Ct);

        locked.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "doğru parola bile kilit süresince reddedilir");
        (await locked.Content.ReadAsStringAsync(Ct)).Should().Contain("kilitlendi");
    }

    [Theory]
    [InlineData(Roles.Support, "/api/stock/receipts", HttpStatusCode.Forbidden)]
    [InlineData(Roles.Accounting, "/api/stock/receipts", HttpStatusCode.Forbidden)]
    [InlineData(Roles.Warehouse, "/api/stock/receipts", HttpStatusCode.BadRequest)] // yetkili; geçersiz SKU → 400
    [InlineData(Roles.Warehouse, "/api/users", HttpStatusCode.Forbidden)]
    [InlineData(Roles.Warehouse, "/api/sync/queue/1/retry", HttpStatusCode.Forbidden)]
    [InlineData(Roles.Support, "/api/orders/1/status", HttpStatusCode.Forbidden)]
    public async Task Rol_yetkileri_uygulanir(string role, string path, HttpStatusCode expected)
    {
        db.SkipIfUnavailable();
        using var client = await db.ApiHost.CreateClientAsAsync(role);

        var response = await client.PostAsJsonAsync(path, new { skuId = int.MaxValue, warehouseId = 1, quantity = 1, status = "Onaylandi" }, Ct);

        response.StatusCode.Should().Be(expected);
    }

    [Theory]
    [InlineData(Roles.Support)]
    [InlineData(Roles.Accounting)]
    [InlineData(Roles.Warehouse)]
    public async Task Tum_roller_ekranlari_okuyabilir(string role)
    {
        db.SkipIfUnavailable();
        using var client = await db.ApiHost.CreateClientAsAsync(role);

        foreach (var path in new[] { "/api/dashboard", "/api/stock-overview", "/api/stock-movements", "/api/orders", "/api/listings", "/api/sync/summary", "/api/settings", "/api/channels" })
        {
            (await client.GetAsync(path, Ct)).StatusCode.Should().Be(HttpStatusCode.OK, because: $"{role} {path}");
        }
    }

    [Fact]
    public async Task Destek_rolu_musteri_bilgisini_maskeli_gorur()
    {
        db.SkipIfUnavailable();
        var demo = await SeedDemoAsync();
        var orders = await db.Api.GetFromJsonAsync<PagedResult<OrderListItem>>($"/api/orders?search={demo.OrderNos[0]}", Json, Ct);
        var id = orders!.Items.Single().Id;

        var asAdmin = await db.Api.GetFromJsonAsync<OrderDetail>($"/api/orders/{id}", Json, Ct);
        using var support = await db.ApiHost.CreateClientAsAsync(Roles.Support);
        var asSupport = await support.GetFromJsonAsync<OrderDetail>($"/api/orders/{id}", Json, Ct);

        asAdmin!.Customer!.FullName.Should().Be("Ayşe Yılmaz");
        asSupport!.Customer!.FullName.Should().Be("A*** Y*****");
        asSupport.Customer.District.Should().Be("***");
        (await support.GetStringAsync($"/api/orders/{id}", Ct)).Should().NotContain("Yılmaz").And.NotContain("Karşıyaka");
    }

    [Fact]
    public async Task Stok_listesi_kanal_stoklarini_ve_sapmayi_gosterir()
    {
        db.SkipIfUnavailable();
        var demo = await SeedDemoAsync();

        var all = await db.Api.GetFromJsonAsync<PagedResult<StockOverviewRow>>($"/api/stock-overview?search=DEMO-{demo.Tag}", Json, Ct);
        all!.Items.Should().HaveCount(4);
        all.Items.Count(r => r.HasDeviation).Should().Be(1, "yalnızca bir eşleştirmede sapma var");
        var deviating = all.Items.Single(r => r.SkuCode == demo.DeviationSkuCode);
        deviating.HasDeviation.Should().BeTrue();
        deviating.Channels.Should().HaveCount(2);
        deviating.Channels.Single(c => c.ChannelCode == "HEPSIBURADA").Should().Match<ChannelStockCell>(c => c.ChannelStock == demo.DeviationChannelStock && c.Expected == demo.DeviationExpected && c.HasDeviation);

        var onlyDeviations = await db.Api.GetFromJsonAsync<PagedResult<StockOverviewRow>>($"/api/stock-overview?search=DEMO-{demo.Tag}&onlyDeviations=true", Json, Ct);
        onlyDeviations!.Items.Select(r => r.SkuCode).Should().Contain(demo.DeviationSkuCode);
        onlyDeviations.Items.Should().OnlyContain(r => r.HasDeviation);
    }

    [Fact]
    public async Task Genel_bakis_bugunun_siparislerini_ve_hatali_senkronu_sayar()
    {
        db.SkipIfUnavailable();
        var before = await db.Api.GetFromJsonAsync<DashboardDto>("/api/dashboard", Json, Ct);
        await SeedDemoAsync();
        var after = await db.Api.GetFromJsonAsync<DashboardDto>("/api/dashboard", Json, Ct);

        after!.TodayOrders.Should().BeGreaterThanOrEqualTo(before!.TodayOrders + 5, "iptal hariç 5 demo siparişi bugün");
        after.SyncErrors.Should().BeGreaterThanOrEqualTo(before.SyncErrors + 1);
        after.ByChannel.Select(c => c.ChannelCode).Should().Contain(["TRENDYOL", "HEPSIBURADA"]);
        after.CriticalStock.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Stok_hareketleri_filtrelenir_ve_kullanici_yazilir()
    {
        db.SkipIfUnavailable();
        var skuId = await db.CreateSkuAsync();
        await using (var c = db.Open())
        {
            var code = await c.ExecuteScalarAsync<string>("SELECT SkuKodu FROM dbo.Sku WHERE Id = @skuId", new { skuId });
            using var depot = await db.ApiHost.CreateClientAsAsync(Roles.Warehouse);
            (await depot.PostAsJsonAsync("/api/stock/receipts", new { skuId, warehouseId = db.DefaultWarehouseId, quantity = 4, reference = "irsaliye-9" }, Ct))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            var page = await db.Api.GetFromJsonAsync<PagedResult<StockMovementItem>>($"/api/stock-movements?sku={code}&type=GIRIS", Json, Ct);
            page!.Items.Should().ContainSingle().Which.Should().Match<StockMovementItem>(m => m.Quantity == 4 && m.User == "test-depo" && m.SourceType == "PANEL");
        }
    }

    [Fact]
    public async Task Iade_depoya_girisi_bir_kez_yapilir()
    {
        db.SkipIfUnavailable();
        var demo = await SeedDemoAsync();
        var returned = (await db.Api.GetFromJsonAsync<PagedResult<OrderListItem>>($"/api/orders?status=Iade&search=DEMO-{demo.Tag}", Json, Ct))!.Items.Single();
        var detail = await db.Api.GetFromJsonAsync<OrderDetail>($"/api/orders/{returned.Id}", Json, Ct);
        var line = detail!.Lines.Single();
        line.Shipped.Should().BeTrue();
        line.ReturnReceived.Should().BeFalse();
        var physicalBefore = (await db.GetBalanceAsync(line.SkuId!.Value))!.Fiziksel;

        using var depot = await db.ApiHost.CreateClientAsAsync(Roles.Warehouse);
        var receipt = await depot.PostAsync($"/api/orders/{returned.Id}/lines/{line.Id}/return-receipt", null, Ct);
        receipt.StatusCode.Should().Be(HttpStatusCode.OK);
        (await receipt.Content.ReadFromJsonAsync<OrderDetail>(Json, Ct))!.Lines.Single().ReturnReceived.Should().BeTrue();
        (await db.GetBalanceAsync(line.SkuId.Value))!.Fiziksel.Should().Be(physicalBefore + line.Quantity);

        var again = await depot.PostAsync($"/api/orders/{returned.Id}/lines/{line.Id}/return-receipt", null, Ct);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await db.GetBalanceAsync(line.SkuId.Value))!.Fiziksel.Should().Be(physicalBefore + line.Quantity, "iade ikinci kez girilmez");

        var active = (await db.Api.GetFromJsonAsync<PagedResult<OrderListItem>>($"/api/orders?status=Yeni&search=DEMO-{demo.Tag}", Json, Ct))!.Items.First();
        var activeLine = (await db.Api.GetFromJsonAsync<OrderDetail>($"/api/orders/{active.Id}", Json, Ct))!.Lines.Single();
        (await depot.PostAsync($"/api/orders/{active.Id}/lines/{activeLine.Id}/return-receipt", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "yalnızca İade durumundaki sipariş");
    }

    [Fact]
    public async Task Ayarlar_ve_kullanici_yonetimi()
    {
        db.SkipIfUnavailable();
        var original = await db.Api.GetFromJsonAsync<AppSettingsDto>("/api/settings", Json, Ct);
        try
        {
            (await db.Api.PutAsJsonAsync("/api/settings", new AppSettingsDto(7), Json, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await db.Api.GetFromJsonAsync<AppSettingsDto>("/api/settings", Json, Ct))!.CriticalStockThreshold.Should().Be(7);
            (await db.Api.PutAsJsonAsync("/api/settings", new AppSettingsDto(-1), Json, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            await db.Api.PutAsJsonAsync("/api/settings", original, Json, Ct);
        }

        var userName = "panel-" + Guid.NewGuid().ToString("N")[..8];
        (await db.Api.PostAsJsonAsync("/api/users", new CreateUserRequest(userName, "Panel Test", Roles.Warehouse, "kisa"), Json, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "parola en az 10 karakter");
        var created = await (await db.Api.PostAsJsonAsync("/api/users", new CreateUserRequest(userName, "Panel Test", Roles.Warehouse, "Yeterince-Uzun-1"), Json, Ct))
            .Content.ReadFromJsonAsync<UserDto>(Json, Ct);
        (await db.Api.PostAsJsonAsync("/api/users", new CreateUserRequest(userName.ToUpperInvariant(), "Kopya", Roles.Warehouse, "Yeterince-Uzun-1"), Json, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        var updated = await (await db.Api.PutAsJsonAsync($"/api/users/{created!.Id}", new UpdateUserRequest("Panel Test", Roles.Accounting, false), Json, Ct))
            .Content.ReadFromJsonAsync<UserDto>(Json, Ct);
        updated!.Should().Match<UserDto>(u => u.Role == Roles.Accounting && !u.Active);

        using var client = db.ApiHost.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new { userName, password = "Yeterince-Uzun-1" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "pasif kullanıcı giriş yapamaz");

        await using var c = db.Open();
        (await c.ExecuteScalarAsync<string>("SELECT SifreHash FROM dbo.AppUser WHERE Id = @id", new { id = created.Id }))
            .Should().NotContain("Yeterince-Uzun-1", "parola düz metin saklanmaz");
    }
}
