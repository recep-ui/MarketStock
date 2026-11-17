using PazaryeriHub.Application.ChannelCatalog;
using PazaryeriHub.Application.Channels;

namespace PazaryeriHub.Tests.Unit.ChannelCatalog;

/// <summary>Kanal API katalog öğelerinin eşleştirici girdisine çevrilmesi (Excel okuyucusuyla aynı kurallar).</summary>
public sealed class ChannelCatalogEntriesTests
{
    private static ChannelCatalogEntry E(string? code, string? barcode, string? model = null, bool passive = false) =>
        new(code, barcode, null, model, null, null, "Ad", null, null, null, passive);

    [Fact]
    public void Kirpilir_sira_numarasi_verilir_pasif_korunur()
    {
        var (items, errors) = ChannelCatalogEntries.ToItems([E(" HB1 ", " B1 ", " M1 ", passive: true), E(null, "B2")]);

        errors.Should().BeEmpty();
        items.Select(i => (i.Row, i.ChannelProductCode, i.Barcode, i.ModelCode, i.Passive))
            .Should().Equal((1, "HB1", "B1", "M1", true), (2, null, "B2", null, false));
    }

    [Fact]
    public void Kodsuz_uzun_kodlu_ve_tekrarlanan_ogeler_hatalidir()
    {
        var (items, errors) = ChannelCatalogEntries.ToItems([
            E(null, null),                       // 1: kod yok
            E(null, new string('9', 51)),        // 2: barkod > 50
            E("HB1", "B1"),                      // 3
            E("HB1", "B2"),                      // 4: kanal kodu tekrar (3 ile)
            E("HB3", "B3")                       // 5
        ]);

        errors.Select(e => e.Row).Distinct().Should().Equal(1, 2, 3, 4);
        items.Select(i => i.Row).Should().Equal(5);
    }
}
