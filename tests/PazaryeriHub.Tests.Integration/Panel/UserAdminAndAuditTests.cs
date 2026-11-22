using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using PazaryeriHub.Application.Audit;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Tests.Integration.Excel;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Panel;

/// <summary>Yönetici: kullanıcı silme / pasife alma (açık oturum kapanır) ve işlem geçmişi (ADR-045).</summary>
[Collection(DatabaseCollection.Name)]
public sealed class UserAdminAndAuditTests(TestDatabase db)
{
    private static readonly JsonSerializerOptions Json = ExcelImportApiTests.Json;
    private const string Password = "Yeterince-Uzun-1";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(UserDto User, HttpClient Client)> CreateAndLoginAsync(string role)
    {
        var userName = "yonet-" + Guid.NewGuid().ToString("N")[..8];
        var created = await (await db.Api.PostAsJsonAsync("/api/users", new CreateUserRequest(userName, "Yönetim Test", role, Password), Json, Ct))
            .Content.ReadFromJsonAsync<UserDto>(Json, Ct);
        var client = db.ApiHost.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new { userName, password = Password }, Ct)).EnsureSuccessStatusCode();
        (await client.GetAsync("/api/auth/me", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        return (created!, client);
    }

    [Fact]
    public async Task Silinen_kullanicinin_acik_oturumu_kapanir_ve_listeden_cikar()
    {
        db.SkipIfUnavailable();
        var (user, client) = await CreateAndLoginAsync(Roles.Warehouse);
        using var _ = client;

        (await db.Api.DeleteAsync($"/api/users/{user.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.GetAsync("/api/auth/me", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "silinen kullanıcının çerezi artık geçersiz");
        var users = await db.Api.GetFromJsonAsync<List<UserDto>>("/api/users", Json, Ct);
        users!.Should().NotContain(u => u.Id == user.Id);
        (await db.Api.DeleteAsync($"/api/users/{user.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Pasife_alinan_kullanicinin_oturumu_kapanir_rol_degisikligi_hemen_uygulanir()
    {
        db.SkipIfUnavailable();
        var (user, client) = await CreateAndLoginAsync(Roles.Admin);
        using var _ = client;
        (await client.GetAsync("/api/users", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        await db.Api.PutAsJsonAsync($"/api/users/{user.Id}", new UpdateUserRequest(user.FullName, Roles.Support, true), Json, Ct);
        (await client.GetAsync("/api/users", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "yönetici yetkisi alınınca açık oturumda da geçersiz");
        (await client.GetFromJsonAsync<JsonElement>("/api/auth/me", Ct)).GetProperty("role").GetString().Should().Be(Roles.Support);

        await db.Api.PutAsJsonAsync($"/api/users/{user.Id}", new UpdateUserRequest(user.FullName, Roles.Support, false), Json, Ct);
        (await client.GetAsync("/api/auth/me", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "pasif kullanıcının oturumu kapanır");
    }

    [Fact]
    public async Task Yonetici_kendini_silemez_pasife_alamaz_ve_yetkisini_dusuremez()
    {
        db.SkipIfUnavailable();
        var (user, client) = await CreateAndLoginAsync(Roles.Admin);
        using var _ = client;

        (await client.DeleteAsync($"/api/users/{user.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PutAsJsonAsync($"/api/users/{user.Id}", new UpdateUserRequest(user.FullName, Roles.Admin, false), Json, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PutAsJsonAsync($"/api/users/{user.Id}", new UpdateUserRequest(user.FullName, Roles.Warehouse, true), Json, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PutAsJsonAsync($"/api/users/{user.Id}", new UpdateUserRequest("Yeni Ad", Roles.Admin, true), Json, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK, "kendi adını değiştirebilir");

        await db.Api.DeleteAsync($"/api/users/{user.Id}", Ct);
    }

    [Fact]
    public async Task Islem_gecmisi_yazma_islemlerini_ve_girisleri_kaydeder_yalnizca_yonetici_gorur()
    {
        db.SkipIfUnavailable();
        var (user, client) = await CreateAndLoginAsync(Roles.Admin);
        using var _ = client;
        var newName = "gecmis-" + Guid.NewGuid().ToString("N")[..8];
        var created = await (await client.PostAsJsonAsync("/api/users", new CreateUserRequest(newName, "Geçmiş Test", Roles.Support, Password), Json, Ct))
            .Content.ReadFromJsonAsync<UserDto>(Json, Ct);
        using var anonymous = db.ApiHost.CreateClient();
        (await anonymous.PostAsJsonAsync("/api/auth/login", new { userName = newName, password = "Yanlis-Parola-123" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var mine = await client.GetFromJsonAsync<AuditPage>($"/api/audit?user={user.UserName}", Json, Ct);
        mine!.Items.Should().Contain(e => e.Type == AuditTypes.Login && e.Status == 200);
        mine.Items.Should().Contain(e => e.Type == AuditTypes.Request && e.Method == "POST" && e.Path == "/api/users" && e.Status == 201);
        mine.Items.Should().OnlyContain(e => e.UserName == user.UserName);
        mine.Items.Should().BeInDescendingOrder(e => e.AtUtc);

        var failed = await client.GetFromJsonAsync<AuditPage>($"/api/audit?user={newName}&type={AuditTypes.LoginFailed}", Json, Ct);
        failed!.Items.Should().ContainSingle().Which.Status.Should().Be(401);

        await using (var c = db.Open())
        {
            (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.AuditLog WHERE CONCAT(Yol, Kullanici) LIKE '%Yanlis-Parola%' OR CONCAT(Yol, Kullanici) LIKE '%Yeterince-Uzun%'"))
                .Should().Be(0, "parola ve istek gövdesi kaydedilmez");
        }

        using var support = await db.ApiHost.CreateClientAsAsync(Roles.Support);
        (await support.GetAsync("/api/audit", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await db.Api.DeleteAsync($"/api/users/{created!.Id}", Ct);
        await db.Api.DeleteAsync($"/api/users/{user.Id}", Ct);
    }
}
