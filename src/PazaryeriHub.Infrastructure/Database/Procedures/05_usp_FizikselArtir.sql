/*
    Fiziksel stok artışı yapan iki yordam: usp_StokGiris (GIRIS) ve usp_IadeGiris (IADE_GIRIS).
    Bakiye satırı yoksa oluşturulur. UPDLOCK + SERIALIZABLE: aynı SKU/depo için eşzamanlı ilk girişlerde
    iki satır eklenmeye çalışılmaz (anahtar aralığı kilitlenir).
    Hata kodları: 50010 geçersiz adet. Geçersiz SkuId/WarehouseId → FK ihlali (547).
*/
CREATE OR ALTER PROCEDURE dbo.usp_FizikselArtir
    @Tip            VARCHAR(20),
    @SkuId          INT,
    @WarehouseId    INT,
    @Adet           INT,
    @KaynakTip      VARCHAR(30),
    @KaynakId       NVARCHAR(100),
    @Kullanici      NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Tip NOT IN ('GIRIS', 'IADE_GIRIS')
        THROW 50011, N'usp_FizikselArtir yalnızca GIRIS veya IADE_GIRIS için kullanılır.', 1;
    IF @Adet IS NULL OR @Adet <= 0
        THROW 50010, N'Adet sıfırdan büyük olmalı.', 1;

    DECLARE @Sonuc TABLE (Fiziksel INT, Rezerve INT, Satilabilir INT);
    DECLARE @HareketId BIGINT, @Fiziksel INT, @Rezerve INT, @Satilabilir INT;

    BEGIN TRANSACTION;

    UPDATE sb
       SET Fiziksel = sb.Fiziksel + @Adet
    OUTPUT inserted.Fiziksel, inserted.Rezerve, inserted.Satilabilir INTO @Sonuc (Fiziksel, Rezerve, Satilabilir)
      FROM dbo.StockBalance AS sb WITH (UPDLOCK, SERIALIZABLE)
     WHERE sb.SkuId = @SkuId
       AND sb.WarehouseId = @WarehouseId;

    IF @@ROWCOUNT = 0
        INSERT dbo.StockBalance (SkuId, WarehouseId, Fiziksel, Rezerve, GuvenlikStogu)
        OUTPUT inserted.Fiziksel, inserted.Rezerve, inserted.Satilabilir INTO @Sonuc (Fiziksel, Rezerve, Satilabilir)
        VALUES (@SkuId, @WarehouseId, @Adet, 0, 0);

    SELECT @Fiziksel = Fiziksel, @Rezerve = Rezerve, @Satilabilir = Satilabilir FROM @Sonuc;

    EXEC dbo.usp_StokHareketYaz
        @SkuId, @WarehouseId, @Tip, @Adet, @KaynakTip, @KaynakId, @Kullanici,
        @Fiziksel, @Rezerve, @Satilabilir, @HareketId OUTPUT;

    COMMIT TRANSACTION;

    SELECT @Fiziksel AS Fiziksel, @Rezerve AS Rezerve, @Satilabilir AS Satilabilir, @HareketId AS HareketId;
END
GO

/* Mal kabul / stok girişi: Fiziksel += @Adet */
CREATE OR ALTER PROCEDURE dbo.usp_StokGiris
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
    EXEC dbo.usp_FizikselArtir 'GIRIS', @SkuId, @WarehouseId, @Adet, @KaynakTip, @KaynakId, @Kullanici;
END
GO

/* İadenin depoya fiziksel girişi (panelden onaylandıktan sonra): Fiziksel += @Adet */
CREATE OR ALTER PROCEDURE dbo.usp_IadeGiris
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
    EXEC dbo.usp_FizikselArtir 'IADE_GIRIS', @SkuId, @WarehouseId, @Adet, @KaynakTip, @KaynakId, @Kullanici;
END
