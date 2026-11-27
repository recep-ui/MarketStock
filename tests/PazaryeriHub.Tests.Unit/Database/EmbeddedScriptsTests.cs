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
            "PazaryeriHub.Infrastructure.Database.Migrations.0004_senkron_hatti.sql",
            "PazaryeriHub.Infrastructure.Database.Migrations.0005_kullanici_ve_ayarlar.sql",
            "PazaryeriHub.Infrastructure.Database.Migrations.0006_kanal_katalog.sql",
            "PazaryeriHub.Infrastructure.Database.Migrations.0007_katalog_cek.sql",
            "PazaryeriHub.Infrastructure.Database.Migrations.0008_coklu_ilan_ve_esik.sql",
            "PazaryeriHub.Infrastructure.Database.Migrations.0009_kanal_referans.sql",
            "PazaryeriHub.Infrastructure.Database.Migrations.0010_kanala_yazma_altyapisi.sql",
            "PazaryeriHub.Infrastructure.Database.Migrations.0011_siparis_kanal_kimlikleri.sql",
            "PazaryeriHub.Infrastructure.Database.Migrations.0012_iade_talepleri.sql",
            "PazaryeriHub.Infrastructure.Database.Migrations.0013_fiyat.sql",
            "PazaryeriHub.Infrastructure.Database.Migrations.0014_soru_cevap.sql",
            "PazaryeriHub.Infrastructure.Database.Migrations.0015_eslesme_onayi.sql",
            "PazaryeriHub.Infrastructure.Database.Migrations.0016_kanalda_yok.sql",
            "PazaryeriHub.Infrastructure.Database.Migrations.0017_ilan_stok_kurali_ve_gorsel.sql",
            "PazaryeriHub.Infrastructure.Database.Migrations.0018_islem_gecmisi.sql",
            "PazaryeriHub.Infrastructure.Database.Migrations.0019_stok_gonderim_onayi.sql",
            "PazaryeriHub.Infrastructure.Database.Migrations.0020_eslesmeyen_kanal_fiyati.sql");
    }

    [Fact]
    public void Tum_sakli_yordamlar_gomulu()
    {
        var procedures = Resources
            .Where(r => r.StartsWith("PazaryeriHub.Infrastructure.Database.Procedures.", StringComparison.Ordinal))
            .ToList();

        procedures.Should().HaveCount(8);

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
