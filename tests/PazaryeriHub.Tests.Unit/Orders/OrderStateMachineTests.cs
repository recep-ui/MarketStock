using PazaryeriHub.Domain.Orders;
using static PazaryeriHub.Domain.Orders.OrderStatus;

namespace PazaryeriHub.Tests.Unit.Orders;

/// <summary>Aşama 3: sipariş durum makinesi (CLAUDE.md §7).</summary>
public sealed class OrderStateMachineTests
{
    private static readonly (OrderStatus From, OrderStatus To)[] Valid =
    [
        (Yeni, Onaylandi), (Onaylandi, Hazirlaniyor), (Hazirlaniyor, Kargoda), (Kargoda, TeslimEdildi),
        (Yeni, Iptal), (Onaylandi, Iptal), (Hazirlaniyor, Iptal),
        (Kargoda, Iade), (TeslimEdildi, Iade)
    ];

    public static TheoryData<OrderStatus, OrderStatus> ValidTransitions
    {
        get
        {
            var data = new TheoryData<OrderStatus, OrderStatus>();
            foreach (var (from, to) in Valid)
            {
                data.Add(from, to);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ValidTransitions))]
    public void Izinli_gecisler_kabul_edilir(OrderStatus from, OrderStatus to) =>
        OrderStateMachine.CanTransition(from, to).Should().BeTrue();

    [Fact]
    public void Listede_olmayan_tum_gecisler_reddedilir()
    {
        var valid = Valid.ToHashSet();
        foreach (var from in Enum.GetValues<OrderStatus>())
        {
            foreach (var to in Enum.GetValues<OrderStatus>())
            {
                OrderStateMachine.CanTransition(from, to).Should().Be(valid.Contains((from, to)), because: $"{from} → {to}");
            }
        }
    }

    [Theory]
    [InlineData(Kargoda, Iptal)]          // kargodaki sipariş iptal edilemez (iade olur)
    [InlineData(TeslimEdildi, Kargoda)]   // geri dönüş yok
    [InlineData(Iptal, Yeni)]             // son durumdan çıkış yok
    [InlineData(Iade, TeslimEdildi)]
    [InlineData(Yeni, TeslimEdildi)]      // tek adımda atlanamaz
    public void Ornek_gecersiz_gecisler(OrderStatus from, OrderStatus to) =>
        OrderStateMachine.CanTransition(from, to).Should().BeFalse();

    [Fact]
    public void Ileri_atlama_ara_adimlarla_bulunur() =>
        OrderStateMachine.FindPath(Yeni, Kargoda).Should().Equal(Onaylandi, Hazirlaniyor, Kargoda);

    [Fact]
    public void Yeniden_iadeye_en_kisa_yol() =>
        OrderStateMachine.FindPath(Yeni, Iade).Should().Equal(Onaylandi, Hazirlaniyor, Kargoda, Iade);

    [Fact]
    public void Ayni_durum_bos_yol() => OrderStateMachine.FindPath(Kargoda, Kargoda).Should().BeEmpty();

    [Theory]
    [InlineData(Kargoda, Yeni)]
    [InlineData(Iptal, Onaylandi)]
    [InlineData(Kargoda, Iptal)]
    [InlineData(Iade, Kargoda)]
    public void Geri_donus_ve_son_durumdan_cikis_icin_yol_yok(OrderStatus from, OrderStatus to) =>
        OrderStateMachine.FindPath(from, to).Should().BeNull();

    [Theory]
    [InlineData(Iptal, true)]
    [InlineData(Iade, true)]
    [InlineData(TeslimEdildi, false)] // TeslimEdildi → Iade mümkün
    [InlineData(Yeni, false)]
    public void Son_durumlar(OrderStatus status, bool terminal) => OrderStateMachine.IsTerminal(status).Should().Be(terminal);

    [Theory]
    [InlineData(Yeni, StockIntent.Reserved)]
    [InlineData(Hazirlaniyor, StockIntent.Reserved)]
    [InlineData(Kargoda, StockIntent.Shipped)]
    [InlineData(TeslimEdildi, StockIntent.Shipped)]
    [InlineData(Iade, StockIntent.Shipped)]   // iade malı depoya ancak panel onayıyla girer
    [InlineData(Iptal, StockIntent.Released)]
    public void Duruma_gore_stok_hedefi(OrderStatus status, StockIntent intent) =>
        OrderStateMachine.StockIntentFor(status).Should().Be(intent);

    [Fact]
    public void Durum_adlari_veritabani_degerleriyle_ayni() =>
        Enum.GetNames<OrderStatus>().Should().Equal("Yeni", "Onaylandi", "Hazirlaniyor", "Kargoda", "TeslimEdildi", "Iptal", "Iade");
}
