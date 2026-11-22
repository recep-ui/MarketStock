using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Infrastructure.Auth;
using PazaryeriHub.Infrastructure.Persistence;
using PazaryeriHub.Tests.Integration.Infrastructure;
using IdentityOptions = PazaryeriHub.Application.Auth.IdentityOptions;

namespace PazaryeriHub.Tests.Integration.Panel;

/// <summary>
/// Kurulum (--kurulum) tohum kullanıcısı: başka bilgisayardan taşınan veritabanında kullanıcılar zaten vardır;
/// kur.ps1'de girilen ilk Yönetici yine de eklenmeli, var olan kullanıcıların parolasına dokunulmamalı (ADR-046).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class SetupSeedUserTests(TestDatabase db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private UserService Create(string userName, string password) => new(
        db.Services.GetRequiredService<SqlConnectionFactory>(),
        Options.Create(new IdentityOptions { SeedUsers = [new SeedUser { UserName = userName, FullName = "Kurulum", Role = Roles.Admin, Password = password }] }),
        TimeProvider.System,
        NullLogger<UserService>.Instance);

    private static IHostEnvironment Production => new HostingEnvironment { EnvironmentName = Environments.Production };

    private async Task<string?> HashAsync(string userName)
    {
        await using var c = new SqlConnection(db.ConnectionString);
        return await c.QuerySingleOrDefaultAsync<string>("SELECT SifreHash FROM dbo.AppUser WHERE KullaniciAdi = @userName", new { userName });
    }

    [Fact]
    public async Task Uretimde_kullanici_varken_normal_baslangic_tohum_eklemez()
    {
        db.SkipIfUnavailable();
        var userName = "kurulum-" + Guid.NewGuid().ToString("N")[..8];
        await db.Api.GetAsync("/api/auth/me", Ct); // en az bir kullanıcı (test yöneticisi) var

        await Create(userName, "Kurulum-Parola-1").SeedAsync(Production, setupMode: false, Ct);

        (await HashAsync(userName)).Should().BeNull();
    }

    [Fact]
    public async Task Kurulum_modunda_eksik_yonetici_eklenir_var_olanin_parolasi_degismez()
    {
        db.SkipIfUnavailable();
        var userName = "kurulum-" + Guid.NewGuid().ToString("N")[..8];
        await db.Api.GetAsync("/api/auth/me", Ct);

        await Create(userName, "Kurulum-Parola-1").SeedAsync(Production, setupMode: true, Ct);
        var first = await HashAsync(userName);
        first.Should().NotBeNull("taşınan veritabanında da ilk Yönetici eklenmeli");
        (await Create(userName, "Kurulum-Parola-1").ValidateAsync(userName, "Kurulum-Parola-1", Ct)).User.Should().NotBeNull();

        await Create(userName, "Baska-Parola-22").SeedAsync(Production, setupMode: true, Ct);
        (await HashAsync(userName)).Should().Be(first, "kurulum var olan kullanıcının parolasını değiştirmez");
    }
}
