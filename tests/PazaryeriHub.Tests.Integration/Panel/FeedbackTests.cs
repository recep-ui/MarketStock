using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Tests.Integration.Excel;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Panel;

/// <summary>
/// Öneri ve istekler (ADR-064): her rol not yazar ve listeyi görür; not, yazanın oturumdaki kimliğinden (istemcinin gönderdiğinden
/// değil) kullanıcı adı ve ad soyadla kaydedilir; durumu yalnızca Yönetici değiştirir.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class FeedbackTests(TestDatabase db)
{
    private static readonly JsonSerializerOptions Json = ExcelImportApiTests.Json;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Not_yazanin_adiyla_kaydedilir_ve_herkes_listede_gorur()
    {
        db.SkipIfUnavailable();
        using var support = await db.ApiHost.CreateClientAsAsync(Roles.Support);
        var text = "Sipariş listesine kargo firması filtresi eklensin " + Guid.NewGuid().ToString("N")[..6];

        // İstemci başka bir ad göndermeye çalışsa da oturumdaki kullanıcı yazılır
        var created = await support.PostAsJsonAsync("/api/feedback", new { text, userName = "sahte", fullName = "Sahte Kişi" }, Json, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var note = await created.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        note.GetProperty("text").GetString().Should().Be(text);
        note.GetProperty("userName").GetString().Should().Be("test-destek");
        note.GetProperty("fullName").GetString().Should().Be("test-destek", "tohum kullanıcının ad soyadı verilmediğinde kullanıcı adıdır");
        note.GetProperty("status").GetString().Should().Be("YENI");

        using var accounting = await db.ApiHost.CreateClientAsAsync(Roles.Accounting);
        var list = await accounting.GetFromJsonAsync<JsonElement>("/api/feedback?pageSize=200", Json, Ct);
        var items = list.GetProperty("items").EnumerateArray().ToList();
        items.Should().Contain(i => i.GetProperty("text").GetString() == text && i.GetProperty("fullName").GetString() == "test-destek");
    }

    [Fact]
    public async Task Bos_veya_cok_uzun_not_reddedilir()
    {
        db.SkipIfUnavailable();
        using var warehouse = await db.ApiHost.CreateClientAsAsync(Roles.Warehouse);

        (await warehouse.PostAsJsonAsync("/api/feedback", new { text = "   " }, Json, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await warehouse.PostAsJsonAsync("/api/feedback", new { text = new string('a', 2001) }, Json, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Durumu_yalnizca_yonetici_degistirir()
    {
        db.SkipIfUnavailable();
        using var support = await db.ApiHost.CreateClientAsAsync(Roles.Support);
        var created = await support.PostAsJsonAsync("/api/feedback", new { text = "Stok listesinde toplu seçim olsun" }, Json, Ct);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("id").GetInt32();

        (await support.PutAsJsonAsync($"/api/feedback/{id}/status", new { status = "TAMAMLANDI" }, Json, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var admin = await db.ApiHost.CreateClientAsAsync(Roles.Admin);
        (await admin.PutAsJsonAsync($"/api/feedback/{id}/status", new { status = "GECERSIZ" }, Json, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync("/api/feedback/999999999/status", new { status = "TAMAMLANDI" }, Json, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var done = await admin.PutAsJsonAsync($"/api/feedback/{id}/status", new { status = "TAMAMLANDI" }, Json, Ct);
        done.StatusCode.Should().Be(HttpStatusCode.OK, await done.Content.ReadAsStringAsync(Ct));
        var note = await done.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        note.GetProperty("status").GetString().Should().Be("TAMAMLANDI");
        note.GetProperty("statusChangedBy").GetString().Should().Be("test-yonetici");

        var open = await support.GetFromJsonAsync<JsonElement>("/api/feedback?status=YENI&pageSize=200", Json, Ct);
        open.GetProperty("items").EnumerateArray().Should().NotContain(i => i.GetProperty("id").GetInt32() == id);
    }
}
