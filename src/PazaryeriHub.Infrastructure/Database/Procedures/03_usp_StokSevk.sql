/*
    Sevk: rezerve edilmiş malın depodan çıkışı. Fiziksel -= @Adet, Rezerve -= @Adet.
    Yalnızca Rezerve >= @Adet ise (CHECK Rezerve <= Fiziksel olduğundan Fiziksel de yeterlidir).
    Hata kodları: 50002 sevk için rezerve yetersiz, 50010 geçersiz adet.
*/
CREATE OR ALTER PROCEDURE dbo.usp_StokSevk
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

    IF @Adet IS NULL OR @Adet <= 0
        THROW 50010, N'Adet sıfırdan büyük olmalı.', 1;

    DECLARE @Sonuc TABLE (Fiziksel INT, Rezerve INT, Satilabilir INT);
    DECLARE @Etkilenen INT, @HareketId BIGINT, @Fiziksel INT, @Rezerve INT, @Satilabilir INT;

    BEGIN TRANSACTION;

    UPDATE sb
       SET Fiziksel = sb.Fiziksel - @Adet,
           Rezerve  = sb.Rezerve  - @Adet
    OUTPUT inserted.Fiziksel, inserted.Rezerve, inserted.Satilabilir INTO @Sonuc (Fiziksel, Rezerve, Satilabilir)
      FROM dbo.StockBalance AS sb WITH (UPDLOCK, ROWLOCK)
     WHERE sb.SkuId = @SkuId
       AND sb.WarehouseId = @WarehouseId
       AND sb.Rezerve >= @Adet;

    SET @Etkilenen = @@ROWCOUNT;

    IF @Etkilenen = 0
    BEGIN
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW 50002, N'Sevk için yeterli rezerve yok.', 1;
    END

    SELECT @Fiziksel = Fiziksel, @Rezerve = Rezerve, @Satilabilir = Satilabilir FROM @Sonuc;

    EXEC dbo.usp_StokHareketYaz
        @SkuId, @WarehouseId, 'SEVK', @Adet, @KaynakTip, @KaynakId, @Kullanici,
        @Fiziksel, @Rezerve, @Satilabilir, @HareketId OUTPUT;

    COMMIT TRANSACTION;

    SELECT @Fiziksel AS Fiziksel, @Rezerve AS Rezerve, @Satilabilir AS Satilabilir, @HareketId AS HareketId;
END
