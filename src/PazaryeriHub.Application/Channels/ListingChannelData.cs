using System.Text.Json;

namespace PazaryeriHub.Application.Channels;

/// <summary>
/// ChannelListing.KanalVerisi JSON'undan panelde gösterilen kanal bilgileri (ADR-031): ürün linki, kanalda görünen fiyat, varyant.
/// Yalnızca kanalın kendi API'sinden gelen değerler; link https değilse gösterilmez.
/// Ad, marka, stok kodu, renk ve beden kanalın bildirdiğidir; yanlış eşleştirme kontrolünde kullanılır (ADR-035).
/// </summary>
public sealed record ListingChannelData(
    string? ProductUrl, decimal? SalePrice, decimal? ListPrice, string? Variant,
    string? Name = null, string? Brand = null, string? StockCode = null, string? Color = null, string? Size = null,
    string? ImageUrl = null)
{
    /// <summary>Kanal adresleri (ürün linki, görsel) yalnızca mutlak https ve en fazla 1000 karakterse kabul edilir.</summary>
    public static string? HttpsOrNull(string? url) =>
        url is not null && url.Length <= 1000 && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? url : null;

    public static ListingChannelData Empty { get; } = new(null, null, null, null);

    public static ListingChannelData Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object)
            {
                return Empty;
            }
            var url = HttpsOrNull(Str(r, "urunLinki"));
            return new ListingChannelData(url, Dec(r, "kanalSatisFiyati"), Dec(r, "kanalListeFiyati"), Str(r, "varyant"),
                Str(r, "urunAdi"), Str(r, "marka"), Str(r, "stokKodu"), Str(r, "renk"), Str(r, "beden"),
                HttpsOrNull(Str(r, "gorsel")));
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static decimal? Dec(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;
}
