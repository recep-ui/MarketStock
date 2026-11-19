using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Panel;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.Panel;

/// <summary>
/// "Doğru eşleşme" onayı (ADR-036). İmza sunucuda, ilanın bugünkü bilgisinden hesaplanır (istemciye güvenilmez).
/// Eşleştirmeyi (ChannelListing.SkuId) değiştirmez; onay kaydı silinmez, kapatılır.
/// </summary>
public sealed class ListingMatchApprovalService(
    SqlConnectionFactory connectionFactory, PanelQueryService panel, TimeProvider clock, ILogger<ListingMatchApprovalService> logger)
    : IListingMatchApprovalService
{
    private const int UniqueViolation = 2601;

    public async Task<ChannelStockListRow> ApproveAsync(int listingId, string? user, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var channelId = await ChannelOfAsync(connection, listingId, ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // Açık onay kilitlenir: aynı ilan için eşzamanlı iki onay tek kayıt bırakır
        var open = await connection.QuerySingleOrDefaultAsync<(long Id, string Imza)?>(new CommandDefinition("""
            SELECT Id, Imza FROM dbo.ListingMatchApproval WITH (UPDLOCK, HOLDLOCK)
             WHERE ListingId = @listingId AND GeriAlmaTarihi IS NULL
            """, new { listingId }, tx, cancellationToken: ct));
        var row = (await panel.ReadChannelRowsAsync(connection, channelId, listingId, ct, tx)).Single();
        if (row.Warnings.Count == 0)
        {
            throw new ConflictException("Bu eşleşmede uyarı yok; onay gerekmiyor.");
        }

        var signature = ListingMatchCheck.Signature(row.SkuId, row.Channel, row.Ledger, row.Warnings);
        if (open is { } current && current.Imza == signature)
        {
            await tx.CommitAsync(ct);
            return row; // aynı bilgi zaten onaylı
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var who = Truncate(user, 100);
        if (open is { } outdated)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE dbo.ListingMatchApproval SET GeriAlan = @who, GeriAlmaTarihi = @now, KapanisNedeni = 'YENILENDI' WHERE Id = @id
                """, new { who, now, id = outdated.Id }, tx, cancellationToken: ct));
        }
        try
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT dbo.ListingMatchApproval (ListingId, SkuId, Imza, Uyarilar, Onaylayan, OnayTarihi)
                VALUES (@listingId, @skuId, @signature, @warnings, @who, @now)
                """, new
                {
                    listingId, skuId = row.SkuId, signature, now, who,
                    warnings = string.Join(',', row.Warnings.Select(w => w.Code).Distinct().Order(StringComparer.Ordinal))
                }, tx, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number == UniqueViolation)
        {
            throw new ConflictException("Bu ilan aynı anda başka biri tarafından onaylandı; listeyi yenileyin.");
        }
        await tx.CommitAsync(ct);
        logger.LogInformation("Eşleşme onaylandı: ilan {ListingId}, SKU {SkuId}, uyarılar {Uyarilar}, kullanıcı {Kullanici}",
            listingId, row.SkuId, string.Join(',', row.Warnings.Select(w => w.Code)), who);

        return (await panel.ReadChannelRowsAsync(connection, channelId, listingId, ct)).Single();
    }

    public async Task<ChannelStockListRow> RevokeAsync(int listingId, string? user, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var channelId = await ChannelOfAsync(connection, listingId, ct);
        var who = Truncate(user, 100);
        var closed = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.ListingMatchApproval SET GeriAlan = @who, GeriAlmaTarihi = @now, KapanisNedeni = 'GERI_ALINDI'
             WHERE ListingId = @listingId AND GeriAlmaTarihi IS NULL
            """, new { listingId, who, now = clock.GetUtcNow().UtcDateTime }, cancellationToken: ct));
        if (closed == 0)
        {
            throw new NotFoundException("Bu ilanda açık bir eşleşme onayı yok.");
        }
        logger.LogInformation("Eşleşme onayı geri alındı: ilan {ListingId}, kullanıcı {Kullanici}", listingId, who);
        return (await panel.ReadChannelRowsAsync(connection, channelId, listingId, ct)).Single();
    }

    private static async Task<int> ChannelOfAsync(SqlConnection connection, int listingId, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT ChannelId FROM dbo.ChannelListing WHERE Id = @listingId", new { listingId }, cancellationToken: ct))
        ?? throw new NotFoundException($"Eşleştirme bulunamadı (Id={listingId}).");

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        return connection;
    }
}
