using PazaryeriHub.Application.DepotMigration;

namespace PazaryeriHub.Tests.Unit.DepotMigration;

/// <summary>Depo Excel'i → ortak SKU geçiş planı (ADR-059).</summary>
public sealed class DepotMigrationPlannerTests
{
    private static DepotRow Row(int row, string code, int qty = 5, string variant = "YELLOW/SARI") =>
        new(row, "GLORIA BORNOZ", variant, "BORNOZ", "L", qty, code);

    private static DepotState State(
        Dictionary<string, int>? codes = null,
        Dictionary<int, int>? listingSku = null,
        Dictionary<int, (int, int)>? balances = null,
        int[]? pending = null)
    {
        listingSku ??= [];
        var bySku = listingSku.GroupBy(x => x.Value).ToDictionary(g => g.Key, g => (IReadOnlyCollection<int>)g.Select(x => x.Key).ToList());
        return new DepotState(codes ?? [], listingSku, bySku, (pending ?? []).ToHashSet(), balances ?? []);
    }

    [Theory]
    [InlineData("YELLOW/SARI", "SARI")]
    [InlineData("WHITE/BEYAZ (ofre)", "BEYAZ")]
    [InlineData("SMOKE BLUE/ DUMAN MAVİ", "DUMAN MAVİ")]
    [InlineData("CANARD", "CANARD")]
    public void Renk_varyantin_Turkce_kismidir(string variant, string color) =>
        Row(2, "X", variant: variant).Color.Should().Be(color);

    [Fact]
    public void Her_depo_satiri_yeni_SKU_olur_ilanlar_tasinir_tum_ilanlari_giden_eski_SKU_sifirlanir()
    {
        var plan = DepotMigrationPlanner.Plan(
            [Row(2, "GLORIA-SARI-L", 7), Row(3, "GLORIA-SARI-M", 0)],
            [new DepotAssignment(2, ListingId: 10, null), new DepotAssignment(2, ListingId: 11, null), new DepotAssignment(3, null, UnmatchedId: 50)],
            State(listingSku: new() { [10] = 100, [11] = 101, [12] = 101 }, balances: new() { [100] = (9, 0), [101] = (4, 0) }, pending: [50]));

        plan.Errors.Should().BeEmpty();
        plan.NewSkuCount.Should().Be(2);
        plan.NewStockTotal.Should().Be(7);
        plan.Moves.Select(m => (m.ListingId, m.FromSkuId, m.Row.Row)).Should().BeEquivalentTo(new[] { (10, 100, 2), (11, 101, 2) });
        plan.Links.Single().UnmatchedId.Should().Be(50);
        plan.ZeroOldSkus.Should().Equal(new PlannedZero(100, 9));
        // 101'in 12 numaralı ilanı yerinde kalıyor → stoğu korunur
    }

    [Fact]
    public void Kod_zaten_varsa_mevcut_SKU_kullanilir_stok_degismez_ve_oradaki_ilan_tasinmaz()
    {
        var plan = DepotMigrationPlanner.Plan(
            [Row(2, "GLORIA-SARI-L")],
            [new DepotAssignment(2, 10, null), new DepotAssignment(2, 11, null)],
            State(codes: new() { ["GLORIA-SARI-L"] = 500 }, listingSku: new() { [10] = 500, [11] = 101 }, balances: new() { [101] = (3, 0) }));

        plan.Errors.Should().BeEmpty();
        plan.NewSkuCount.Should().Be(0);
        plan.Skus.Single().ExistingSkuId.Should().Be(500);
        plan.Moves.Should().ContainSingle(m => m.ListingId == 11);
        plan.Warnings.Should().ContainSingle(w => w.Contains("zaten var"));
        plan.ZeroOldSkus.Should().Equal(new PlannedZero(101, 3));
    }

    [Fact]
    public void Rezervi_olan_eski_SKU_sifirlanmaz_uyari_verilir()
    {
        var plan = DepotMigrationPlanner.Plan([Row(2, "A")], [new DepotAssignment(2, 10, null)],
            State(listingSku: new() { [10] = 100 }, balances: new() { [100] = (5, 1) }));

        plan.ZeroOldSkus.Should().BeEmpty();
        plan.Warnings.Should().ContainSingle(w => w.Contains("rezervi"));
    }

    [Fact]
    public void Hatalar_tekrarlanan_kod_olmayan_satir_bilinmeyen_ilan_ve_cift_atama()
    {
        var plan = DepotMigrationPlanner.Plan(
            [Row(2, "A"), Row(3, "A"), Row(4, "")],
            [new DepotAssignment(9, 10, null), new DepotAssignment(2, 77, null), new DepotAssignment(2, 10, null), new DepotAssignment(3, 10, null)],
            State(listingSku: new() { [10] = 100 }));

        plan.Errors.Should().Contain(e => e.Contains("'A' birden fazla"))
            .And.Contain(e => e.Contains("satır 4") || e.Contains("Depo satırı 4"))
            .And.Contain(e => e.Contains("depo satırı 9"))
            .And.Contain(e => e.Contains("ilan 77"))
            .And.Contain(e => e.Contains("İlan 10 birden fazla"));
    }

    [Fact]
    public void Negatif_miktar_sifir_sayilir_uyari_verilir()
    {
        var plan = DepotMigrationPlanner.Plan([Row(2, "A", qty: -1), Row(3, "B", qty: 4)], [], State());

        plan.Errors.Should().BeEmpty();
        plan.Skus.Single(s => s.Row.Row == 2).Row.Quantity.Should().Be(0);
        plan.NewStockTotal.Should().Be(4);
        plan.Warnings.Should().ContainSingle(w => w.Contains("miktar -1"));
    }

    [Fact]
    public void Alan_siniri_asilirsa_hata()
    {
        var plan = DepotMigrationPlanner.Plan([new DepotRow(2, "M", "X/Y", "BORNOZ", new string('L', 31), 1, "K")], [], State());
        plan.Errors.Should().ContainSingle(e => e.Contains("alan sınırını"));
    }

    [Fact]
    public void Bekleyen_olmayan_eslesmeyen_kayit_atlanir()
    {
        var plan = DepotMigrationPlanner.Plan([Row(2, "A")], [new DepotAssignment(2, null, 50)], State());

        plan.Links.Should().BeEmpty();
        plan.Warnings.Should().ContainSingle(w => w.Contains("50"));
    }
}
