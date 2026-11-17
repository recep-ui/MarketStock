using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PazaryeriHub.Application.Common;

namespace PazaryeriHub.Infrastructure.Persistence;

/// <summary>SQL Server kısıt ihlallerini kullanıcıya gösterilebilir <see cref="ConflictException"/>'a çevirir.</summary>
internal static class DbErrors
{
    public const int UniqueIndex = 2601;
    public const int UniqueConstraint = 2627;
    public const int ForeignKeyOrCheck = 547;

    public static SqlException? Sql(Exception ex) => ex switch
    {
        SqlException sql => sql,
        DbUpdateException { InnerException: SqlException sql } => sql,
        _ => null
    };

    public static bool IsUnique(Exception ex) => Sql(ex)?.Number is UniqueIndex or UniqueConstraint;

    public static bool IsReferenceViolation(Exception ex) =>
        Sql(ex) is { Number: ForeignKeyOrCheck } sql && sql.Message.Contains("REFERENCE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Benzersizlik ihlalinde hangi alanın çakıştığını indeks/kısıt adından bulur.</summary>
    public static ConflictException UniqueConflict(Exception ex)
    {
        var message = Sql(ex)?.Message ?? "";
        var field =
            message.Contains("UQ_Sku_SkuKodu", StringComparison.Ordinal) ? "Bu SKU kodu zaten kayıtlı." :
            message.Contains("UX_Sku_Barkod", StringComparison.Ordinal) ? "Bu barkod başka bir SKU'da kayıtlı." :
            message.Contains("UQ_Warehouse_Ad", StringComparison.Ordinal) ? "Bu depo adı zaten kayıtlı." :
            message.Contains("UX_ChannelListing_Channel_KanalBarkod", StringComparison.Ordinal) ? "Bu kanal barkodu bu kanalda başka bir ilana bağlı." :
            message.Contains("UX_ChannelListing_Channel_KanalUrunKodu", StringComparison.Ordinal) ? "Bu kanal ürün kodu bu kanalda başka bir ilana bağlı." :
            "Kayıt benzersizlik kuralını ihlal ediyor.";
        return new ConflictException(field, ex);
    }
}
