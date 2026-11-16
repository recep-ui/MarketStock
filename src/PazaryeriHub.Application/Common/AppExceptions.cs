namespace PazaryeriHub.Application.Common;

/// <summary>İstenen kayıt yok (HTTP 404).</summary>
public sealed class NotFoundException(string message) : Exception(message);

/// <summary>İşlem mevcut durumla çelişiyor: benzersizlik ihlali, bağlı kayıt nedeniyle silinemez vb. (HTTP 409).</summary>
public sealed class ConflictException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>Girdi doğrulama hatası (HTTP 400). Alan adı → hata mesajları.</summary>
public sealed class RequestValidationException(IReadOnlyDictionary<string, string[]> errors)
    : Exception("Girdi doğrulanamadı.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

/// <summary>Basit doğrulama toplayıcı: hataları biriktirir, sonunda tek seferde fırlatır.</summary>
public sealed class Validator
{
    private readonly Dictionary<string, List<string>> _errors = [];

    public Validator Required(string field, string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(field, "Zorunlu alan.");
        }
        else
        {
            MaxLength(field, value, maxLength);
        }
        return this;
    }

    public Validator MaxLength(string field, string? value, int maxLength)
    {
        if (value is not null && value.Trim().Length > maxLength)
        {
            Add(field, $"En fazla {maxLength} karakter olabilir.");
        }
        return this;
    }

    public Validator Must(bool condition, string field, string message)
    {
        if (!condition)
        {
            Add(field, message);
        }
        return this;
    }

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list))
        {
            _errors[field] = list = [];
        }
        list.Add(message);
    }

    public void ThrowIfInvalid()
    {
        if (_errors.Count > 0)
        {
            throw new RequestValidationException(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
        }
    }
}

/// <summary>Metin normalizasyonu: baştaki/sondaki boşluklar atılır, boş metin null olur.</summary>
public static class Text
{
    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public static class Paging
{
    public const int MaxPageSize = 500;

    public static (int Page, int PageSize) Normalize(int? page, int? pageSize) =>
        (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 50, 1, MaxPageSize));
}
