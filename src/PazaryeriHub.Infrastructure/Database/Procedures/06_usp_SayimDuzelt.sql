/*
    Sayım düzeltmesi: Fiziksel stoğu sayılan miktara eşitler. Hareket Adet'i işaretli farktır
    (yeni - eski; 0 olabilir, sayım yine de deftere yazılır).
    Sayılan miktar mevcut rezervden az olamaz (CHECK Rezerve <= Fiziksel); bu durumda önce rezervler
    (sevk/iptal) çözülmelidir.
    Hata kodları: 50004 sayılan < rezerve, 50010 geçersiz adet.
*/
CREATE OR ALTER PROCEDURE dbo.usp_SayimDuzelt
    @SkuId          INT,
    @WarehouseId    INT,
    @SayilanAdet    INT,
    @KaynakTip      VARCHAR(30)   = NULL,
    @KaynakId       NVARCHAR(100) = NULL,
    @Kullanici      NVARCHAR(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @SayilanAdet IS NULL OR @SayilanAdet < 0
        THROW 50010, N'Sayılan adet negatif olamaz.', 1;

    DECLARE @Sonuc TABLE (Fiziksel INT, Rezerve INT, Satilabilir INT);
    DECLARE @HareketId BIGINT, @Fiziksel INT, @Rezerve INT, @Satilabilir INT;
    DECLARE @EskiFiziksel INT, @MevcutRezerve INT, @Mesaj NVARCHAR(400);

    BEGIN TRANSACTION;

    SELECT @EskiFiziksel = sb.Fiziksel, @MevcutRezerve = sb.Rezerve
      FROM dbo.StockBalance AS sb WITH (UPDLOCK, SERIALIZABLE)
     WHERE sb.SkuId = @SkuId
       AND sb.WarehouseId = @WarehouseId;

    IF @EskiFiziksel IS NULL
    BEGIN
        SET @EskiFiziksel = 0;
        INSERT dbo.StockBalance (SkuId, WarehouseId, Fiziksel, Rezerve, GuvenlikStogu)
        OUTPUT inserted.Fiziksel, inserted.Rezerve, inserted.Satilabilir INTO @Sonuc (Fiziksel, Rezerve, Satilabilir)
        VALUES (@SkuId, @WarehouseId, @SayilanAdet, 0, 0);
    END
    ELSE
    BEGIN
        IF @SayilanAdet < @MevcutRezerve
        BEGIN
            IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
            SET @Mesaj = CONCAT(N'Sayılan adet (', @SayilanAdet, N') mevcut rezerveden (', @MevcutRezerve, N') az olamaz.');
            THROW 50004, @Mesaj, 1;
        END

        UPDATE sb
           SET Fiziksel = @SayilanAdet
        OUTPUT inserted.Fiziksel, inserted.Rezerve, inserted.Satilabilir INTO @Sonuc (Fiziksel, Rezerve, Satilabilir)
          FROM dbo.StockBalance AS sb
         WHERE sb.SkuId = @SkuId
           AND sb.WarehouseId = @WarehouseId;
    END

    SELECT @Fiziksel = Fiziksel, @Rezerve = Rezerve, @Satilabilir = Satilabilir FROM @Sonuc;

    DECLARE @Fark INT = @SayilanAdet - @EskiFiziksel;

    EXEC dbo.usp_StokHareketYaz
        @SkuId, @WarehouseId, 'SAYIM', @Fark, @KaynakTip, @KaynakId, @Kullanici,
        @Fiziksel, @Rezerve, @Satilabilir, @HareketId OUTPUT;

    COMMIT TRANSACTION;

    SELECT @Fiziksel AS Fiziksel, @Rezerve AS Rezerve, @Satilabilir AS Satilabilir, @HareketId AS HareketId;
END
