using PazaryeriHub.Application.Stock;

namespace PazaryeriHub.Tests.Unit.Stock;

/// <summary>Kanal stoğunu deftere alma kuralları (ADR-024): hedef değer, çekimden sonraki rezervasyon ve işaretler.</summary>
public sealed class ChannelIntakeRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    private static IntakeEvaluation Eval(int?[] source, int available = 0, int reservedSince = 0, decimal share = 100m, int? cap = null,
        int threshold = 0, DateTime? snapshot = null, DateTime? lastMovement = null, bool pending = false, int?[]? all = null) =>
        ChannelIntakeRules.Evaluate(source, all ?? source, available, reservedSince, share, cap, threshold,
            snapshot ?? Now.AddMinutes(-5), lastMovement, pending, Now);

    [Fact]
    public void Temiz_satir_kaynak_degerini_onerir_ve_secili_gelir()
    {
        var e = Eval([7], available: 4);
        e.Should().Match<IntakeEvaluation>(x => x.Value == 7 && x.Target == 7 && x.DefaultSelected && x.Flags.Count == 0);
    }

    [Fact]
    public void Cekimden_sonraki_rezervasyon_hedeften_dusulur_bilgi_isareti_secimi_engellemez()
    {
        var e = Eval([7], available: 1, reservedSince: 3);
        e.Target.Should().Be(4);
        e.Flags.Should().Equal(IntakeFlags.ReservationsAfterSnapshot);
        e.DefaultSelected.Should().BeTrue();
        Eval([2], reservedSince: 5).Target.Should().Be(0, "negatif hedef 0'a sabitlenir");
    }

    [Fact]
    public void Ayni_kanalda_farkli_ilan_degerlerinde_en_dusugu_alinir_ve_isaretlenir()
    {
        var e = Eval([9, 4], available: 0, all: [9, 4, 2]);
        e.Value.Should().Be(4);
        e.MinValue.Should().Be(2);
        e.Flags.Should().Contain(IntakeFlags.ListingsDiffer);
        e.DefaultSelected.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 3, null, 100, IntakeFlags.ThresholdAmbiguous)]   // kanal 0, eşik 3 → gerçek 0..3
    [InlineData(5, 0, 5, 100, IntakeFlags.AtCap)]                   // üst limitte → gerçek daha büyük olabilir
    [InlineData(5, 0, null, 50, IntakeFlags.ShareNot100)]
    public void Tersine_cevrilemeyen_durumlar_isaretlenir_ve_secilmez(int value, int threshold, int? cap, int share, string flag)
    {
        var e = Eval([value], available: 99, share: share, cap: cap, threshold: threshold);
        e.Flags.Should().Contain(flag);
        e.DefaultSelected.Should().BeFalse();
    }

    [Fact]
    public void Eski_cekim_defter_degisimi_bekleyen_senkron_ve_fark_yok_isaretlenir()
    {
        Eval([5], snapshot: Now.AddHours(-25)).Flags.Should().Contain(IntakeFlags.StaleSnapshot);
        Eval([5], lastMovement: Now.AddMinutes(-1), snapshot: Now.AddMinutes(-5)).Flags.Should().Contain(IntakeFlags.LedgerChangedAfter);
        Eval([5], pending: true).Flags.Should().Contain(IntakeFlags.SyncPending);
        Eval([5], available: 5).Flags.Should().Equal(IntakeFlags.NoDifference);
    }

    [Fact]
    public void Kanalda_deger_yoksa_oneri_yok()
    {
        var e = Eval([null, null]);
        e.Value.Should().BeNull();
        e.Target.Should().BeNull();
        e.Flags.Should().Equal(IntakeFlags.NoChannelValue);
        e.DefaultSelected.Should().BeFalse();
    }

    [Fact]
    public void Onerilen_fiziksel_hedef_arti_rezerve_arti_guvenlik() =>
        ChannelIntakeRules.ProposedPhysical(4, 2, 1).Should().Be(7);
}
