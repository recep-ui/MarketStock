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
    private readonly ConcurrentDictionary<string, List<OrderStatusUpdate>> _statusUpdates = new(StringComparer.Ordinal);

    private readonly ConcurrentQueue<ReturnDecisionRequest> _decisions = new();

    /// <summary>Mock kanala bildirilmiş iade kararları.</summary>
    public IReadOnlyList<ReturnDecisionRequest> ReturnDecisions => _decisions.ToList();

    public void RecordReturnDecision(ReturnDecisionRequest request) => _decisions.Enqueue(request);

    /// <summary>Kanal için ayarlanırsa her UpdateOrderStatusAsync bu hatayı fırlatır (kaldırılana kadar).</summary>
    public ConcurrentDictionary<string, Exception> FailStatusUpdate { get; } = new(StringComparer.Ordinal);

    /// <summary>Mock kanala bildirilmiş durum güncellemeleri (sırasıyla).</summary>
    public IReadOnlyList<OrderStatusUpdate> StatusUpdatesOf(string channelOrderNo)
    {
        if (!_statusUpdates.TryGetValue(channelOrderNo, out var list))
        {
            return [];
        }
        lock (list)
        {
            return list.ToList();
        }
    }
    private readonly ConcurrentDictionary<string, IReadOnlyList<ChannelCatalogEntry>> _catalog = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Channel, ChannelReferenceType Type), IReadOnlyList<ChannelReferenceItem>> _references = new();

    /// <summary>Mock kanalın referans listesini ayarlar (öncekinin yerine geçer). Ayarlanmamışsa örnek liste döner.</summary>
    public void SetReferences(string channelCode, ChannelReferenceType type, IEnumerable<ChannelReferenceItem> items) =>
        _references[(channelCode, type)] = items.ToList();

    /// <summary>Bu kanal + tip için FetchReferenceAsync NotSupportedException fırlatır (desteklemeyen kanal senaryosu).</summary>
    public ConcurrentDictionary<(string Channel, ChannelReferenceType Type), bool> UnsupportedReferences { get; } = new();

    /// <summary>Sonraki FetchReferenceAsync çağrısında fırlatılacak hata.</summary>
    public ConcurrentDictionary<string, Exception> FailNextReference { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<ChannelReferenceItem> ReferencesOf(string channelCode, ChannelReferenceType type) =>
        _references.TryGetValue((channelCode, type), out var list) ? list
        : type == ChannelReferenceType.CargoProvider
            ? [new("MOCK-KARGO-1", "Mock Kargo 1"), new("MOCK-KARGO-2", "Mock Kargo 2")]
            : [new("MOCK-RED-1", "Mock red gerekçesi 1")];

    /// <summary>Mock kanal kataloğunun sayfa boyutu (sayfalama testleri için değiştirilebilir).</summary>
    public int CatalogPageSize { get; set; } = 100;

    /// <summary>Sonraki FetchCatalogAsync çağrısında fırlatılacak hata.</summary>
    public ConcurrentDictionary<string, Exception> FailNextCatalog { get; } = new(StringComparer.Ordinal);

    /// <summary>Kanalın ürün kataloğunu ayarlar (öncekinin yerine geçer).</summary>
    public void SetCatalog(string channelCode, IEnumerable<ChannelCatalogEntry> entries) => _catalog[channelCode] = entries.ToList();

    public IReadOnlyList<ChannelCatalogEntry> CatalogOf(string channelCode) => _catalog.TryGetValue(channelCode, out var c) ? c : [];

    public TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Sonraki FetchOrdersAsync çağrısında fırlatılacak hata (hata senaryosu testleri için).</summary>
    public ConcurrentDictionary<string, Exception> FailNextFetch { get; } = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PazaryeriHub.Application.Questions.NormalizedQuestion>> _questions = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Channel, PazaryeriHub.Application.Questions.QuestionAnswerRequest Answer)> _answers = new();

    /// <summary>Kanala soru ekler/günceller (aynı numara → güncelleme).</summary>
    public void UpsertQuestion(string channelCode, PazaryeriHub.Application.Questions.NormalizedQuestion question) =>
        _questions.GetOrAdd(channelCode, _ => new(StringComparer.Ordinal))[question.QuestionNo] = question;

    public IReadOnlyList<PazaryeriHub.Application.Questions.NormalizedQuestion> QuestionsOf(string channelCode) =>
        _questions.TryGetValue(channelCode, out var q) ? q.Values.ToList() : [];

    /// <summary>Kanala giden cevaplar (sırayla; "kaç kez gitti" kontrolü için).</summary>
    public IReadOnlyList<PazaryeriHub.Application.Questions.QuestionAnswerRequest> AnswersOf(string channelCode) =>
        _answers.Where(a => a.Channel == channelCode).Select(a => a.Answer).ToList();

    /// <summary>Mock cevap: soru cevaplandı durumuna geçer (gerçek kanaldaki gibi bir sonraki çekimde görünür).</summary>
    public void RecordAnswer(string channelCode, PazaryeriHub.Application.Questions.QuestionAnswerRequest answer)
    {
        _answers.Enqueue((channelCode, answer));
        if (_questions.TryGetValue(channelCode, out var q) && q.TryGetValue(answer.QuestionNo, out var question))
        {
            q[answer.QuestionNo] = question with { State = PazaryeriHub.Application.Questions.QuestionChannelState.Answered, AnswerText = answer.Text, ChannelStatus = "ANSWERED" };
        }
    }

    /// <summary>Kanal için ayarlanırsa soru okuma "desteklenmiyor" döner (doğrulanmamış kanal senaryosu).</summary>
    public ConcurrentDictionary<string, bool> UnsupportedQuestions { get; } = new(StringComparer.Ordinal);

    /// <summary>Kanal için ayarlanırsa her PushPriceAsync bu hatayı fırlatır (kaldırılana kadar).</summary>
    public ConcurrentDictionary<string, Exception> FailPricePush { get; } = new(StringComparer.Ordinal);

    /// <summary>Kanalın fiyat kuralını değiştirir (ör. KDV doğrulanmamış kanal senaryosu).</summary>
    public ConcurrentDictionary<string, PriceUpdateRule> PriceRules { get; } = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, (decimal ListPrice, decimal SalePrice)>> _prices = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Channel, PriceUpdate Update)> _pricePushes = new();

    /// <summary>Kanaldaki fiyatlar (kod veya barkod anahtarıyla).</summary>
    public ConcurrentDictionary<string, (decimal ListPrice, decimal SalePrice)> PricesOf(string channelCode) =>
        _prices.GetOrAdd(channelCode, _ => new(StringComparer.Ordinal));

    /// <summary>Kanala giden tüm fiyat kalemleri (sırayla; "kaç kez gitti" kontrolü için).</summary>
    public IReadOnlyList<PriceUpdate> PricePushesOf(string channelCode) =>
        _pricePushes.Where(p => p.Channel == channelCode).Select(p => p.Update).ToList();

    public void RecordPricePush(string channelCode, IEnumerable<PriceUpdate> updates)
    {
        foreach (var u in updates)
        {
            _pricePushes.Enqueue((channelCode, u));
        }
    }

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

    public void RecordStatusUpdate(OrderStatusUpdate update)
    {
        var list = _statusUpdates.GetOrAdd(update.ChannelOrderNo, _ => []);
        lock (list)
        {
            list.Add(update);
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

    /// <summary>Mock fiyat: liste + satış fiyatı kod/barkod anahtarıyla tutulur; başarısız kalemler <see cref="MockChannelStore.FailingItems"/>.</summary>
    public Task<BatchResult> PushPriceAsync(IReadOnlyList<PriceUpdate> updates, CancellationToken ct)
    {
        if (store.FailPricePush.TryGetValue(ChannelCode, out var error))
        {
            throw error;
        }

        var batchId = "mock-" + Guid.NewGuid().ToString("N");
        var prices = store.PricesOf(ChannelCode);
        var errors = new List<BatchItemError>();
        foreach (var u in updates)
        {
            var key = u.ChannelProductCode ?? u.Barcode ?? "";
            if (store.FailingItems.TryGetValue(key, out var message))
            {
                errors.Add(new BatchItemError(u.ChannelProductCode, u.Barcode, message));
                continue;
            }
            prices[key] = (u.ListPrice, u.SalePrice);
        }
        store.RecordPricePush(ChannelCode, updates);
        store.BatchErrors[batchId] = errors;
        return Task.FromResult(new BatchResult(batchId, Accepted: true));
    }

    public Task<IReadOnlyList<PazaryeriHub.Application.Questions.NormalizedQuestion>> FetchQuestionsAsync(DateTime sinceUtc, CancellationToken ct) =>
        store.UnsupportedQuestions.ContainsKey(ChannelCode)
            ? throw new NotSupportedException("Mock: bu kanalın soru API'si doğrulanmadı.")
            : Task.FromResult(store.QuestionsOf(ChannelCode));

    public Task AnswerQuestionAsync(PazaryeriHub.Application.Questions.QuestionAnswerRequest request, CancellationToken ct)
    {
        store.RecordAnswer(ChannelCode, request);
        return Task.CompletedTask;
    }

    public PazaryeriHub.Application.Questions.QuestionAnswerRule DescribeQuestionAnswer() =>
        new(!store.UnsupportedQuestions.ContainsKey(ChannelCode), 10, 2000, "Mock kanal (gerçek API çağrısı yok).");

    /// <summary>Mock: KDV dahil kabul edilir (gerçek kanal çağrısı yok). Testler <see cref="MockChannelStore.PriceRules"/> ile değiştirebilir.</summary>
    public PriceUpdateRule DescribePriceUpdate() =>
        store.PriceRules.TryGetValue(ChannelCode, out var rule)
            ? rule
            : new PriceUpdateRule(true, true, true, false, "Mock kanal (gerçek API çağrısı yok).");

    public Task<BatchStatus> CheckBatchAsync(string batchId, CancellationToken ct)
    {
        var errors = store.BatchErrors.TryGetValue(batchId, out var e) ? e : [];
        return Task.FromResult(new BatchStatus(batchId, errors.Count == 0 ? BatchState.Completed : BatchState.PartiallyFailed, errors));
    }

    public Task UpdateOrderStatusAsync(OrderStatusUpdate update, CancellationToken ct)
    {
        if (store.FailStatusUpdate.TryGetValue(ChannelCode, out var error))
        {
            throw error;
        }
        store.RecordStatusUpdate(update);
        return Task.CompletedTask;
    }

    public Task DecideReturnAsync(ReturnDecisionRequest request, CancellationToken ct)
    {
        store.RecordReturnDecision(request);
        return Task.CompletedTask;
    }

    /// <summary>Mock: tüm kararlar desteklenir; red gerekçesi zorunlu (kanalın referans listesinden).</summary>
    public ReturnDecisionRule DescribeReturnDecision(ReturnDecisionKind kind) =>
        kind == ReturnDecisionKind.Reject ? new(true, "Mock: red.", RequiresReason: true) : new(true, "Mock.");

    /// <summary>Mock: Hazırlanıyor desteklenir; Kargoda kendi kargoda takip no zorunlu, kanal kargosunda bildirim gerekmez.</summary>
    public OrderNotificationRule DescribeOrderNotification(OrderStatus target, bool ownCargo) => target switch
    {
        OrderStatus.Hazirlaniyor => new(true, false, "Mock: hazırlanıyor bildirimi."),
        OrderStatus.Kargoda when !ownCargo => OrderNotificationRule.NotRequiredBecause("Kanal kargosu: kanal durumu kendisi günceller."),
        OrderStatus.Kargoda => new(true, false, "Mock: kendi kargo ile kargoda bildirimi.", RequiresTrackingNumber: true),
        _ => OrderNotificationRule.NotSupportedBecause("Bu durum için kanala bildirim yapılmaz.")
    };

    public Task<IReadOnlyList<NormalizedReturn>> FetchReturnsAsync(DateTime sinceUtc, CancellationToken ct) =>
        Task.FromResult(store.ReturnsSince(ChannelCode, sinceUtc));

    public Task<ChannelReferenceList> FetchReferenceAsync(ChannelReferenceType type, CancellationToken ct)
    {
        if (store.FailNextReference.TryRemove(ChannelCode, out var error))
        {
            throw error;
        }
        if (store.UnsupportedReferences.ContainsKey((ChannelCode, type)))
        {
            throw new NotSupportedException($"Mock: {ChannelCode} için {type} desteklenmiyor.");
        }
        return Task.FromResult(new ChannelReferenceList(store.ReferencesOf(ChannelCode, type), ReferenceSource.Api));
    }

    /// <summary>Mock katalog: imleç = sıradaki öğenin konumu.</summary>
    public Task<ChannelCatalogPage> FetchCatalogAsync(string? cursor, CancellationToken ct)
    {
        if (store.FailNextCatalog.TryRemove(ChannelCode, out var error))
        {
            throw error;
        }
        var all = store.CatalogOf(ChannelCode);
        var offset = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
        var size = Math.Max(1, store.CatalogPageSize);
        var page = all.Skip(offset).Take(size).ToList();
        var next = offset + page.Count < all.Count ? (offset + page.Count).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return Task.FromResult(new ChannelCatalogPage(page, next));
    }
}
