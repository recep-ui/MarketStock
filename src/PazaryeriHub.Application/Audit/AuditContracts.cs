namespace PazaryeriHub.Application.Audit;

/// <summary>İşlem geçmişi kayıt tipleri (ADR-045).</summary>
public static class AuditTypes
{
    /// <summary>Panelden/API'den yapılan yazma isteği (GET dışı).</summary>
    public const string Request = "ISTEK";

    /// <summary>Başarılı giriş.</summary>
    public const string Login = "GIRIS";

    /// <summary>Başarısız giriş (yanlış parola, kilitli veya pasif hesap). Denenen kullanıcı adı yazılır, parola asla.</summary>
    public const string LoginFailed = "GIRIS_HATALI";

    /// <summary>Çıkış.</summary>
    public const string Logout = "CIKIS";

    public static IReadOnlyList<string> All { get; } = [Request, Login, LoginFailed, Logout];
}

/// <summary>Yazılacak kayıt. İstek gövdesi, sorgu dizesi ve parola YAZILMAZ; yalnızca metot, yol ve sonuç kodu.</summary>
public sealed record AuditRecord(string Type, string? UserName, string? Role, string? Method, string? Path, int? Status,
    string? IpAddress, int? DurationMs);

public sealed record AuditEntry(long Id, DateTime AtUtc, string? UserName, string? Role, string Type, string? Method, string? Path,
    int? Status, string? IpAddress, int? DurationMs);

public sealed record AuditPage(IReadOnlyList<AuditEntry> Items, int Total, int Page, int PageSize);

/// <param name="PathContains">Yolda geçen metin (ör. "users", "stock").</param>
/// <param name="OnlyErrors">Yalnızca 4xx/5xx sonuçlar.</param>
public sealed record AuditQuery(string? UserName = null, string? Type = null, DateTime? FromUtc = null, DateTime? ToUtc = null,
    string? PathContains = null, bool OnlyErrors = false, int Page = 1, int PageSize = 50);

public interface IAuditLog
{
    /// <summary>Kaydı yazar. Hata fırlatmaz: kayıt yazılamazsa asıl işlem bozulmaz, yalnızca loglanır.</summary>
    Task WriteAsync(AuditRecord record, CancellationToken ct);

    Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken ct);
}
