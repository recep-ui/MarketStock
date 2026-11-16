using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using PazaryeriHub.Application.Stock;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.Stock;

/// <summary>
/// <see cref="IStockLedger"/>'ın saklı yordamlarla (Dapper) uygulaması. Transaction yönetimi yordamların içindedir
/// (SET XACT_ABORT ON + tek transaction); burada ayrıca transaction açılmaz.
/// </summary>
public sealed class SqlStockLedger(SqlConnectionFactory connectionFactory) : IStockLedger
{
    public Task<StockChangeResult> ReserveAsync(StockChangeRequest request, CancellationToken ct = default) =>
        ExecuteAsync("dbo.usp_StokRezerve", "@Adet", request, ct);

    public Task<StockChangeResult> ShipAsync(StockChangeRequest request, CancellationToken ct = default) =>
        ExecuteAsync("dbo.usp_StokSevk", "@Adet", request, ct);

    public Task<StockChangeResult> CancelReservationAsync(StockChangeRequest request, CancellationToken ct = default) =>
        ExecuteAsync("dbo.usp_RezerveIptal", "@Adet", request, ct);

    public Task<StockChangeResult> ReceiveAsync(StockChangeRequest request, CancellationToken ct = default) =>
        ExecuteAsync("dbo.usp_StokGiris", "@Adet", request, ct);

    public Task<StockChangeResult> ReceiveReturnAsync(StockChangeRequest request, CancellationToken ct = default) =>
        ExecuteAsync("dbo.usp_IadeGiris", "@Adet", request, ct);

    public Task<StockChangeResult> AdjustToCountAsync(StockChangeRequest request, CancellationToken ct = default) =>
        ExecuteAsync("dbo.usp_SayimDuzelt", "@SayilanAdet", request, ct);

    private async Task<StockChangeResult> ExecuteAsync(string procedure, string quantityParameter, StockChangeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parameters = new DynamicParameters();
        parameters.Add("@SkuId", request.SkuId, DbType.Int32);
        parameters.Add("@WarehouseId", request.WarehouseId, DbType.Int32);
        parameters.Add(quantityParameter, request.Quantity, DbType.Int32);
        parameters.Add("@KaynakTip", request.SourceType, DbType.AnsiString, size: 30);
        parameters.Add("@KaynakId", request.SourceId, DbType.String, size: 100);
        parameters.Add("@Kullanici", request.User, DbType.String, size: 100);

        try
        {
            await using var connection = connectionFactory.Create();
            var row = await connection.QuerySingleAsync<ResultRow>(new CommandDefinition(
                procedure, parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));

            return new StockChangeResult(row.Fiziksel, row.Rezerve, row.Satilabilir, row.HareketId);
        }
        catch (SqlException ex) when (TranslateError(ex) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>Saklı yordamın THROW 500xx hatasını uygulama istisnasına çevirir; diğer SQL hataları olduğu gibi kalır.</summary>
    internal static StockOperationException? TranslateError(SqlException ex) => ex.Number switch
    {
        (int)StockErrorCode.InsufficientStock => new InsufficientStockException(ex.Message, ex),
        (int)StockErrorCode.InsufficientReservationForShipment or
        (int)StockErrorCode.InsufficientReservationForCancel or
        (int)StockErrorCode.CountBelowReserved or
        (int)StockErrorCode.InvalidQuantity => new StockOperationException((StockErrorCode)ex.Number, ex.Message, ex),
        _ => null
    };

    private sealed record ResultRow(int Fiziksel, int Rezerve, int Satilabilir, long HareketId);
}
