using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using PazaryeriHub.Application.Catalog;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Stock;
using PazaryeriHub.Tests.Integration.Excel;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Catalog;

/// <summary>Aşama 2: ürün/SKU CRUD, stok girişi, kanal ve eşleştirme API'leri (gerçek veritabanı).</summary>
[Collection(DatabaseCollection.Name)]
public sealed class CatalogApiTests(TestDatabase db)
{
    private static readonly JsonSerializerOptions Json = ExcelImportApiTests.Json;
    private static string NewCode() => "API-" + Guid.NewGuid().ToString("N")[..10];

    private async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(expected, because: body);
        return JsonSerializer.Deserialize<T>(body, Json)!;
    }

    private async Task<ProductDto> CreateProductAsync(params string[] skuCodes)
    {
        var response = await db.Api.PostAsJsonAsync("/api/products",
            new CreateProductRequest("API Ürünü", "Marka", "Kategori", skuCodes.Select(c => new SkuInput(c, null, "Beyaz", "M")).ToList()),
            Json, TestContext.Current.CancellationToken);
        return await ReadAsync<ProductDto>(response, HttpStatusCode.Created);
    }

    [Fact]
    public async Task Urun_ve_sku_olusturulur_okunur_guncellenir()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var (c1, c2) = (NewCode(), NewCode());

        var created = await CreateProductAsync(c1, c2);
        created.Skus.Select(s => s.SkuCode).Should().Equal(c1, c2);

        var fetched = await ReadAsync<ProductDto>(await db.Api.GetAsync($"/api/products/{created.Id}", ct), HttpStatusCode.OK);
        fetched.Name.Should().Be("API Ürünü");

        var updated = await ReadAsync<ProductDto>(
            await db.Api.PutAsJsonAsync($"/api/products/{created.Id}", new ProductInput("  Yeni Ad  ", null, "Gömlek"), Json, ct), HttpStatusCode.OK);
        updated.Name.Should().Be("Yeni Ad", "baştaki/sondaki boşluklar atılır");
        updated.Brand.Should().BeNull();

        var sku = await ReadAsync<SkuDto>(
            await db.Api.PutAsJsonAsync($"/api/skus/{created.Skus[0].Id}", new SkuInput(c1, "BRK-" + c1, "Siyah", "L"), Json, ct), HttpStatusCode.OK);
        sku.Barcode.Should().Be("BRK-" + c1);

        var search = await ReadAsync<PagedResult<SkuListItem>>(await db.Api.GetAsync($"/api/skus?search={c1}", ct), HttpStatusCode.OK);
        search.Items.Should().ContainSingle().Which.ProductName.Should().Be("Yeni Ad");
    }

    [Fact]
    public async Task Ayni_sku_kodu_409_eksik_alan_400_doner()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var code = NewCode();
        var product = await CreateProductAsync(code);

        var duplicate = await db.Api.PostAsJsonAsync($"/api/products/{product.Id}/skus", new SkuInput(code, null, null, null), Json, ct);
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await duplicate.Content.ReadAsStringAsync(ct)).Should().Contain("SKU kodu zaten kayıtlı");

        var invalid = await db.Api.PostAsJsonAsync("/api/products", new CreateProductRequest("", null, null, null), Json, ct);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var problem = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync(ct));
        problem.RootElement.GetProperty("errors").TryGetProperty("name", out _).Should().BeTrue();

        (await db.Api.GetAsync("/api/products/2147483647", ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Stok_girisi_sayim_ve_guvenlik_stogu_api_ile_yapilir()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var product = await CreateProductAsync(NewCode());
        var skuId = product.Skus[0].Id;
        var wh = db.DefaultWarehouseId;

        var receipt = await ReadAsync<StockChangeResult>(
            await db.Api.PostAsJsonAsync("/api/stock/receipts", new { skuId, warehouseId = wh, quantity = 12, reference = "irsaliye-1" }, ct), HttpStatusCode.OK);
        receipt.Physical.Should().Be(12);

        var count = await ReadAsync<StockChangeResult>(
            await db.Api.PostAsJsonAsync("/api/stock/counts", new { skuId, warehouseId = wh, countedQuantity = 10 }, ct), HttpStatusCode.OK);
        count.Physical.Should().Be(10);

        var safety = await ReadAsync<WarehouseBalance>(
            await db.Api.PutAsJsonAsync("/api/stock/safety-stock", new { skuId, warehouseId = wh, quantity = 3 }, ct), HttpStatusCode.OK);
        safety.Should().BeEquivalentTo(new { Physical = 10, SafetyStock = 3, Available = 7, WarehouseName = "Ana Depo" });

        var balances = await ReadAsync<List<WarehouseBalance>>(await db.Api.GetAsync($"/api/stock/{skuId}", ct), HttpStatusCode.OK);
        balances.Should().ContainSingle().Which.Available.Should().Be(7);

        // Güvenlik stoğu hareket yazmaz ama outbox'a StokDegisti yazar (kanallar güncellensin)
        (await db.CountMovementsAsync(skuId)).Should().Be(2);
        (await db.CountOutboxAsync(skuId, "GUVENLIK_STOGU")).Should().Be(1);

        var zero = await db.Api.PostAsJsonAsync("/api/stock/receipts", new { skuId, warehouseId = wh, quantity = 0 }, ct);
        zero.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await zero.Content.ReadAsStringAsync(ct)).Should().Contain("50010");

        var unknownSku = await db.Api.PostAsJsonAsync("/api/stock/receipts", new { skuId = int.MaxValue, warehouseId = wh, quantity = 1 }, ct);
        unknownSku.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await unknownSku.Content.ReadAsStringAsync(ct)).Should().NotContain("FOREIGN KEY", "SQL metni yanıta sızmamalı");
    }

    [Fact]
    public async Task Stogu_olan_sku_silinemez_olmayan_silinir()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var withStock = await CreateProductAsync(NewCode());
        await db.Ledger.ReceiveAsync(new(withStock.Skus[0].Id, db.DefaultWarehouseId, 1), ct);

        var blocked = await db.Api.DeleteAsync($"/api/skus/{withStock.Skus[0].Id}", ct);
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await blocked.Content.ReadAsStringAsync(ct)).Should().Contain("silinemez");

        var empty = await CreateProductAsync(NewCode(), NewCode());
        (await db.Api.DeleteAsync($"/api/skus/{empty.Skus[0].Id}", ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await db.Api.DeleteAsync($"/api/products/{empty.Id}", ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await db.Api.GetAsync($"/api/products/{empty.Id}", ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Kanal_payi_dogrulanir_ve_guncellenir()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var channels = await ReadAsync<List<ChannelDto>>(await db.Api.GetAsync("/api/channels", ct), HttpStatusCode.OK);
        channels.Select(c => c.Code).Should().BeEquivalentTo(["TRENDYOL", "HEPSIBURADA", "BEYMEN", "SOFTCOTTON"]);
        var beymen = channels.Single(c => c.Code == "BEYMEN");

        (await db.Api.PutAsJsonAsync($"/api/channels/{beymen.Id}", new ChannelUpdate("Beymen", 120, false, null), Json, ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await db.Api.PutAsJsonAsync($"/api/channels/{beymen.Id}", new ChannelUpdate("Beymen", 50, false, "[1,2]"), Json, ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "ayarlar bir JSON nesnesi olmalı");

        try
        {
            var updated = await ReadAsync<ChannelDto>(
                await db.Api.PutAsJsonAsync($"/api/channels/{beymen.Id}", new ChannelUpdate("Beymen", 33.33m, false, """{"stokUstLimit":50}"""), Json, ct),
                HttpStatusCode.OK);
            updated.SharePercent.Should().Be(33.33m);
            updated.HasCredentials.Should().Be(beymen.HasCredentials, "kanal güncellemesi API bilgisine dokunmaz");
        }
        finally
        {
            await db.Api.PutAsJsonAsync($"/api/channels/{beymen.Id}", new ChannelUpdate(beymen.Name, beymen.SharePercent, beymen.Active, beymen.SettingsJson), Json, ct);
        }
    }

    [Fact]
    public async Task Eslestirme_eklenir_guncellenir_aranir_silinir()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var code = NewCode();
        var product = await CreateProductAsync(code);
        var trendyol = (await ReadAsync<List<ChannelDto>>(await db.Api.GetAsync("/api/channels", ct), HttpStatusCode.OK)).Single(c => c.Code == "TRENDYOL");

        var created = await ReadAsync<ListingDto>(
            await db.Api.PutAsJsonAsync("/api/listings", new ListingInput(product.Skus[0].Id, trendyol.Id, "TY-" + code, null, null), Json, ct), HttpStatusCode.OK);
        created.Status.Should().Be("AKTIF");

        var updated = await ReadAsync<ListingDto>(
            await db.Api.PutAsJsonAsync("/api/listings", new ListingInput(product.Skus[0].Id, trendyol.Id, "TY-" + code, "BRK", "pasif"), Json, ct), HttpStatusCode.OK);
        updated.Id.Should().Be(created.Id, "aynı SKU + kanal için upsert");
        updated.Status.Should().Be("PASIF");

        (await db.Api.PutAsJsonAsync("/api/listings", new ListingInput(product.Skus[0].Id, trendyol.Id, null, null, null), Json, ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "kod veya barkoddan biri zorunlu");

        var found = await ReadAsync<PagedResult<ListingDto>>(await db.Api.GetAsync($"/api/listings?channelId={trendyol.Id}&search=TY-{code}", ct), HttpStatusCode.OK);
        found.Items.Should().ContainSingle().Which.SkuCode.Should().Be(code);

        // ADR-044: liste, kanalın bildirdiği ad/görsel/varyant ile bağlı SKU'nun ürün adı/renk/bedenini yan yana verir
        await using (var c = db.Open())
        {
            await c.ExecuteAsync("UPDATE dbo.ChannelListing SET KanalVerisi = @data WHERE Id = @id", new
            {
                id = created.Id,
                data = JsonSerializer.Serialize(new
                {
                    urunAdi = "Kanaldaki Kimono " + code, renk = "Beyaz", beden = "M", marka = "Marka",
                    gorsel = "https://cdn.example.test/k.jpg", kanalDurumu = "Satışta"
                })
            });
        }
        var compared = (await ReadAsync<PagedResult<ListingDto>>(
            await db.Api.GetAsync($"/api/listings?channelId={trendyol.Id}&search=TY-{code}", ct), HttpStatusCode.OK)).Items.Single();
        compared.ChannelName.Should().Be("Kanaldaki Kimono " + code);
        compared.ImageUrl.Should().Be("https://cdn.example.test/k.jpg");
        compared.ChannelStatus.Should().Be("Satışta");
        compared.ProductName.Should().Be("API Ürünü");
        compared.SkuColor.Should().Be("Beyaz");
        compared.SkuSize.Should().Be("M");
        compared.MatchBasis.Should().NotBeNullOrEmpty();
        // Arama kanaldaki ada ve ürün adına da bakar
        (await ReadAsync<PagedResult<ListingDto>>(await db.Api.GetAsync($"/api/listings?search=Kanaldaki Kimono {code}", ct), HttpStatusCode.OK))
            .Items.Should().ContainSingle(x => x.Id == created.Id);

        (await db.Api.DeleteAsync($"/api/listings/{created.Id}", ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await using var connection = db.Open();
        (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.ChannelListing WHERE Id = @id", new { id = created.Id })).Should().Be(0);
    }
}
