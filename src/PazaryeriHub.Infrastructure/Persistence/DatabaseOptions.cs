namespace PazaryeriHub.Infrastructure.Persistence;

/// <summary>
/// Veritabanı ayarları ("Database" bölümü). Bağlantı dizesi ayrıca "ConnectionStrings:PazaryeriHub" altından okunur;
/// gizli bilgi içerdiği için repoya değil user-secrets / ortam değişkenine yazılır.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    public const string ConnectionStringName = "PazaryeriHub";

    /// <summary>
    /// true ise uygulama başlarken veritabanı yoksa oluşturulur ve RCSI / snapshot ayarları açılır.
    /// Yalnızca geliştirme ortamı içindir; CREATE/ALTER DATABASE yetkisi gerektirir.
    /// </summary>
    public bool AutoCreate { get; set; }

    /// <summary>Başlangıçtaki veritabanı hazırlığı için üst süre sınırı (saniye).</summary>
    public int BootstrapTimeoutSeconds { get; set; } = 30;
}
