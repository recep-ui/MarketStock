/*
    Rezervasyon: Rezerve += @Adet, yalnızca (Fiziksel - Rezerve) >= @Adet ise.
    Tek koşullu UPDATE (UPDLOCK, ROWLOCK): kontrol ve yazım atomik; paralel çağrılarda stok eksiye düşmez.
    0 satır etkilenirse (yetersiz stok veya bakiye satırı yok) THROW 50001.
    Uygulama katmanı 50001'i yakalayıp siparişi "stok yetersiz" bayrağıyla kaydeder (ilke 7).
    Not: GuvenlikStogu rezervasyonu ENGELLEMEZ (spesifikasyon §4); yalnızca kanala gönderilen stoğu düşürür.
    Hata kodları: 50001 stok yetersiz, 50010 geçersiz adet.
*/
CREATE OR ALTER PROCEDURE dbo.usp_StokRezerve
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
       SET Rezerve = sb.Rezerve + @Adet
    OUTPUT inserted.Fiziksel, inserted.Rezerve, inserted.Satilabilir INTO @Sonuc (Fiziksel, Rezerve, Satilabilir)
      FROM dbo.StockBalance AS sb WITH (UPDLOCK, ROWLOCK)
     WHERE sb.SkuId = @SkuId
       AND sb.WarehouseId = @WarehouseId
       AND sb.Fiziksel - sb.Rezerve >= @Adet;

    SET @Etkilenen = @@ROWCOUNT;

    IF @Etkilenen = 0
    BEGIN
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW 50001, N'Stok yetersiz.', 1;
    END

    SELECT @Fiziksel = Fiziksel, @Rezerve = Rezerve, @Satilabilir = Satilabilir FROM @Sonuc;

    EXEC dbo.usp_StokHareketYaz
        @SkuId, @WarehouseId, 'REZERVE', @Adet, @KaynakTip, @KaynakId, @Kullanici,
        @Fiziksel, @Rezerve, @Satilabilir, @HareketId OUTPUT;

    COMMIT TRANSACTION;

    SELECT @Fiziksel AS Fiziksel, @Rezerve AS Rezerve, @Satilabilir AS Satilabilir, @HareketId AS HareketId;
END
