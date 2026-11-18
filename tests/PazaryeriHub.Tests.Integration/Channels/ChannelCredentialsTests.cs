using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using PazaryeriHub.Adapters.Mock;
using PazaryeriHub.Adapters.Trendyol;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Tests.Integration.Excel;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Channels;

/// <summary>Aşama 6: kanal kimlik bilgilerinin şifreli saklanması ve adaptör modu seçimi.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class ChannelCredentialsTests(TestDatabase db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Kullanıcının test veritabanına girdiği gerçek API bilgisi testten önce saklanır, sonra aynen geri yazılır.</summary>
    private async Task<byte[]?> SaveCredentialsAsync(string code)
    {
        await using var c = db.Open();
        return await c.ExecuteScalarAsync<byte[]?>("SELECT KimlikSifreli FROM dbo.Channel WHERE Kod = @code", new { code });
    }

    private async Task RestoreCredentialsAsync(string code, byte[]? value)
    {
        await using var c = db.Open();
        await c.ExecuteAsync("UPDATE dbo.Channel SET KimlikSifreli = @value WHERE Kod = @code", new { code, value });
    }

    [Fact]
    public async Task Kimlik_bilgisi_sifreli_saklanir_ve_hicbir_yanitta_donmez()
    {
        db.SkipIfUnavailable();
        var channels = await db.Api.GetFromJsonAsync<List<ChannelDto>>("/api/channels", ExcelImportApiTests.Json, Ct);
        var trendyol = channels!.Single(c => c.Code == "TRENDYOL");
        const string secret = "cok-gizli-api-secret-123";
        var original = await SaveCredentialsAsync("TRENDYOL");

        try
        {
            var invalid = await db.Api.PutAsJsonAsync($"/api/channels/{trendyol.Id}/credentials", new { sellerId = 0, apiKey = "k", apiSecret = secret, environment = "Prod" }, Ct);
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var saved = await db.Api.PutAsJsonAsync($"/api/channels/{trendyol.Id}/credentials", new { sellerId = 12345, apiKey = "key-1", apiSecret = secret, environment = "Stage" }, Ct);
            saved.StatusCode.Should().Be(HttpStatusCode.NoContent);

            var listed = await db.Api.GetStringAsync("/api/channels", Ct);
            listed.Should().NotContain(secret).And.NotContain("key-1");
            (await db.Api.GetFromJsonAsync<List<ChannelDto>>("/api/channels", ExcelImportApiTests.Json, Ct))!
                .Single(c => c.Code == "TRENDYOL").HasCredentials.Should().BeTrue();

            await using (var c = db.Open())
            {
                var stored = await c.ExecuteScalarAsync<byte[]>("SELECT KimlikSifreli FROM dbo.Channel WHERE Kod = 'TRENDYOL'");
                System.Text.Encoding.UTF8.GetString(stored!).Should().NotContain(secret, "veritabanında düz metin olmamalı");
            }

            using var scope = db.Services.CreateScope();
            var json = await scope.ServiceProvider.GetRequiredService<IChannelCredentialStore>().GetJsonAsync("TRENDYOL", Ct);
            TrendyolCredentials.Parse(json!).Should().Match<TrendyolCredentials>(x => x.SellerId == 12345 && x.ApiSecret == secret && x.Environment == "Stage");

            using var depot = await db.ApiHost.CreateClientAsAsync(Roles.Warehouse);
            (await depot.PutAsJsonAsync($"/api/channels/{trendyol.Id}/credentials", new { sellerId = 1, apiKey = "a", apiSecret = "b", environment = "Prod" }, Ct))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);

            var softcotton = channels!.Single(c => c.Code == "SOFTCOTTON");
            (await db.Api.PutAsJsonAsync($"/api/channels/{softcotton.Id}/credentials", new { x = 1 }, Ct))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest, "gerçek adaptörü olmayan kanala bilgi girilmez");
        }
        finally
        {
            try
            {
                (await db.Api.DeleteAsync($"/api/channels/{trendyol.Id}/credentials", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
            }
            finally
            {
                await RestoreCredentialsAsync("TRENDYOL", original);
            }
        }
    }

    [Fact]
    public async Task Varsayilan_mod_mock_gercek_modda_trendyol_adaptoru_secilir()
    {
        db.SkipIfUnavailable();
        db.Services.GetServices<IChannelAdapter>().Single(a => a.ChannelCode == "TRENDYOL").Should().BeOfType<MockChannelAdapter>();

        await using var real = new ApiFactory(db.ConnectionString!, new Dictionary<string, string?> { ["Kanallar:TRENDYOL:Mod"] = "Gercek" });
        var adapters = real.Services.GetServices<IChannelAdapter>().ToList();

        // Gerçek adaptör yazma kapısıyla sarılıdır (ADR-026)
        adapters.Single(a => a.ChannelCode == "TRENDYOL").Should().BeOfType<PazaryeriHub.Infrastructure.Channels.GuardedChannelAdapter>()
            .Which.Inner.Should().BeOfType<TrendyolAdapter>();
        adapters.Where(a => a.ChannelCode != "TRENDYOL").Should().AllBeOfType<MockChannelAdapter>();

        await using var hb = new ApiFactory(db.ConnectionString!, new Dictionary<string, string?> { ["Kanallar:HEPSIBURADA:Mod"] = "Gercek" });
        hb.Services.GetServices<IChannelAdapter>().Single(a => a.ChannelCode == "HEPSIBURADA")
            .Should().BeOfType<PazaryeriHub.Infrastructure.Channels.GuardedChannelAdapter>()
            .Which.Inner.Should().BeOfType<PazaryeriHub.Adapters.Hepsiburada.HepsiburadaAdapter>();

        await using var by = new ApiFactory(db.ConnectionString!, new Dictionary<string, string?> { ["Kanallar:BEYMEN:Mod"] = "Gercek" });
        by.Services.GetServices<IChannelAdapter>().Single(a => a.ChannelCode == "BEYMEN")
            .Should().BeOfType<PazaryeriHub.Infrastructure.Channels.GuardedChannelAdapter>()
            .Which.Inner.Should().BeOfType<PazaryeriHub.Adapters.Beymen.BeymenAdapter>();
        by.Services.GetServices<IChannelAdapter>().Single(a => a.ChannelCode == "SOFTCOTTON").Should().BeOfType<MockChannelAdapter>();

        // Kimlik bilgisi yokken gerçek adaptör dış çağrı yapmadan sade hata verir. Kullanıcının girdiği gerçek anahtar varsa
        // geçici olarak kaldırılır (gerçek Trendyol'a istek ÇIKMASIN) ve test sonunda aynen geri yazılır.
        var original = await SaveCredentialsAsync("TRENDYOL");
        try
        {
            await RestoreCredentialsAsync("TRENDYOL", null);
            var act = () => adapters.Single(a => a.ChannelCode == "TRENDYOL").FetchOrdersAsync(DateTime.UtcNow.AddHours(-1), Ct);
            (await act.Should().ThrowAsync<ChannelApiException>()).Which.Message.Should().Contain("girilmemiş");
        }
        finally
        {
            await RestoreCredentialsAsync("TRENDYOL", original);
        }
    }
}
