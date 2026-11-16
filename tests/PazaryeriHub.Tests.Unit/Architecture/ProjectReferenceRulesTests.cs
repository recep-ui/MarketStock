using System.Xml.Linq;

namespace PazaryeriHub.Tests.Unit.Architecture;

/// <summary>
/// Hub-and-spoke ve katman bağımlılık kurallarını .csproj proje referansları üzerinden doğrular.
/// Domain ← Application ← (Infrastructure | Adapters.*) ← Api
/// </summary>
public sealed class ProjectReferenceRulesTests
{
    private static readonly Lazy<Dictionary<string, string[]>> References = new(LoadReferences);

    private static Dictionary<string, string[]> LoadReferences()
    {
        var root = FindRepositoryRoot();
        return Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .ToDictionary(
                p => Path.GetFileNameWithoutExtension(p),
                p => XDocument.Load(p).Descendants("ProjectReference")
                    .Select(r => Path.GetFileNameWithoutExtension(r.Attribute("Include")!.Value.Replace('\\', Path.DirectorySeparatorChar)))
                    .ToArray());
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PazaryeriHub.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("PazaryeriHub.slnx bulunamadı.");
    }

    private static string[] RefsOf(string project) => References.Value[project];

    [Fact]
    public void Beklenen_projeler_mevcut()
    {
        References.Value.Keys.Should().Contain(
        [
            "PazaryeriHub.Api", "PazaryeriHub.Domain", "PazaryeriHub.Application", "PazaryeriHub.Infrastructure",
            "PazaryeriHub.Adapters.Trendyol", "PazaryeriHub.Adapters.Hepsiburada",
            "PazaryeriHub.Adapters.Beymen", "PazaryeriHub.Adapters.Softcotton"
        ]);
    }

    [Fact]
    public void Domain_hicbir_projeye_bagimli_degil() =>
        RefsOf("PazaryeriHub.Domain").Should().BeEmpty();

    [Fact]
    public void Application_yalnizca_Domaine_bagimli() =>
        RefsOf("PazaryeriHub.Application").Should().BeEquivalentTo(["PazaryeriHub.Domain"]);

    [Fact]
    public void Cekirdek_katmanlar_adaptorleri_ve_Apiyi_tanimaz()
    {
        foreach (var core in new[] { "PazaryeriHub.Domain", "PazaryeriHub.Application", "PazaryeriHub.Infrastructure" })
        {
            RefsOf(core).Should().NotContain(r => r.StartsWith("PazaryeriHub.Adapters.", StringComparison.Ordinal) || r == "PazaryeriHub.Api",
                because: $"{core} kanal adaptörlerini tanımamalı (hub-and-spoke)");
        }
    }

    [Fact]
    public void Adaptorler_yalnizca_Application_ve_Domaine_bagimli()
    {
        var adapters = References.Value.Keys.Where(k => k.StartsWith("PazaryeriHub.Adapters.", StringComparison.Ordinal)).ToList();
        adapters.Should().NotBeEmpty();

        foreach (var adapter in adapters)
        {
            RefsOf(adapter).Should().OnlyContain(r => r == "PazaryeriHub.Application" || r == "PazaryeriHub.Domain",
                because: $"{adapter} altyapıya, Api'ye veya başka adaptöre bağımlı olmamalı");
        }
    }
}
