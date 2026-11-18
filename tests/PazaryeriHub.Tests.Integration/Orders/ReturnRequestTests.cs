using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using PazaryeriHub.Adapters.Mock;
using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Orders;
using PazaryeriHub.Domain.Orders;
using PazaryeriHub.Infrastructure.Channels;
using PazaryeriHub.Infrastructure.Orders;
using PazaryeriHub.Tests.Integration.Excel;
using PazaryeriHub.Tests.Integration.Infrastructure;

namespace PazaryeriHub.Tests.Integration.Orders;

/// <summary>
/// M3 (ADR-027): IadeCek (mock kanal), tekil kayıt, depoya giriş (RL:, bir kez, sevk şartı, eski yolla çift giriş yok),
/// Yönetici kararı (denetim izi, gerekçe doğrulaması, yetenek) ve kanala gönderim.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ReturnRequestTests(TestDatabase db)
{
    private const string Channel = "SOFTCOTTON"; // mock kanal

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MockChannelStore Mock => db.Services.GetRequiredService<MockChannelStore>();

    [Fact]
    public async Task Ayni_iade_bes_kez_gelse_tek_kayit_depoya_giris_bir_kez_ve_sevk_sarti()
    {
        db.SkipIfUnavailable();
        var (orderNo, barcode, skuId) = await ShippedOrderAsync();
        var claimNo = "CLM-" + Guid.NewGuid().ToString("N")[..10];
        var unknownClaim = "CLM-" + Guid.NewGuid().ToString("N")[..10];
        Mock.AddReturn(Channel, new NormalizedReturn(orderNo, claimNo, DateTime.UtcNow, [new(null, barcode, 1, "L-" + orderNo, ["ci-1"])], "Beden uymadı", "WaitingInAction"));
        Mock.AddReturn(Channel, new NormalizedReturn("YOK-" + orderNo, unknownClaim, DateTime.UtcNow, [new(null, barcode, 1, "X1", ["ci-9"])], null, "Created"));

        for (var i = 0; i < 5; i++)
        {
            await FetchAsync();
        }

        await using (var c = db.Open())
        {
            (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReturnRequest WHERE KanalIadeNo = @claimNo", new { claimNo })).Should().Be(1);
            (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReturnRequestLine l JOIN dbo.ReturnRequest r ON r.Id = l.ReturnRequestId WHERE r.KanalIadeNo = @claimNo", new { claimNo }))
                .Should().Be(1);
        }
        var list = await db.Api.GetFromJsonAsync<PagedResult<ReturnListItem>>($"/api/returns?search={claimNo}", ExcelImportApiTests.Json, Ct);
        var item = list!.Items.Single();
        item.OrderId.Should().NotBeNull("sipariş no ile eşleşir");
        item.ChannelStatus.Should().Be("WaitingInAction");
        var detail = await db.Api.GetFromJsonAsync<ReturnDetail>($"/api/returns/{item.Id}", ExcelImportApiTests.Json, Ct);
        var line = detail!.Lines.Single();
        line.SkuId.Should().Be(skuId);
        line.OrderLineId.Should().NotBeNull();
        line.ReceiveProblem.Should().BeNull();

        // Depo: depoya giriş (bir kez)
        var physicalBefore = await PhysicalAsync(skuId);
        using var depo = await db.ApiHost.CreateClientAsAsync(Roles.Warehouse);
        (await depo.PostAsync($"/api/returns/{item.Id}/lines/{line.Id}/receive", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PhysicalAsync(skuId)).Should().Be(physicalBefore + 1);
        (await depo.PostAsync($"/api/returns/{item.Id}/lines/{line.Id}/receive", null, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PhysicalAsync(skuId)).Should().Be(physicalBefore + 1, "ikinci giriş stoğu etkilemez");

        // Eski sipariş ekranı yolu aynı satırı ikinci kez giremez
        await IngestAsync(orderNo, barcode, OrderStatus.Iade);
        var orderLineId = line.OrderLineId!.Value;
        var old = await depo.PostAsync($"/api/orders/{item.OrderId}/lines/{orderLineId}/return-receipt", null, Ct);
        old.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await old.Content.ReadAsStringAsync(Ct)).Should().Contain("kanal iade talebi");

        // Siparişi bu uygulamada olmayan iade stoğa girilemez
        var unknown = (await db.Api.GetFromJsonAsync<PagedResult<ReturnListItem>>($"/api/returns?search={unknownClaim}", ExcelImportApiTests.Json, Ct))!.Items.Single();
        var unknownDetail = await db.Api.GetFromJsonAsync<ReturnDetail>($"/api/returns/{unknown.Id}", ExcelImportApiTests.Json, Ct);
        unknownDetail!.Lines.Single().ReceiveProblem.Should().Contain("eşleşmedi");
        (await depo.PostAsync($"/api/returns/{unknown.Id}/lines/{unknownDetail.Lines.Single().Id}/receive", null, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Karar_yalnizca_yonetici_gerekce_dogrulanir_denetim_izi_yazilir_ve_kanala_gider()
    {
        db.SkipIfUnavailable();
        var channelId = await ChannelIdAsync();
        var (orderNo, barcode, _) = await ShippedOrderAsync();
        var claimNo = "CLM-" + Guid.NewGuid().ToString("N")[..10];
        Mock.AddReturn(Channel, new NormalizedReturn(orderNo, claimNo, DateTime.UtcNow, [new(null, barcode, 1, "L-" + orderNo, ["ci-1", "ci-2"])], null, "WaitingInAction"));
        await FetchAsync();
        var id = (await db.Api.GetFromJsonAsync<PagedResult<ReturnListItem>>($"/api/returns?search={claimNo}", ExcelImportApiTests.Json, Ct))!.Items.Single().Id;

        // M1 red gerekçe listesi (deterministik)
        Mock.SetReferences(Channel, ChannelReferenceType.ClaimRejectReason, [new("R-OK", "Geçerli gerekçe")]);
        using (var scope = db.Services.CreateScope())
        {
            await ActivatorUtilities.CreateInstance<ReferenceFetchJob>(scope.ServiceProvider).RunAsync(Channel, scheduled: false, Ct);
        }
        try
        {
            using var depo = await db.ApiHost.CreateClientAsAsync(Roles.Warehouse);
            (await depo.PostAsJsonAsync($"/api/returns/{id}/decision", new { decision = "ONAY" }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "karar yalnızca Yönetici");

            await SetCapabilityAsync(channelId, CapabilityStates.Closed);
            (await db.Api.PostAsJsonAsync($"/api/returns/{id}/decision", new { decision = "ONAY" }, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict, "yetenek kapalı");

            await SetCapabilityAsync(channelId, CapabilityStates.Open);
            var approve = await DecideAsync(id, new { decision = "ONAY" });
            approve.Outcome.Should().Be("KUYRUGA_ALINDI");
            (await DecideAsync(id, new { decision = "ONAY" })).Outcome.Should().Be("ZATEN_VAR");

            (await db.Api.PostAsJsonAsync($"/api/returns/{id}/decision", new { decision = "RED" }, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "gerekçe zorunlu");
            (await db.Api.PostAsJsonAsync($"/api/returns/{id}/decision", new { decision = "RED", reasonCode = "R-YOK" }, Ct)).StatusCode
                .Should().Be(HttpStatusCode.BadRequest, "kanal listesinde olmayan gerekçe");
            (await DecideAsync(id, new { decision = "RED", reasonCode = "R-OK", description = "Ürün hasarlı geldi" })).Outcome.Should().Be("KUYRUGA_ALINDI");

            using (var scope = db.Services.CreateScope())
            {
                (await ActivatorUtilities.CreateInstance<ChannelCommandJob>(scope.ServiceProvider).RunAsync(Channel, Ct)).Sent.Should().BeGreaterThanOrEqualTo(2);
            }
            var sent = Mock.ReturnDecisions.Where(d => d.ReturnNo == claimNo).ToList();
            sent.Select(d => d.Kind).Should().Equal(ReturnDecisionKind.Approve, ReturnDecisionKind.Reject);
            sent[0].Lines.Single().ChannelItemIds.Should().Equal("ci-1", "ci-2");
            sent[1].ReasonCode.Should().Be("R-OK");

            var detail = await db.Api.GetFromJsonAsync<ReturnDetail>($"/api/returns/{id}", ExcelImportApiTests.Json, Ct);
            detail!.Decisions.Should().HaveCount(2);
            detail.Decisions.Should().OnlyContain(d => !string.IsNullOrEmpty(d.User) && d.CommandState == ChannelCommandStates.Sent);
            detail.Decisions[1].ReasonCode.Should().Be("R-OK");
        }
        finally
        {
            await SetCapabilityAsync(channelId, CapabilityStates.Closed);
            Mock.SetReferences(Channel, ChannelReferenceType.ClaimRejectReason, []);
        }
    }

    // ------------------------------------------------------------------ yardımcılar

    private async Task<(string OrderNo, string Barcode, int SkuId)> ShippedOrderAsync()
    {
        var barcode = "869" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999);
        var skuId = await db.CreateSkuAsync(barcode);
        await db.Ledger.ReceiveAsync(new(skuId, db.DefaultWarehouseId, 5), Ct);
        var orderNo = "RTN-" + Guid.NewGuid().ToString("N")[..12];
        await IngestAsync(orderNo, barcode, OrderStatus.Kargoda); // ileri atlama: rezerve + sevk
        return (orderNo, barcode, skuId);
    }

    private async Task IngestAsync(string orderNo, string barcode, OrderStatus status)
    {
        using var scope = db.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IOrderIngestionService>().IngestAsync(Channel,
            new NormalizedOrder(orderNo, DateTime.UtcNow, status, 30m, [new NormalizedOrderLine(null, barcode, null, 1, 30m, ChannelLineId: "L-" + orderNo)]), Ct);
    }

    private async Task FetchAsync()
    {
        using var scope = db.Services.CreateScope();
        await ActivatorUtilities.CreateInstance<ReturnFetchJob>(scope.ServiceProvider).RunAsync(Channel, scheduled: false, Ct);
    }

    private async Task<ReturnDecisionResult> DecideAsync(long id, object body)
    {
        var response = await db.Api.PostAsJsonAsync($"/api/returns/{id}/decision", body, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ReturnDecisionResult>(ExcelImportApiTests.Json, Ct))!;
    }

    private async Task<int> PhysicalAsync(int skuId)
    {
        await using var c = db.Open();
        return await c.ExecuteScalarAsync<int>("SELECT ISNULL(SUM(Fiziksel), 0) FROM dbo.StockBalance WHERE SkuId = @skuId", new { skuId });
    }

    private async Task SetCapabilityAsync(int channelId, string state)
    {
        using var scope = db.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IChannelCapabilityService>().SetAsync(channelId, ChannelCapabilities.ReturnDecision, state, "test", Ct);
    }

    private async Task<int> ChannelIdAsync()
    {
        await using var c = db.Open();
        return await c.ExecuteScalarAsync<int>("SELECT Id FROM dbo.Channel WHERE Kod = @Channel", new { Channel });
    }
}
