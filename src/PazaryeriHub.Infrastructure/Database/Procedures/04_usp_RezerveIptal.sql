/*
    Rezerve iptali (ör. sipariş iptali): Rezerve -= @Adet, yalnızca Rezerve >= @Adet ise.
    Hata kodları: 50003 iptal için rezerve yetersiz, 50010 geçersiz adet.
*/
CREATE OR ALTER PROCEDURE dbo.usp_RezerveIptal
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
       SET Rezerve = sb.Rezerve - @Adet
    OUTPUT inserted.Fiziksel, inserted.Rezerve, inserted.Satilabilir INTO @Sonuc (Fiziksel, Rezerve, Satilabilir)
      FROM dbo.StockBalance AS sb WITH (UPDLOCK, ROWLOCK)
     WHERE sb.SkuId = @SkuId
       AND sb.WarehouseId = @WarehouseId
       AND sb.Rezerve >= @Adet;

    SET @Etkilenen = @@ROWCOUNT;

    IF @Etkilenen = 0
    BEGIN
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW 50003, N'İptal edilecek kadar rezerve yok.', 1;
    END

    SELECT @Fiziksel = Fiziksel, @Rezerve = Rezerve, @Satilabilir = Satilabilir FROM @Sonuc;

    EXEC dbo.usp_StokHareketYaz
        @SkuId, @WarehouseId, 'REZERVE_IPTAL', @Adet, @KaynakTip, @KaynakId, @Kullanici,
        @Fiziksel, @Rezerve, @Satilabilir, @HareketId OUTPUT;

    COMMIT TRANSACTION;

    SELECT @Fiziksel AS Fiziksel, @Rezerve AS Rezerve, @Satilabilir AS Satilabilir, @HareketId AS HareketId;
END
