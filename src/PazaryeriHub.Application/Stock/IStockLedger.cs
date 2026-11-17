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

    /// <summary>
    /// Kanal stoğunu deftere alır (ADR-024): Fiziksel = Rezerve + GüvenlikStoğu + MAX(0, hedef − referanstan sonraki net rezervasyon)
    /// (kilit altında, uygulama anı).
    /// SAYIM hareketi (kaynak KANAL_ALIM). Fark yoksa hareket yazılmaz (<see cref="StockChangeResult.MovementId"/> = 0).
    /// <paramref name="expectedPhysical"/> verilir ve mevcut Fiziksel farklıysa <see cref="StockErrorCode.PhysicalChangedSincePreview"/>.
    /// </summary>
    /// <param name="referenceUtc">Hedefin geçerli olduğu an (önizleme, veritabanı saati); sonrasındaki net rezervasyon hedeften düşülür.</param>
    Task<StockChangeResult> TakeChannelStockAsync(StockChangeRequest request, int? expectedPhysical, DateTime? referenceUtc, CancellationToken ct = default);

    /// <summary>
    /// Güvenlik stoğunu ayarlar (GuvenlikStogu = Quantity). Fiziksel stok değişmediği için hareket yazılmaz;
    /// ancak satılabilir stok değiştiği için outbox'a <c>StokDegisti</c> olayı yazılır (kanallar güncellensin).
    /// Bakiye satırı yoksa 0 fiziksel stokla oluşturulur.
    /// </summary>
    Task<WarehouseBalance> SetSafetyStockAsync(StockChangeRequest request, CancellationToken ct = default);

    /// <summary>SKU'nun depo bazında bakiyeleri (bakiyesi olmayan depolar listelenmez).</summary>
    Task<IReadOnlyList<WarehouseBalance>> GetBalancesAsync(int skuId, CancellationToken ct = default);
}

public sealed record WarehouseBalance(int WarehouseId, string WarehouseName, int Physical, int Reserved, int SafetyStock, int Available);

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
