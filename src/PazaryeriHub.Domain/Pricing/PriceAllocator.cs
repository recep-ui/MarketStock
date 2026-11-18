namespace PazaryeriHub.Domain.Pricing;

/// <summary>Kanal fiyatı yuvarlama biçimi.</summary>
public enum PriceRounding
{
    /// <summary>Kuruşa (2 hane) yuvarlanır.</summary>
    None,

    /// <summary>En yakın tam liraya yuvarlanır (0,50 yukarı).</summary>
    Whole,

    /// <summary>Lira kısmı korunur, kuruş ,99 olur (123,40 → 123,99). Sonuç hiçbir zaman hesaplanandan düşük olmaz.</summary>
    Ends99,

    /// <summary>Kuruş ,90 olur; hesaplanan ,90'ı geçiyorsa bir sonraki liranın ,90'ı (123,95 → 124,90).</summary>
    Ends90
}

/// <summary>
/// Kanal fiyat kuralı (ADR-028): <c>fiyat = SKU fiyatı × Carpan + SabitEk</c>, sonra yuvarlama, sonra taban.
/// </summary>
/// <param name="Multiplier">Çarpan (0 &lt; x ≤ 10). 1 = SKU fiyatı aynen.</param>
/// <param name="FixedAdd">Sabit ek (TL; negatif olabilir).</param>
/// <param name="Floor">Taban fiyat (TL). Hesaplanan satış fiyatı bunun altına İNMEZ; null → taban yok.</param>
public sealed record PriceRule(decimal Multiplier = 1m, decimal FixedAdd = 0m, PriceRounding Rounding = PriceRounding.None, decimal? Floor = null)
{
    public static PriceRule Default { get; } = new();

    public const decimal MaxMultiplier = 10m;
    public const decimal MaxAmount = 10_000_000m;

    /// <summary>Kural geçerli mi; değilse Türkçe gerekçe listesi.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Multiplier is <= 0m or > MaxMultiplier)
        {
            errors.Add($"Çarpan 0'dan büyük ve en fazla {MaxMultiplier} olmalı.");
        }
        if (Math.Abs(FixedAdd) > MaxAmount)
        {
            errors.Add("Sabit ek çok büyük.");
        }
        if (Floor is < 0m || Floor > MaxAmount)
        {
            errors.Add("Taban fiyat 0 veya pozitif olmalı.");
        }
        if (!Enum.IsDefined(Rounding))
        {
            errors.Add("Geçersiz yuvarlama.");
        }
        return errors;
    }
}

/// <summary>Kanala gidecek fiyat; <see cref="Problem"/> doluysa fiyat gönderilemez.</summary>
/// <param name="FloorApplied">Hesaplanan satış fiyatı tabanın altındaydı ve tabana çekildi.</param>
/// <param name="FromOverride">İlan için elle girilmiş fiyat kullanıldı (kural uygulanmadı).</param>
public sealed record PriceTarget(decimal SalePrice, decimal ListPrice, bool FloorApplied, bool FromOverride, string? Problem)
{
    public bool Sendable => Problem is null;

    public static PriceTarget Unpriceable(string problem) => new(0m, 0m, false, false, problem);
}

/// <summary>
/// Kanala gidecek fiyatı hesaplayan saf fonksiyon (ADR-028; <see cref="Stock.StockAllocator"/> gibi yan etkisiz).
/// <code>
/// satış  = ilan fiyatı (elle) ?? yuvarla(SKU satış × çarpan + ek)
/// liste  = yuvarla(SKU liste × çarpan + ek)  (SKU liste yoksa = satış)
/// satış  = MAX(satış, taban)                 (taban ASLA aşağı delinmez)
/// liste  = MAX(liste, satış)                 (kanallar liste &lt; satış kabul etmez)
/// </code>
/// </summary>
public static class PriceAllocator
{
    public static PriceTarget Compute(decimal? skuListPrice, decimal? skuSalePrice, decimal? listingOverride, PriceRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var ruleErrors = rule.Validate();
        if (ruleErrors.Count > 0)
        {
            return PriceTarget.Unpriceable("Kanal fiyat kuralı geçersiz: " + string.Join(" ", ruleErrors));
        }

        decimal sale;
        var fromOverride = listingOverride is not null;
        if (listingOverride is { } manual)
        {
            if (manual <= 0m)
            {
                return PriceTarget.Unpriceable("İlan fiyatı 0'dan büyük olmalı.");
            }
            sale = Math.Round(manual, 2, MidpointRounding.AwayFromZero);
        }
        else if (skuSalePrice is { } baseSale)
        {
            if (baseSale <= 0m)
            {
                return PriceTarget.Unpriceable("SKU satış fiyatı 0'dan büyük olmalı.");
            }
            sale = Apply(baseSale, rule);
        }
        else
        {
            return PriceTarget.Unpriceable("SKU satış fiyatı tanımlı değil.");
        }

        var list = skuListPrice is { } baseList && baseList > 0m ? Apply(baseList, rule) : sale;

        var floorApplied = false;
        if (rule.Floor is { } floor && sale < floor)
        {
            sale = floor;
            floorApplied = true;
        }

        if (sale <= 0m)
        {
            return PriceTarget.Unpriceable("Hesaplanan fiyat 0 veya negatif (kuralı kontrol edin).");
        }

        return new PriceTarget(sale, Math.Max(list, sale), floorApplied, fromOverride, null);
    }

    /// <summary>Kural (çarpan, ek, yuvarlama) — taban hariç.</summary>
    public static decimal Apply(decimal price, PriceRule rule) => Round(price * rule.Multiplier + rule.FixedAdd, rule.Rounding);

    public static decimal Round(decimal value, PriceRounding rounding)
    {
        var cents = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        return rounding switch
        {
            PriceRounding.Whole => Math.Round(cents, 0, MidpointRounding.AwayFromZero),
            PriceRounding.Ends99 => Math.Floor(cents) + 0.99m,
            PriceRounding.Ends90 => Math.Floor(cents) + 0.90m >= cents ? Math.Floor(cents) + 0.90m : Math.Floor(cents) + 1.90m,
            _ => cents
        };
    }

    /// <summary>
    /// Son gönderilen fiyata göre değişim yüzdesi (mutlak). Son gönderilen yoksa (ilk gönderim) null.
    /// </summary>
    public static decimal? ChangePercent(decimal? lastSent, decimal newPrice) =>
        lastSent is { } last && last > 0m ? Math.Round(Math.Abs(newPrice - last) / last * 100m, 2, MidpointRounding.AwayFromZero) : null;

    /// <summary>
    /// Değişim eşiği aşıyor mu (ADR-028): aşıyorsa otomatik gönderilmez, Yönetici onayı bekler.
    /// İlk gönderim (son gönderilen yok) eşiğe takılmaz; yeteneği açmadan önceki önizlemede gösterilir.
    /// </summary>
    public static bool ExceedsThreshold(decimal? lastSent, decimal newPrice, decimal thresholdPercent)
    {
        if (thresholdPercent < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(thresholdPercent), thresholdPercent, "Eşik negatif olamaz.");
        }
        return ChangePercent(lastSent, newPrice) is { } change && change > thresholdPercent;
    }
}
