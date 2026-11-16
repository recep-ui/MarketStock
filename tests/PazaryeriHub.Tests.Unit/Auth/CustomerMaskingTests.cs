using PazaryeriHub.Application.Auth;
using PazaryeriHub.Application.Channels;

namespace PazaryeriHub.Tests.Unit.Auth;

/// <summary>KVKK: Destek rolü için müşteri maskeleme.</summary>
public sealed class CustomerMaskingTests
{
    [Theory]
    [InlineData("Ali Veli", "A** V***")]
    [InlineData("Ayşe Nur Yılmaz", "A*** N** Y*****")]
    [InlineData("Öz", "Ö**")]
    [InlineData("", "")]
    public void Ad_soyad_maskelenir(string input, string expected) => CustomerMasking.MaskName(input).Should().Be(expected);

    [Fact]
    public void Il_gorunur_ilce_gizlenir() =>
        CustomerMasking.Mask(new CustomerSummary("Ali Veli", "İstanbul", "Kadıköy"))
            .Should().Be(new CustomerSummary("A** V***", "İstanbul", "***"));

    [Fact]
    public void Bos_musteri_null_kalir() => CustomerMasking.Mask(null).Should().BeNull();
}
