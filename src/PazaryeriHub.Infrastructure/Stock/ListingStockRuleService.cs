using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Stock;
using PazaryeriHub.Application.Sync;
using PazaryeriHub.Domain.Stock;
using PazaryeriHub.Infrastructure.Persistence;
using PazaryeriHub.Infrastructure.Sync;

namespace PazaryeriHub.Infrastructure.Stock;

/// <summary>İlan stok kuralı (ADR-042): ChannelListing.StokKural* kolonları + aynı transaction'da outbox olayı.</summary>
public sealed class ListingStockRuleService(
    SqlConnectionFactory connectionFactory, SyncStore syncStore, TimeProvider clock, ILogger<ListingStockRuleService> logger) : IListingStockRuleService
{
    /// <summary>Panelde girilebilecek en büyük adet (yanlış yazım koruması).</summary>
    public const int MaxValue = 100_000;

    public async Task<ListingStockRuleDto> GetAsync(int listingId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return await ReadAsync(connection, listingId, ct);
    }

    public async Task<ListingStockRuleDto> SetAsync(int listingId, ListingStockRuleInput input, string? user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var type = string.IsNullOrWhiteSpace(input.Type) ? null : input.Type.Trim().ToUpperInvariant();
        var v = new Validator();
        if (type is not null)
        {
            v.Must(ListingStockRule.ParseType(type) is not null, "type", "Kural UST_LIMIT veya SABIT olmalı.");
            v.Must(input.Value is >= 0 and <= MaxValue, "value", $"Adet 0 ile {MaxValue} arasında olmalı.");
        }
        v.ThrowIfInvalid();
        var value = type is null ? null : input.Value;

        await using var connection = await OpenAsync(ct);
        await using (var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            var row = await connection.QuerySingleOrDefaultAsync<(string? Tip, int? Deger)?>(new CommandDefinition(
                "SELECT StokKuralTip, StokKuralDeger FROM dbo.ChannelListing WITH (UPDLOCK) WHERE Id = @listingId", new { listingId }, tx, cancellationToken: ct))
                ?? throw new NotFoundException($"Eşleştirme bulunamadı (Id={listingId}).");
            if (row.Tip != type || row.Deger != value)
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE dbo.ChannelListing
                       SET StokKuralTip = @type, StokKuralDeger = @value, StokKuralDegistiren = @user, StokKuralTarih = @now
                     WHERE Id = @listingId;
                    INSERT dbo.Outbox (OlayTipi, Payload) VALUES (@eventType, CONCAT(N'{"ListingId":', @listingId, N'}'));
                    """, new { listingId, type, value, user, now = clock.GetUtcNow().UtcDateTime, eventType = OutboxEvents.ListingChanged },
                    tx, cancellationToken: ct));
                logger.LogInformation("İlan stok kuralı değişti: ilan {IlanId}, {EskiTip}/{EskiDeger} → {YeniTip}/{YeniDeger}, kullanıcı {Kullanici}",
                    listingId, row.Tip, row.Deger, type, value, user);
            }
            await tx.CommitAsync(ct);
        }
        return await ReadAsync(connection, listingId, ct);
    }

    private async Task<ListingStockRuleDto> ReadAsync(SqlConnection connection, int listingId, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<(string? Tip, int? Deger, string? Degistiren, DateTime? Tarih)?>(new CommandDefinition(
            "SELECT StokKuralTip, StokKuralDeger, StokKuralDegistiren, StokKuralTarih FROM dbo.ChannelListing WHERE Id = @listingId",
            new { listingId }, cancellationToken: ct))
            ?? throw new NotFoundException($"Eşleştirme bulunamadı (Id={listingId}).");
        var target = (await syncStore.ComputeTargetsAsync(connection, null, [listingId], ct)).Single();
        return new ListingStockRuleDto(listingId, row.Tip, row.Deger, target.Target, target.LastSent, row.Degistiren,
            row.Tarih is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        return connection;
    }
}
