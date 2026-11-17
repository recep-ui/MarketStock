using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PazaryeriHub.Adapters.Mock;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Domain.Orders;
using PazaryeriHub.Infrastructure.Channels;
using PazaryeriHub.Infrastructure.Persistence;
using PazaryeriHub.Tests.Integration.Excel;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Orders;

/// <summary>
/// M2 (ADR-026): kanala yazma yetenekleri, yazma kapısı, komut kuyruğu ve sipariş durumu bildirimi (mock kanal).
/// Kabul: kapı kapalıyken hiçbir şey gitmez; DENEME'de yalnızca onaylanan tek kayıt gider; onaylanan bildirim mock kanalda tam bir kez
/// görünür; kanaldan gelen geçiş bildirim üretmez; takip numarası gereken kanalda takip numarasız kayıt reddedilir; 5. hatada HATA.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class OrderNotificationTests(TestDatabase db)
{
    private const string Channel = "SOFTCOTTON"; // mock kanal (gerçek adaptörü yok)

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MockChannelStore Mock => db.Services.GetRequiredService<MockChannelStore>();

    private static string NewOrderNo() => "NTF-" + Guid.NewGuid().ToString("N")[..12];

    // ------------------------------------------------------------------ yetenekler ve kapı

    [Fact]
    public async Task Yetenekler_varsayilan_kapali_stok_acik_yalnizca_yonetici_degistirir()
    {
        db.SkipIfUnavailable();
        var channelId = await ChannelIdAsync();
        try
        {
            await SetCapabilityAsync(channelId, ChannelCapabilities.OrderStatus, CapabilityStates.Closed);
            var caps = (await db.Api.GetFromJsonAsync<List<ChannelCapabilityDto>>($"/api/channels/{channelId}/capabilities", ExcelImportApiTests.Json, Ct))!;
            caps.Select(c => c.Capability).Should().Equal(ChannelCapabilities.All);
            caps.Single(c => c.Capability == "STOK").State.Should().Be("ACIK", "mevcut stok gönderimi bozulmasın");
            caps.Single(c => c.Capability == "SIPARIS_DURUM").State.Should().Be("KAPALI");

            using var depo = await db.ApiHost.CreateClientAsAsync(Roles.Warehouse);
            (await depo.PutAsJsonAsync($"/api/channels/{channelId}/capabilities/SIPARIS_DURUM", new { state = "ACIK" }, Ct))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await db.Api.PutAsJsonAsync($"/api/channels/{channelId}/capabilities/SIPARIS_DURUM", new { state = "BELKI" }, Ct))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var set = await db.Api.PutAsJsonAsync($"/api/channels/{channelId}/capabilities/SIPARIS_DURUM", new { state = "DENEME" }, Ct);
            set.StatusCode.Should().Be(HttpStatusCode.OK);
            (await set.Content.ReadFromJsonAsync<ChannelCapabilityDto>(ExcelImportApiTests.Json, Ct))!.ChangedBy.Should().NotBeNullOrEmpty("denetim izi");
        }
        finally
        {
            await SetCapabilityAsync(channelId, ChannelCapabilities.OrderStatus, CapabilityStates.Closed);
        }
    }

    [Fact]
    public async Task Yazma_kapisi_global_kilit_pasif_kanal_ve_kapali_yetenekte_engeller()
    {
        db.SkipIfUnavailable();
        var channelId = await ChannelIdAsync();
        var factory = db.Services.GetRequiredService<SqlConnectionFactory>();
        var locked = new ChannelWriteGate(new FixedOptions(new ChannelWriteOptions { YazmaKapali = true }), factory);
        var open = new ChannelWriteGate(new FixedOptions(new ChannelWriteOptions()), factory);
        await using var c = db.Open();
        var wasActive = await c.ExecuteScalarAsync<bool>("SELECT Aktif FROM dbo.Channel WHERE Id = @channelId", new { channelId });
        try
        {
            await c.ExecuteAsync("UPDATE dbo.Channel SET Aktif = 1 WHERE Id = @channelId", new { channelId });
            await SetCapabilityAsync(channelId, ChannelCapabilities.OrderStatus, CapabilityStates.Open);

            (await Blocked(() => locked.EnsureAllowedAsync(Channel, ChannelCapabilities.OrderStatus, Ct))).Should().Contain("Global yazma kilidi");
            await open.EnsureAllowedAsync(Channel, ChannelCapabilities.OrderStatus, Ct); // engel yok

            await SetCapabilityAsync(channelId, ChannelCapabilities.OrderStatus, CapabilityStates.Closed);
            (await Blocked(() => open.EnsureAllowedAsync(Channel, ChannelCapabilities.OrderStatus, Ct))).Should().Contain("Yetenek kapalı");

            await SetCapabilityAsync(channelId, ChannelCapabilities.OrderStatus, CapabilityStates.Trial);
            await open.EnsureAllowedAsync(Channel, ChannelCapabilities.OrderStatus, Ct); // DENEME: kayıt bazlı onay kuyrukta

            await c.ExecuteAsync("UPDATE dbo.Channel SET Aktif = 0 WHERE Id = @channelId", new { channelId });
            (await Blocked(() => open.EnsureAllowedAsync(Channel, ChannelCapabilities.OrderStatus, Ct))).Should().Contain("pasif");
        }
        finally
        {
            await c.ExecuteAsync("UPDATE dbo.Channel SET Aktif = @wasActive WHERE Id = @channelId", new { channelId, wasActive });
            await SetCapabilityAsync(channelId, ChannelCapabilities.OrderStatus, CapabilityStates.Closed);
        }
    }

    // ------------------------------------------------------------------ bildirim akışı

    [Fact]
    public async Task Deneme_modunda_yalnizca_onaylanan_bildirim_bir_kez_gider_kanaldan_gelen_gecis_bildirim_uretmez()
    {
        db.SkipIfUnavailable();
        var channelId = await ChannelIdAsync();
        var no = NewOrderNo();
        var orderId = await IngestAsync(no, OrderStatus.Yeni);
        var fromChannel = await IngestAsync(NewOrderNo(), OrderStatus.Hazirlaniyor); // kanaldan Hazırlanıyor geldi
        try
        {
            await ChangeStatusAsync(orderId, OrderStatus.Onaylandi);
            await ChangeStatusAsync(orderId, OrderStatus.Hazirlaniyor);

            var pending = await db.Api.GetFromJsonAsync<PagedResult<OrderNotificationRow>>($"/api/orders/notifications?channelId={channelId}&filter=BILDIRILMEDI&pageSize=500", ExcelImportApiTests.Json, Ct);
            pending!.Items.Select(i => i.OrderId).Should().Contain(orderId).And.NotContain(fromChannel, "kanaldan gelen durum zaten kanalda");

            // Yetenek kapalı → kayıt oluşmaz
            await SetCapabilityAsync(channelId, ChannelCapabilities.OrderStatus, CapabilityStates.Closed);
            (await NotifyAsync([orderId])).Single().Outcome.Should().Be(NotifyOutcomes.Rejected);
            (await CommandsAsync(orderId)).Should().BeEmpty();

            // DENEME → onay bekler, job göndermez
            await SetCapabilityAsync(channelId, ChannelCapabilities.OrderStatus, CapabilityStates.Trial);
            var results = await NotifyAsync([orderId, fromChannel]);
            results.Single(r => r.OrderId == orderId).Outcome.Should().Be(NotifyOutcomes.AwaitingApproval);
            results.Single(r => r.OrderId == fromChannel).Outcome.Should().Be(NotifyOutcomes.NotRequired);
            (await NotifyAsync([orderId])).Single().Outcome.Should().Be(NotifyOutcomes.AlreadyExists, "aynı bildirim ikinci kez oluşmaz");

            (await RunJobAsync()).Claimed.Should().Be(0);
            Mock.StatusUpdatesOf(no).Should().BeEmpty();

            // Onay → tek kayıt gider
            var commandId = (await CommandsAsync(orderId)).Single().Id;
            using (var depo = await db.ApiHost.CreateClientAsAsync(Roles.Warehouse))
            {
                (await depo.PostAsync($"/api/channel-commands/{commandId}/approve", null, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            }
            (await db.Api.PostAsync($"/api/channel-commands/{commandId}/approve", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await RunJobAsync()).Sent.Should().Be(1);
            (await RunJobAsync()).Claimed.Should().Be(0, "gönderilen kayıt tekrar gönderilmez");

            var update = Mock.StatusUpdatesOf(no).Should().ContainSingle().Subject;
            update.Status.Should().Be(OrderStatus.Hazirlaniyor);
            update.Lines.Should().Equal(new OrderStatusUpdateLine("L-" + no, 2));
            update.ChannelPackageId.Should().Be("P-" + no);
            (await CommandsAsync(orderId)).Single().State.Should().Be(ChannelCommandStates.Sent);
        }
        finally
        {
            await SetCapabilityAsync(channelId, ChannelCapabilities.OrderStatus, CapabilityStates.Closed);
        }
    }

    [Fact]
    public async Task Kendi_kargoda_takip_numarasi_zorunlu_kanal_kargosunda_bildirim_gerekmez_otomatik_mod_kuyruga_alir()
    {
        db.SkipIfUnavailable();
        var channelId = await ChannelIdAsync();
        var orderId = await IngestAsync(NewOrderNo(), OrderStatus.Yeni);
        var autoOrder = await IngestAsync(NewOrderNo(), OrderStatus.Yeni);
        try
        {
            // M1 referans listesi: kanalın kargo firmaları (kod doğrulaması için)
            Mock.SetReferences(Channel, ChannelReferenceType.CargoProvider, [new("MOCK-KARGO-1", "Mock Kargo 1")]);
            using (var scope = db.Services.CreateScope())
            {
                await ActivatorUtilities.CreateInstance<ReferenceFetchJob>(scope.ServiceProvider).RunAsync(Channel, scheduled: false, Ct);
            }
            await SetCapabilityAsync(channelId, ChannelCapabilities.OrderStatus, CapabilityStates.Open);
            foreach (var s in new[] { OrderStatus.Onaylandi, OrderStatus.Hazirlaniyor, OrderStatus.Kargoda })
            {
                await ChangeStatusAsync(orderId, s);
            }

            (await NotifyAsync([orderId], ownCargo: false)).Single().Outcome.Should().Be(NotifyOutcomes.NotRequired);
            var noTracking = (await NotifyAsync([orderId], ownCargo: true, cargo: new CargoInput(null, null, null))).Single();
            noTracking.Outcome.Should().Be(NotifyOutcomes.Rejected);
            noTracking.Message.Should().Contain("takip numarası");
            (await NotifyAsync([orderId], ownCargo: true, cargo: new CargoInput("BILINMEYEN-KARGO", "123", null))).Single().Message
                .Should().Contain("kargo listesinde yok", "M1 referans listesi doluysa kod doğrulanır");
            (await NotifyAsync([orderId], ownCargo: true, cargo: new CargoInput("MOCK-KARGO-1", "TRK-1", null))).Single().Outcome
                .Should().Be(NotifyOutcomes.Queued);

            // OTOMATİK mod: panel geçişi Hazırlanıyor bildirimini kendiliğinden kuyruğa alır
            (await db.Api.PutAsJsonAsync($"/api/channels/{channelId}/notification-mode", new { mode = "OTOMATIK" }, Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
            await ChangeStatusAsync(autoOrder, OrderStatus.Onaylandi);
            await ChangeStatusAsync(autoOrder, OrderStatus.Hazirlaniyor);
            (await CommandsAsync(autoOrder)).Should().ContainSingle().Which.State.Should().Be(ChannelCommandStates.Pending);
        }
        finally
        {
            await db.Api.PutAsJsonAsync($"/api/channels/{channelId}/notification-mode", new { mode = "ONAYLA_GONDER" }, Ct);
            Mock.SetReferences(Channel, ChannelReferenceType.CargoProvider, []);
            await SetCapabilityAsync(channelId, ChannelCapabilities.OrderStatus, CapabilityStates.Closed);
            await CancelPendingAsync(channelId);
        }
    }

    [Fact]
    public async Task Kanal_hatasinda_ustel_bekleme_besinci_hatada_HATA_engelde_deneme_sayilmaz_yeniden_dene_sifirlar()
    {
        db.SkipIfUnavailable();
        var channelId = await ChannelIdAsync();
        var no = NewOrderNo();
        var orderId = await IngestAsync(no, OrderStatus.Yeni);
        try
        {
            await SetCapabilityAsync(channelId, ChannelCapabilities.OrderStatus, CapabilityStates.Open);
            await ChangeStatusAsync(orderId, OrderStatus.Onaylandi);
            await ChangeStatusAsync(orderId, OrderStatus.Hazirlaniyor);
            (await NotifyAsync([orderId])).Single().Outcome.Should().Be(NotifyOutcomes.Queued);
            var commandId = (await CommandsAsync(orderId)).Single().Id;

            Mock.FailStatusUpdate[Channel] = new ChannelApiException(Channel, "Kanal geçici hata verdi.", 503, transient: true);
            for (var i = 1; i <= 5; i++)
            {
                await MakeDueAsync(commandId);
                var r = await RunJobAsync();
                r.Claimed.Should().Be(1);
                var cmd = (await CommandsAsync(orderId)).Single();
                cmd.Attempts.Should().Be(i);
                cmd.State.Should().Be(i < 5 ? ChannelCommandStates.Pending : ChannelCommandStates.Failed);
                if (i < 5)
                {
                    cmd.NextAttemptUtc.Should().BeAfter(DateTime.UtcNow, "üstel bekleme");
                }
            }
            (await CommandsAsync(orderId)).Single().Error.Should().Be("Kanal geçici hata verdi.");

            // Yeniden dene → sayaç sıfırlanır; engel (yetenek kapalı) deneme sayılmaz
            Mock.FailStatusUpdate.TryRemove(Channel, out _);
            Mock.FailStatusUpdate[Channel] = new ChannelWriteBlockedException(Channel, "SIPARIS_DURUM", "Yetenek kapalı.");
            (await db.Api.PostAsync($"/api/channel-commands/{commandId}/retry", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await RunJobAsync()).Blocked.Should().Be(1);
            var blocked = (await CommandsAsync(orderId)).Single();
            blocked.State.Should().Be(ChannelCommandStates.Pending);
            blocked.Attempts.Should().Be(0, "engel hata değildir");
            blocked.Error.Should().StartWith("Bekliyor:");

            Mock.FailStatusUpdate.TryRemove(Channel, out _);
            await MakeDueAsync(commandId);
            (await RunJobAsync()).Sent.Should().Be(1);
            Mock.StatusUpdatesOf(no).Should().ContainSingle();
        }
        finally
        {
            Mock.FailStatusUpdate.TryRemove(Channel, out _);
            await SetCapabilityAsync(channelId, ChannelCapabilities.OrderStatus, CapabilityStates.Closed);
        }
    }

    // ------------------------------------------------------------------ yardımcılar

    private sealed class FixedOptions(ChannelWriteOptions value) : IOptionsMonitor<ChannelWriteOptions>
    {
        public ChannelWriteOptions CurrentValue => value;
        public ChannelWriteOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<ChannelWriteOptions, string?> listener) => null;
    }

    private static async Task<string> Blocked(Func<Task> act) =>
        (await act.Should().ThrowAsync<ChannelWriteBlockedException>()).Which.Reason;

    private async Task<long> IngestAsync(string no, OrderStatus status)
    {
        using var scope = db.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IOrderIngestionService>().IngestAsync(Channel,
            new NormalizedOrder(no, DateTime.UtcNow, status, 50m, [new NormalizedOrderLine(null, "BC-" + no, null, 2, 25m, ChannelLineId: "L-" + no)],
                null, null, ChannelPackageId: "P-" + no), Ct);
        return result.OrderId;
    }

    private async Task ChangeStatusAsync(long orderId, OrderStatus status) =>
        (await db.Api.PostAsJsonAsync($"/api/orders/{orderId}/status", new { status }, ExcelImportApiTests.Json, Ct)).EnsureSuccessStatusCode();

    private async Task<IReadOnlyList<OrderNotifyItemResult>> NotifyAsync(long[] orderIds, bool ownCargo = false, CargoInput? cargo = null)
    {
        var response = await db.Api.PostAsJsonAsync("/api/orders/notifications", new { orderIds, ownCargo, cargo }, ExcelImportApiTests.Json, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrderNotifyResult>(ExcelImportApiTests.Json, Ct))!.Items;
    }

    private async Task<List<ChannelCommandDto>> CommandsAsync(long orderId) =>
        (await db.Api.GetFromJsonAsync<List<ChannelCommandDto>>($"/api/orders/{orderId}/notifications", ExcelImportApiTests.Json, Ct))!;

    private async Task<ChannelCommandJob.RunResult> RunJobAsync()
    {
        using var scope = db.Services.CreateScope();
        return await ActivatorUtilities.CreateInstance<ChannelCommandJob>(scope.ServiceProvider).RunAsync(Channel, Ct);
    }

    private async Task MakeDueAsync(long commandId)
    {
        await using var c = db.Open();
        await c.ExecuteAsync("UPDATE dbo.ChannelCommand SET SonrakiDeneme = NULL WHERE Id = @commandId", new { commandId });
    }

    private async Task CancelPendingAsync(int channelId)
    {
        await using var c = db.Open();
        await c.ExecuteAsync("UPDATE dbo.ChannelCommand SET Durum = 'IPTAL' WHERE ChannelId = @channelId AND Durum IN ('ONAY_BEKLIYOR', 'BEKLIYOR')", new { channelId });
    }

    private async Task SetCapabilityAsync(int channelId, string capability, string state)
    {
        using var scope = db.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IChannelCapabilityService>().SetAsync(channelId, capability, state, "test", Ct);
    }

    private async Task<int> ChannelIdAsync()
    {
        await using var c = db.Open();
        return await c.ExecuteScalarAsync<int>("SELECT Id FROM dbo.Channel WHERE Kod = @Channel", new { Channel });
    }
}
