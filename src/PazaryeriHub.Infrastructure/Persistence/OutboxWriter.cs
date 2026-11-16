using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace PazaryeriHub.Infrastructure.Persistence;

/// <summary>
/// EF Core işlemleriyle aynı transaction'da outbox olayı yazar (ilke 3: değişiklik + olay birlikte).
/// Çağıran, <c>db.Database.BeginTransactionAsync</c> ile transaction açmış olmalıdır.
/// </summary>
internal static class OutboxWriter
{
    public static Task AddAsync(PazaryeriHubDbContext db, string eventType, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        return db.Database.ExecuteSqlInterpolatedAsync($"INSERT dbo.Outbox (OlayTipi, Payload) VALUES ({eventType}, {json})", ct);
    }
}
