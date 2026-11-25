using System.Globalization;
using System.Text.RegularExpressions;

namespace PazaryeriHub.Application.Panel;

/// <summary>
/// Metindeki ebat ölçüsünü (ör. "70x140", "70 × 140 cm", "200x220x30") karşılaştırılabilir biçime çevirir (ADR-053). Saf fonksiyon.
/// Sayılar küçükten büyüğe sıralanır ("140x70" = "70x140"); ondalık ayırıcı virgül veya nokta olabilir.
/// Metinde birden fazla farklı ebat varsa (ör. set ürünü) hangisinin kastedildiği bilinemeyeceği için null döner.
/// </summary>
public static partial class SizeDimensions
{
    [GeneratedRegex(
        @"(?<![\d.,])(\d+(?:[.,]\d+)?)\s*(?:cm)?\s*[x×*]\s*(\d+(?:[.,]\d+)?)(?:\s*(?:cm)?\s*[x×*]\s*(\d+(?:[.,]\d+)?))?(?![\d.,]*\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>Karşılaştırma biçimi: "70x140" (sıralı, nokta ondalık). Ebat yoksa veya birden fazla farklı ebat varsa null.</summary>
    public static string? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var found = Pattern().Matches(text)
            .Select(m => string.Join('x', m.Groups.Values.Skip(1).Where(g => g.Success)
                .Select(g => decimal.Parse(g.Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture))
                .Order()
                .Select(n => n.ToString("0.##", CultureInfo.InvariantCulture))))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return found.Count == 1 ? found[0] : null;
    }

    /// <summary>Gösterim: "70×140".</summary>
    public static string Display(string canonical) => canonical.Replace('x', '×');
}
