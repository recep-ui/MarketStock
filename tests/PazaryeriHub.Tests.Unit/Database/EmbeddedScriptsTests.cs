using PazaryeriHub.Infrastructure.Database;

namespace PazaryeriHub.Tests.Unit.Database;

/// <summary>Migration ve yordam script'lerinin DbUp'ın aradığı kaynak adlarıyla derlemeye gömüldüğünü doğrular.</summary>
public sealed class EmbeddedScriptsTests
{
    private static readonly string[] Resources = typeof(DatabaseMigrator).Assembly.GetManifestResourceNames();

    [Fact]
    public void Migration_scriptleri_gomulu_ve_sirali()
    {
        var migrations = Resources
            .Where(r => r.StartsWith("PazaryeriHub.Infrastructure.Database.Migrations.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        migrations.Should().Equal(
            "PazaryeriHub.Infrastructure.Database.Migrations.0001_temel_sema.sql",
            "PazaryeriHub.Infrastructure.Database.Migrations.0002_baslangic_verisi.sql",
            "PazaryeriHub.Infrastructure.Database.Migrations.0003_siparis_cekirdegi.sql",
            "PazaryeriHub.Infrastructure.Database.Migrations.0004_senkron_hatti.sql");
    }

    [Fact]
    public void Tum_sakli_yordamlar_gomulu()
    {
        var procedures = Resources
            .Where(r => r.StartsWith("PazaryeriHub.Infrastructure.Database.Procedures.", StringComparison.Ordinal))
            .ToList();

        procedures.Should().HaveCount(7);

        var allText = string.Concat(procedures.Select(ReadResource));
        foreach (var proc in new[] { "usp_StokRezerve", "usp_StokSevk", "usp_RezerveIptal", "usp_IadeGiris", "usp_StokGiris", "usp_SayimDuzelt" })
        {
            allText.Should().Contain($"CREATE OR ALTER PROCEDURE dbo.{proc}", because: $"{proc} spesifikasyonda zorunlu");
        }
    }

    [Fact]
    public void Her_yordam_xact_abort_ve_transaction_kullanir()
    {
        foreach (var name in Resources.Where(r => r.Contains(".Procedures.", StringComparison.Ordinal)))
        {
            var text = ReadResource(name);
            text.Should().Contain("SET XACT_ABORT ON", because: name);
        }
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(DatabaseMigrator).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
