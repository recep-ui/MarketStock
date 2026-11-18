using PazaryeriHub.Application.Channels;

namespace PazaryeriHub.Application.Auth;

/// <summary>Panel rolleri (CLAUDE.md §8). Değerler AppUser.Rol ile aynıdır.</summary>
public static class Roles
{
    public const string Admin = "Yonetici";
    public const string Warehouse = "Depo";
    public const string Accounting = "Muhasebe";
    public const string Support = "Destek";

    public static readonly string[] All = [Admin, Warehouse, Accounting, Support];
}

/// <summary>
/// Yetki politikaları. Yetki matrisi (docs/decisions.md ADR-014):
/// <list type="table">
/// <item><term>Read</term><description>tüm roller: ekranları görüntüleme</description></item>
/// <item><term>StockWrite</term><description>Yönetici, Depo: stok girişi/sayım, güvenlik stoğu, iade depoya giriş</description></item>
/// <item><term>OrderWrite</term><description>Yönetici, Depo: sipariş durum değişikliği</description></item>
/// <item><term>CatalogWrite</term><description>Yönetici: ürün/SKU/eşleştirme/Excel içe aktarma</description></item>
/// <item><term>Admin</term><description>Yönetici: kanal ayarları, eşikler, kullanıcılar, senkron yeniden dene, mutabakat</description></item>
/// </list>
/// </summary>
public static class Policies
{
    public const string Read = "Okuma";
    public const string StockWrite = "StokYazma";
    public const string OrderWrite = "SiparisYazma";
    public const string CatalogWrite = "KatalogYazma";
    public const string Admin = "Yonetim";

    /// <summary>Müşteri sorusuna cevap yazma/gönderme (ADR-029): Yönetici + Destek.</summary>
    public const string QuestionAnswer = "SoruCevap";
}

public sealed record UserDto(int Id, string UserName, string FullName, string Role, bool Active, DateTime? LastLoginUtc);

public sealed record CreateUserRequest(string UserName, string FullName, string Role, string Password);

public sealed record UpdateUserRequest(string FullName, string Role, bool Active);

/// <summary>Giriş sonucu. Başarısızlık nedeni kullanıcıya genel bir mesajla gösterilir (kullanıcı adı var/yok sızdırılmaz).</summary>
public sealed record LoginResult(UserDto? User, bool LockedOut);

public interface IUserService
{
    Task<LoginResult> ValidateAsync(string userName, string password, CancellationToken ct);
    Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct);
    Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken ct);
    Task<UserDto> UpdateAsync(int id, UpdateUserRequest request, CancellationToken ct);
    Task SetPasswordAsync(int id, string password, CancellationToken ct);
}

/// <summary>
/// Başlangıçta oluşturulacak kullanıcılar ("Kimlik" bölümü). Parolalar REPOYA YAZILMAZ: user-secrets / ortam değişkeni
/// veya testlerde çalışma anında üretilen rastgele değer. Development/Testing'de mevcut kullanıcının parolası/rolü güncellenir;
/// diğer ortamlarda yalnızca hiç kullanıcı yoksa (ilk kurulum) oluşturulur.
/// </summary>
public sealed class IdentityOptions
{
    public const string SectionName = "Kimlik";

    public List<SeedUser> SeedUsers { get; set; } = [];

    public int MaxFailedLogins { get; set; } = 5;

    public int LockoutMinutes { get; set; } = 5;

    public int MinPasswordLength { get; set; } = 10;
}

public sealed class SeedUser
{
    public string UserName { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Role { get; set; } = Roles.Admin;
    public string Password { get; set; } = "";
}

/// <summary>KVKK: Destek rolü için müşteri bilgisini maskeler (API katmanında).</summary>
public static class CustomerMasking
{
    /// <summary>"Ali Veli" → "A** V***"; il görünür, ilçe gizlenir.</summary>
    public static CustomerSummary? Mask(CustomerSummary? customer) => customer is null
        ? null
        : new CustomerSummary(MaskName(customer.FullName), customer.City, customer.District is null ? null : "***");

    public static string? MaskName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return fullName;
        }
        return string.Join(' ', fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part[..1] + new string('*', Math.Max(2, part.Length - 1))));
    }
}
