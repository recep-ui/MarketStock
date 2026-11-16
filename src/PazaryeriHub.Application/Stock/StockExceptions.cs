namespace PazaryeriHub.Application.Stock;

/// <summary>Saklı yordamların fırlattığı iş kuralı hataları (THROW 500xx).</summary>
public enum StockErrorCode
{
    /// <summary>50001: Rezervasyon için serbest stok yetersiz.</summary>
    InsufficientStock = 50001,

    /// <summary>50002: Sevk için yeterli rezerve yok.</summary>
    InsufficientReservationForShipment = 50002,

    /// <summary>50003: İptal edilecek kadar rezerve yok.</summary>
    InsufficientReservationForCancel = 50003,

    /// <summary>50004: Sayılan miktar mevcut rezervden az.</summary>
    CountBelowReserved = 50004,

    /// <summary>50010: Geçersiz miktar.</summary>
    InvalidQuantity = 50010
}

/// <summary>Stok iş kuralı ihlali. Bakiye, hareket ve outbox değişmeden kalır (transaction geri alınır).</summary>
public class StockOperationException(StockErrorCode code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public StockErrorCode Code { get; } = code;
}

/// <summary>
/// Rezervasyon için stok yetersiz (50001). Sipariş akışında bu hata siparişi reddetmez;
/// sipariş "stok yetersiz" bayrağıyla kaydedilir (ilke 7).
/// </summary>
public sealed class InsufficientStockException(string message, Exception? innerException = null)
    : StockOperationException(StockErrorCode.InsufficientStock, message, innerException);
