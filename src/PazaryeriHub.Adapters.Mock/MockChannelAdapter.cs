using System.Collections.Concurrent;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Domain.Orders;

namespace PazaryeriHub.Adapters.Mock;

/// <summary>
/// Tüm mock kanalların ortak bellek içi durumu (süreç ömrü boyunca). Siparişler, kanal stokları ve gönderim geçmişi burada tutulur.
/// Testler ve geliştirme uç noktaları buraya sipariş "yerleştirir"; adaptör buradan "çeker".
/// </summary>
public sealed class MockChannelStore
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, MockOrder>> _orders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _stock = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<NormalizedReturn>> _returns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<(OrderStatus Status, CargoInfo? Cargo)>> _statusUpdates = new(StringComparer.Ordinal);

    public TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Sonraki FetchOrdersAsync çağrısında fırlatılacak hata (hata senaryosu testleri için).</summary>
    public ConcurrentDictionary<string, Exception> FailNextFetch { get; } = new(StringComparer.Ordinal);

    /// <summary>Siparişi ekler veya günceller (aynı numara → güncelleme; kanalda durum değişimi gibi).</summary>
    public void UpsertOrder(string channelCode, NormalizedOrder order)
    {
        var orders = _orders.GetOrAdd(channelCode, _ => new(StringComparer.Ordinal));
        orders[order.ChannelOrderNo] = new MockOrder(order, Clock.GetUtcNow().UtcDateTime);
    }

    public IReadOnlyList<NormalizedOrder> OrdersModifiedSince(string channelCode, DateTime sinceUtc) =>
        _orders.TryGetValue(channelCode, out var orders)
            ? orders.Values.Where(o => o.ModifiedUtc >= sinceUtc).OrderBy(o => o.ModifiedUtc).Select(o => o.Order).ToList()
            : [];

    public ConcurrentDictionary<string, int> StockOf(string channelCode) => _stock.GetOrAdd(channelCode, _ => new(StringComparer.Ordinal));

    public void AddReturn(string channelCode, NormalizedReturn ret) => _returns.GetOrAdd(channelCode, _ => new()).Enqueue(ret);

    public IReadOnlyList<NormalizedReturn> ReturnsSince(string channelCode, DateTime sinceUtc) =>
        _returns.TryGetValue(channelCode, out var q) ? q.Where(r => r.CreatedUtc >= sinceUtc).ToList() : [];

    public void RecordStatusUpdate(string channelOrderNo, OrderStatus status, CargoInfo? cargo)
    {
        var list = _statusUpdates.GetOrAdd(channelOrderNo, _ => []);
        lock (list)
        {
            list.Add((status, cargo));
        }
    }

    private sealed record MockOrder(NormalizedOrder Order, DateTime ModifiedUtc);
}

/// <summary>
/// Sahte kanal adaptörü. Gerçek pazaryeri API'si ÇAĞIRMAZ. Aşama 6'da her kanal için doğrulanmış dokümantasyonla
/// yazılacak gerçek adaptör bunun yerini alır (Beymen ve softcotton için entegrasyon yöntemi henüz teyit edilmedi).
/// </summary>
public sealed class MockChannelAdapter(string channelCode, MockChannelStore store) : IChannelAdapter
{
    public string ChannelCode { get; } = channelCode;

    public Task<IReadOnlyList<NormalizedOrder>> FetchOrdersAsync(DateTime sinceUtc, CancellationToken ct)
    {
        if (store.FailNextFetch.TryRemove(ChannelCode, out var error))
        {
            throw error;
        }
        return Task.FromResult(store.OrdersModifiedSince(ChannelCode, sinceUtc));
    }

    public Task<IReadOnlyList<ChannelStock>> FetchStockAsync(IEnumerable<string>? skus, CancellationToken ct)
    {
        var stock = store.StockOf(ChannelCode);
        var filter = skus?.ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<ChannelStock> result = stock
            .Where(kv => filter is null || filter.Contains(kv.Key))
            .Select(kv => new ChannelStock(kv.Key, null, kv.Value))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<BatchResult> PushStockAsync(IReadOnlyList<StockUpdate> updates, CancellationToken ct)
    {
        var stock = store.StockOf(ChannelCode);
        foreach (var u in updates)
        {
            stock[u.ChannelProductCode ?? u.Barcode ?? ""] = u.Quantity;
        }
        return Task.FromResult(new BatchResult("mock-" + Guid.NewGuid().ToString("N"), Accepted: true));
    }

    public Task<BatchResult> PushPriceAsync(IReadOnlyList<PriceUpdate> updates, CancellationToken ct) =>
        Task.FromResult(new BatchResult("mock-" + Guid.NewGuid().ToString("N"), Accepted: true));

    public Task<BatchStatus> CheckBatchAsync(string batchId, CancellationToken ct) =>
        Task.FromResult(new BatchStatus(batchId, BatchState.Completed, []));

    public Task UpdateOrderStatusAsync(string channelOrderNo, OrderStatus status, CargoInfo? cargo, CancellationToken ct)
    {
        store.RecordStatusUpdate(channelOrderNo, status, cargo);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NormalizedReturn>> FetchReturnsAsync(DateTime sinceUtc, CancellationToken ct) =>
        Task.FromResult(store.ReturnsSince(ChannelCode, sinceUtc));
}
