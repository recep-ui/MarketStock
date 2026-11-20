namespace PazaryeriHub.Domain.Stock;

/// <summary>Bir SKU'nun tek bir depodaki stok seviyesi.</summary>
public readonly record struct StockLevel(int Physical, int Reserved, int SafetyStock)
{
    /// <summary>Satılabilir = Fiziksel - Rezerve - GuvenlikStogu (negatif olabilir).</summary>
    public int Available => Physical - Reserved - SafetyStock;
}

/// <summary>
/// Kanala gönderilecek stoğu hesaplayan saf fonksiyon (CLAUDE.md §5). Yan etkisi yoktur, veritabanı bilmez.
/// <code>
/// Satılabilir     = Σ (Fiziksel - Rezerve - GuvenlikStogu)          (tüm depolar)
/// Kanala gidecek  = MIN( FLOOR(Satılabilir * PayYuzde / 100), kanal üst limiti )
/// Negatif sonuç 0'a sabitlenir.
/// Satılabilir &lt;= genel düşük stok eşiği ise 0 (ADR-023; eşik 0 → yalnızca negatif/0 sıfırlanır, mevcut davranış).
/// </code>
/// </summary>
public static class StockAllocator
{
    /// <summary>Tüm depoların toplam satılabilir stoğu (negatif olabilir; sabitleme <see cref="Allocate(long, decimal, int?, int)"/>'te yapılır).</summary>
    public static long TotalAvailable(IEnumerable<StockLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        return levels.Sum(l => (long)l.Available);
    }

    /// <summary>Depo seviyelerinden kanala gidecek stoğu hesaplar.</summary>
    public static int Allocate(IEnumerable<StockLevel> levels, decimal sharePercent, int? channelCap = null, int lowStockThreshold = 0) =>
        Allocate(TotalAvailable(levels), sharePercent, channelCap, lowStockThreshold);

    /// <summary>
    /// İlan başına kural ile (ADR-042). Kural yoksa <see cref="Allocate(long, decimal, int?, int)"/> ile aynıdır.
    /// UST_LIMIT: MIN(hesaplanan, X). SABIT: MIN(X, satılabilir, kanal üst limiti); kanal payının yerine geçer.
    /// Her iki durumda da düşük stok eşiği önce uygulanır ve sonuç defterdeki satılabilir stoğu asla aşmaz.
    /// </summary>
    public static int Allocate(long totalAvailable, decimal sharePercent, int? channelCap, int lowStockThreshold, ListingStockRule? rule)
    {
        var allocated = Allocate(totalAvailable, sharePercent, channelCap, lowStockThreshold);
        if (rule is null || totalAvailable <= 0 || totalAvailable <= lowStockThreshold)
        {
            return allocated;
        }
        return rule.Type switch
        {
            ListingStockRuleType.UpperLimit => Math.Min(allocated, rule.Value),
            ListingStockRuleType.Fixed => (int)Math.Min(Math.Min(rule.Value, totalAvailable), channelCap ?? int.MaxValue),
            _ => allocated,
        };
    }

    /// <param name="totalAvailable">Tüm depoların toplam satılabilir stoğu.</param>
    /// <param name="sharePercent">Kanal payı (Channel.PayYuzde), 0–100 arası.</param>
    /// <param name="channelCap">Kanal üst limiti; null ise limit yok.</param>
    /// <param name="lowStockThreshold">
    /// Genel düşük stok eşiği (tüm ürün ve kanallar için tek değer). Toplam satılabilir bu değere eşit veya altındaysa 0 döner.
    /// Pay uygulanmadan ÖNCE, ortak havuz değeri üzerinde kontrol edilir.
    /// </param>
    public static int Allocate(long totalAvailable, decimal sharePercent, int? channelCap = null, int lowStockThreshold = 0)
    {
        if (sharePercent is < 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(sharePercent), sharePercent, "Kanal payı 0 ile 100 arasında olmalı.");
        }

        if (channelCap is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCap), channelCap, "Kanal üst limiti negatif olamaz.");
        }

        if (lowStockThreshold < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lowStockThreshold), lowStockThreshold, "Düşük stok eşiği negatif olamaz.");
        }

        if (totalAvailable <= 0 || totalAvailable <= lowStockThreshold)
        {
            return 0;
        }

        // decimal: 33.33 gibi paylarda kayan nokta hatası olmadan FLOOR alınır.
        var share = Math.Floor(totalAvailable * sharePercent / 100m);
        var result = share > int.MaxValue ? int.MaxValue : (int)share;

        return channelCap is { } cap ? Math.Min(result, cap) : result;
    }
}
