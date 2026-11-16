namespace PazaryeriHub.Domain.Orders;

/// <summary>Sipariş durumu. Değer adları veritabanındaki <c>[Order].Durum</c> değerleriyle birebir aynıdır.</summary>
public enum OrderStatus
{
    Yeni,
    Onaylandi,
    Hazirlaniyor,
    Kargoda,
    TeslimEdildi,
    Iptal,
    Iade
}

/// <summary>
/// Sipariş durum makinesi (CLAUDE.md §7), saf fonksiyon:
/// <code>
/// Yeni → Onaylandi → Hazirlaniyor → Kargoda → TeslimEdildi
/// Yeni / Onaylandi / Hazirlaniyor → Iptal
/// Kargoda / TeslimEdildi → Iade
/// </code>
/// Iptal ve Iade son durumlardır.
/// </summary>
public static class OrderStateMachine
{
    private static readonly Dictionary<OrderStatus, OrderStatus[]> Allowed = new()
    {
        [OrderStatus.Yeni] = [OrderStatus.Onaylandi, OrderStatus.Iptal],
        [OrderStatus.Onaylandi] = [OrderStatus.Hazirlaniyor, OrderStatus.Iptal],
        [OrderStatus.Hazirlaniyor] = [OrderStatus.Kargoda, OrderStatus.Iptal],
        [OrderStatus.Kargoda] = [OrderStatus.TeslimEdildi, OrderStatus.Iade],
        [OrderStatus.TeslimEdildi] = [OrderStatus.Iade],
        [OrderStatus.Iptal] = [],
        [OrderStatus.Iade] = []
    };

    /// <summary>Tek adımlık geçiş izinli mi?</summary>
    public static bool CanTransition(OrderStatus from, OrderStatus to) => Allowed[from].Contains(to);

    public static IReadOnlyList<OrderStatus> NextStates(OrderStatus from) => Allowed[from];

    public static bool IsTerminal(OrderStatus status) => Allowed[status].Length == 0;

    /// <summary>
    /// <paramref name="from"/>'dan <paramref name="to"/>'ya izinli adımlarla en kısa yol (başlangıç hariç, hedef dahil).
    /// Aynı durumsa boş liste; yol yoksa null. Kanaldan gelen "ileri atlamalar" (ör. Yeni → Kargoda) bu yolla
    /// ara adımlar kaydedilerek uygulanır; geri dönüş ve son durumdan çıkış her zaman reddedilir.
    /// </summary>
    public static IReadOnlyList<OrderStatus>? FindPath(OrderStatus from, OrderStatus to)
    {
        if (from == to)
        {
            return [];
        }

        var previous = new Dictionary<OrderStatus, OrderStatus> { [from] = from };
        var queue = new Queue<OrderStatus>([from]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in Allowed[current])
            {
                if (!previous.TryAdd(next, current))
                {
                    continue;
                }
                if (next == to)
                {
                    var path = new List<OrderStatus> { to };
                    for (var s = current; s != from; s = previous[s])
                    {
                        path.Add(s);
                    }
                    path.Reverse();
                    return path;
                }
                queue.Enqueue(next);
            }
        }
        return null;
    }

    /// <summary>Stok açısından sipariş satırının olması gereken hali.</summary>
    public static StockIntent StockIntentFor(OrderStatus status) => status switch
    {
        OrderStatus.Yeni or OrderStatus.Onaylandi or OrderStatus.Hazirlaniyor => StockIntent.Reserved,
        OrderStatus.Kargoda or OrderStatus.TeslimEdildi or OrderStatus.Iade => StockIntent.Shipped,
        OrderStatus.Iptal => StockIntent.Released,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };
}

/// <summary>
/// Sipariş satırının stok defterindeki hedef hali. Iade'de mal depoya ancak panelden onaylanınca girer (usp_IadeGiris),
/// bu yüzden Iade "sevk edilmiş" sayılır.
/// </summary>
public enum StockIntent
{
    /// <summary>Rezerve edilmiş olmalı.</summary>
    Reserved,

    /// <summary>Sevk edilmiş olmalı (fiziksel↓, rezerve↓).</summary>
    Shipped,

    /// <summary>Rezerve (varsa) iade edilmiş olmalı.</summary>
    Released
}
