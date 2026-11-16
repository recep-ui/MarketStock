namespace PazaryeriHub.Application.Stock;

/// <summary>
/// Stok defteri: tüm stok değişiklikleri buradan geçer. Her çağrı tek bir veritabanı transaction'ında
/// bakiyeyi günceller, <c>StockMovement</c> hareketi ve <c>Outbox</c> (StokDegisti) olayı yazar.
/// Stok asla doğrudan üzerine yazılmaz (sayım düzeltmesi bile fark hareketi olarak kaydedilir).
/// </summary>
public interface IStockLedger
{
    /// <summary>Rezerve eder. Serbest stok (Fiziksel - Rezerve) yetmezse <see cref="InsufficientStockException"/>.</summary>
    Task<StockChangeResult> ReserveAsync(StockChangeRequest request, CancellationToken ct = default);

    /// <summary>Rezerve edilmiş malı sevk eder (Fiziksel↓, Rezerve↓).</summary>
    Task<StockChangeResult> ShipAsync(StockChangeRequest request, CancellationToken ct = default);

    /// <summary>Rezervi iptal eder (Rezerve↓).</summary>
    Task<StockChangeResult> CancelReservationAsync(StockChangeRequest request, CancellationToken ct = default);

    /// <summary>Mal kabul / stok girişi (Fiziksel↑).</summary>
    Task<StockChangeResult> ReceiveAsync(StockChangeRequest request, CancellationToken ct = default);

    /// <summary>İadenin depoya fiziksel girişi (Fiziksel↑).</summary>
    Task<StockChangeResult> ReceiveReturnAsync(StockChangeRequest request, CancellationToken ct = default);

    /// <summary>Sayım düzeltmesi: Fiziksel = <see cref="StockChangeRequest.Quantity"/> (sayılan miktar).</summary>
    Task<StockChangeResult> AdjustToCountAsync(StockChangeRequest request, CancellationToken ct = default);
}

/// <param name="SkuId">SKU.</param>
/// <param name="WarehouseId">Depo.</param>
/// <param name="Quantity">Miktar (sayımda: sayılan miktar).</param>
/// <param name="SourceType">Kaynak türü (ör. SIPARIS, IADE, EXCEL, PANEL).</param>
/// <param name="SourceId">Kaynak kimliği (ör. kanal sipariş no).</param>
/// <param name="User">İşlemi yapan kullanıcı.</param>
public sealed record StockChangeRequest(
    int SkuId,
    int WarehouseId,
    int Quantity,
    string? SourceType = null,
    string? SourceId = null,
    string? User = null);

/// <summary>İşlem sonrası bakiye (ilgili SKU + depo) ve yazılan hareketin kimliği.</summary>
public sealed record StockChangeResult(int Physical, int Reserved, int Available, long MovementId);
