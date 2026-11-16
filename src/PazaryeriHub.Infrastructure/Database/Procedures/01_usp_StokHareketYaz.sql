/*
    İç yardımcı yordam: hareket (StockMovement) + outbox (StokDegisti) kaydını yazar.
    Yalnızca stok yordamlarının AÇIK transaction'ı içinden çağrılır; böylece bakiye, hareket ve outbox
    aynı transaction'da yazılır (CLAUDE.md ilke 3).
    Bu klasördeki script'ler her açılışta yeniden çalışır (CREATE OR ALTER).
*/
CREATE OR ALTER PROCEDURE dbo.usp_StokHareketYaz
    @SkuId          INT,
    @WarehouseId    INT,
    @Tip            VARCHAR(20),
    @Adet           INT,
    @KaynakTip      VARCHAR(30),
    @KaynakId       NVARCHAR(100),
    @Kullanici      NVARCHAR(100),
    @Fiziksel       INT,
    @Rezerve        INT,
    @Satilabilir    INT,
    @HareketId      BIGINT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @@TRANCOUNT = 0
        THROW 50020, N'usp_StokHareketYaz yalnızca açık bir transaction içinde çağrılabilir.', 1;

    INSERT dbo.StockMovement (SkuId, WarehouseId, Tip, Adet, KaynakTip, KaynakId, Kullanici)
    VALUES (@SkuId, @WarehouseId, @Tip, @Adet, @KaynakTip, @KaynakId, @Kullanici);

    SET @HareketId = CAST(SCOPE_IDENTITY() AS BIGINT);

    INSERT dbo.Outbox (OlayTipi, Payload)
    SELECT 'StokDegisti',
           (SELECT @HareketId   AS HareketId,
                   @SkuId       AS SkuId,
                   @WarehouseId AS WarehouseId,
                   @Tip         AS Tip,
                   @Adet        AS Adet,
                   @Fiziksel    AS Fiziksel,
                   @Rezerve     AS Rezerve,
                   @Satilabilir AS Satilabilir
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
END
