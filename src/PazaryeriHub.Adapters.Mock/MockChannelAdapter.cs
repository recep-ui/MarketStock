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

    /// <summary>Kanal için ayarlanırsa her PushStockAsync bu hatayı fırlatır (kaldırılana kadar).</summary>
    public ConcurrentDictionary<string, Exception> FailPush { get; } = new(StringComparer.Ordinal);

    /// <summary>Bu kodlar/barkodlar toplu işlemde başarısız döner (CheckBatch → PartiallyFailed); stokları güncellenmez.</summary>
    public ConcurrentDictionary<string, string> FailingItems { get; } = new(StringComparer.Ordinal);

    /// <summary>Toplu işlem → başarısız kalemler.</summary>
    internal ConcurrentDictionary<string, List<BatchItemError>> BatchErrors { get; } = new(StringComparer.Ordinal);

    /// <summary>Kanaldaki stoğu doğrudan değiştirir (mutabakat sapması senaryosu: kanal değeri bizim dışımızda değişti).</summary>
    public void SetChannelStock(string channelCode, string key, int quantity) => StockOf(channelCode)[key] = quantity;

    /// <summary>Kanaldaki stok (kod veya barkod anahtarıyla); yoksa null.</summary>
    public int? GetChannelStock(string channelCode, string key) => StockOf(channelCode).TryGetValue(key, out var q) ? q : null;

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
        // Mock'ta tek anahtar tutulur; hem kod hem barkod olarak döndürülür.
        IReadOnlyList<ChannelStock> result = stock
            .Where(kv => filter is null || filter.Contains(kv.Key))
            .Select(kv => new ChannelStock(kv.Key, kv.Key, kv.Value))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<BatchResult> PushStockAsync(IReadOnlyList<StockUpdate> updates, CancellationToken ct)
    {
        if (store.FailPush.TryGetValue(ChannelCode, out var error))
        {
            throw error;
        }

        var batchId = "mock-" + Guid.NewGuid().ToString("N");
        var stock = store.StockOf(ChannelCode);
        var errors = new List<BatchItemError>();
        foreach (var u in updates)
        {
            var key = u.ChannelProductCode ?? u.Barcode ?? "";
            if (store.FailingItems.TryGetValue(key, out var message))
            {
                errors.Add(new BatchItemError(u.ChannelProductCode, u.Barcode, message));
                continue;
            }
            stock[key] = u.Quantity;
        }
        store.BatchErrors[batchId] = errors;
        return Task.FromResult(new BatchResult(batchId, Accepted: true));
    }

    public Task<BatchResult> PushPriceAsync(IReadOnlyList<PriceUpdate> updates, CancellationToken ct) =>
        Task.FromResult(new BatchResult("mock-" + Guid.NewGuid().ToString("N"), Accepted: true));

    public Task<BatchStatus> CheckBatchAsync(string batchId, CancellationToken ct)
    {
        var errors = store.BatchErrors.TryGetValue(batchId, out var e) ? e : [];
        return Task.FromResult(new BatchStatus(batchId, errors.Count == 0 ? BatchState.Completed : BatchState.PartiallyFailed, errors));
    }

    public Task UpdateOrderStatusAsync(string channelOrderNo, OrderStatus status, CargoInfo? cargo, CancellationToken ct)
    {
        store.RecordStatusUpdate(channelOrderNo, status, cargo);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NormalizedReturn>> FetchReturnsAsync(DateTime sinceUtc, CancellationToken ct) =>
        Task.FromResult(store.ReturnsSince(ChannelCode, sinceUtc));
}
