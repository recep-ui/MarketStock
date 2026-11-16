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
/// </code>
/// </summary>
public static class StockAllocator
{
    /// <summary>Tüm depoların toplam satılabilir stoğu (negatif olabilir; sabitleme <see cref="Allocate(long, decimal, int?)"/>'te yapılır).</summary>
    public static long TotalAvailable(IEnumerable<StockLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        return levels.Sum(l => (long)l.Available);
    }

    /// <summary>Depo seviyelerinden kanala gidecek stoğu hesaplar.</summary>
    public static int Allocate(IEnumerable<StockLevel> levels, decimal sharePercent, int? channelCap = null) =>
        Allocate(TotalAvailable(levels), sharePercent, channelCap);

    /// <param name="totalAvailable">Tüm depoların toplam satılabilir stoğu.</param>
    /// <param name="sharePercent">Kanal payı (Channel.PayYuzde), 0–100 arası.</param>
    /// <param name="channelCap">Kanal üst limiti; null ise limit yok.</param>
    public static int Allocate(long totalAvailable, decimal sharePercent, int? channelCap = null)
    {
        if (sharePercent is < 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(sharePercent), sharePercent, "Kanal payı 0 ile 100 arasında olmalı.");
        }

        if (channelCap is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCap), channelCap, "Kanal üst limiti negatif olamaz.");
        }

        if (totalAvailable <= 0)
        {
            return 0;
        }

        // decimal: 33.33 gibi paylarda kayan nokta hatası olmadan FLOOR alınır.
        var share = Math.Floor(totalAvailable * sharePercent / 100m);
        var result = share > int.MaxValue ? int.MaxValue : (int)share;

        return channelCap is { } cap ? Math.Min(result, cap) : result;
    }
}
