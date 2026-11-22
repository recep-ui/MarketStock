using Microsoft.Extensions.Hosting.WindowsServices;

namespace PazaryeriHub.Api.Hosting;

/// <summary>
/// Kurulu çalışma (ADR-045): uygulama bir bilgisayara Windows servisi olarak kurulduğunda ayarlar, şifreleme anahtarları ve loglar
/// <c>%ProgramData%\PazaryeriHub</c> altındadır; program dosyaları ayrı klasördedir (güncellemede veri kaybolmaz).
/// Geliştirme ve deneme modunda bu dosya OKUNMAZ (geliştirici makinesine kurulmuş bir kopya varsa deneme modu ona bağlanmasın).
/// </summary>
public static class InstalledHosting
{
    public const string ServiceName = "PazaryeriHub";

    /// <summary>Tek seferlik kurulum adımı: veritabanını oluştur, şemayı güncelle, ilk yöneticiyi ekle, çık (kur.ps1 çağırır).</summary>
    public const string SetupArg = "--kurulum";

    /// <summary>Kurulu kopyayı servis yerine konsolda çalıştırmak için (sorun giderme).</summary>
    public const string ConsoleArg = "--kurulu";

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PazaryeriHub");

    /// <summary>Ayar dosyası. <c>PAZARYERIHUB_AYARLAR</c> ortam değişkeni başka bir dosya gösterebilir (paketi kurmadan denemek için).</summary>
    public static string SettingsFile =>
        Environment.GetEnvironmentVariable("PAZARYERIHUB_AYARLAR") is { Length: > 0 } custom ? custom : Path.Combine(DataDirectory, "ayarlar.json");

    public static bool IsSetup(string[] args) => args.Contains(SetupArg, StringComparer.OrdinalIgnoreCase);

    /// <summary>Servis olarak, kurulum adımında veya --kurulu ile çalışıyor.</summary>
    public static bool IsInstalled(string[] args) =>
        WindowsServiceHelpers.IsWindowsService() || IsSetup(args) || args.Contains(ConsoleArg, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Kurulu çalışmada: çalışma klasörü programın klasörü olur (servisler System32'de başlar; göreli yollar oraya gitmesin)
    /// ve <see cref="SettingsFile"/> yapılandırmaya eklenir (ortam değişkenleri yine önceliklidir).
    /// </summary>
    public static void Configure(WebApplicationBuilder builder, string[] args)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Host.UseWindowsService(o => o.ServiceName = ServiceName);
        if (!IsInstalled(args))
        {
            return;
        }

        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        builder.Configuration.AddJsonFile(SettingsFile, optional: false, reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables();
        if (IsSetup(args))
        {
            // Kurulum adımı: veritabanı yoksa oluşturulur, migration'lar uygulanır (kur.ps1 yönetici hesabıyla çalıştırır).
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:AutoCreate"] = "true",
                ["Database:MigrateOnStartup"] = "true"
            });
        }
    }
}
