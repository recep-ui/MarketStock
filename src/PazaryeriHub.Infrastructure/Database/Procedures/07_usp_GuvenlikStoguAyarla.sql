/*
    Güvenlik stoğu ayarı: GuvenlikStogu = @Adet.
    Fiziksel/rezerve değişmez, bu yüzden StockMovement yazılmaz (hareket tipleri yalnızca fiziksel/rezerve içindir).
    Satılabilir değiştiği için aynı transaction'da outbox'a StokDegisti yazılır; kanallara yeni stok gider.
    Bakiye satırı yoksa 0 fiziksel stokla oluşturulur.
    Hata kodları: 50010 geçersiz adet. Geçersiz SkuId/WarehouseId → FK ihlali (547).
*/
CREATE OR ALTER PROCEDURE dbo.usp_GuvenlikStoguAyarla
    @SkuId          INT,
    @WarehouseId    INT,
    @Adet           INT,
    @KaynakTip      VARCHAR(30)   = NULL,
    @KaynakId       NVARCHAR(100) = NULL,
    @Kullanici      NVARCHAR(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Adet IS NULL OR @Adet < 0
        THROW 50010, N'Güvenlik stoğu negatif olamaz.', 1;

    DECLARE @Sonuc TABLE (Fiziksel INT, Rezerve INT, GuvenlikStogu INT, Satilabilir INT);

    BEGIN TRANSACTION;

    UPDATE sb
       SET GuvenlikStogu = @Adet
    OUTPUT inserted.Fiziksel, inserted.Rezerve, inserted.GuvenlikStogu, inserted.Satilabilir
      INTO @Sonuc (Fiziksel, Rezerve, GuvenlikStogu, Satilabilir)
      FROM dbo.StockBalance AS sb WITH (UPDLOCK, SERIALIZABLE)
     WHERE sb.SkuId = @SkuId
       AND sb.WarehouseId = @WarehouseId;

    IF @@ROWCOUNT = 0
        INSERT dbo.StockBalance (SkuId, WarehouseId, Fiziksel, Rezerve, GuvenlikStogu)
        OUTPUT inserted.Fiziksel, inserted.Rezerve, inserted.GuvenlikStogu, inserted.Satilabilir
          INTO @Sonuc (Fiziksel, Rezerve, GuvenlikStogu, Satilabilir)
        VALUES (@SkuId, @WarehouseId, 0, 0, @Adet);

    INSERT dbo.Outbox (OlayTipi, Payload)
    SELECT 'StokDegisti',
           (SELECT CAST(NULL AS BIGINT) AS HareketId,
                   @SkuId          AS SkuId,
                   @WarehouseId    AS WarehouseId,
                   'GUVENLIK_STOGU' AS Tip,
                   @Adet           AS Adet,
                   s.Fiziksel      AS Fiziksel,
                   s.Rezerve       AS Rezerve,
                   s.Satilabilir   AS Satilabilir,
                   @KaynakTip      AS KaynakTip,
                   @Kullanici      AS Kullanici
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)
      FROM @Sonuc AS s;

    COMMIT TRANSACTION;

    SELECT Fiziksel, Rezerve, GuvenlikStogu, Satilabilir FROM @Sonuc;
END
