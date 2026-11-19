using PazaryeriHub.Application.Panel;

namespace PazaryeriHub.Tests.Unit.Reports;

/// <summary>Genel arama terimi (ADR-034): boşluklar atılır, 2 karakterden kısa terim aranmaz, uzun terim kısaltılır.</summary>
public sealed class SearchTermTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" a ")]
    public void Kisa_veya_bos_terim_aranmaz(string? q) =>
        SearchTerm.Normalize(q).Should().BeNull();

    [Fact]
    public void Bosluklar_atilir() =>
        SearchTerm.Normalize("  CT-01 ").Should().Be("CT-01");

    [Fact]
    public void Uzun_terim_kisaltilir() =>
        SearchTerm.Normalize(new string('x', 250))!.Length.Should().Be(SearchTerm.MaxLength);

    [Fact]
    public void LIKE_ozel_karakterleri_kacislanir() =>
        SearchTerm.EscapeLike(@"50%_[a]\b").Should().Be(@"50\%\_\[a]\\b");
}
