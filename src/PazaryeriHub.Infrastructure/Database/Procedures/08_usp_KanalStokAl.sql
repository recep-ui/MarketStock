/*
    Kanal stoğunu deftere alma (ADR-024). @HedefSatilabilir, @ReferansZaman (önizleme anı, veritabanı saati) itibarıyla
    olması gereken SATILABİLİR stoktur. O andan sonra gelen siparişler (rezervasyonlar) gerçek satılabilir stoğu azaltır:
        D = Σ REZERVE − Σ REZERVE_IPTAL   (bu SKU + depo, Tarih > @ReferansZaman)
        Yeni Fiziksel = Rezerve + GuvenlikStogu + MAX(0, @HedefSatilabilir − D)     (kilit altında, uygulama anındaki değerler)
    Böylece önizleme ile uygulama arasındaki siparişler satılabilir stoktan düşülür (oversell açılmaz) ve Rezerve <= Fiziksel
    her zaman sağlanır. Sevk Fiziksel ve Rezerve'yi birlikte azalttığı için satılabilir değişmez; ancak @BeklenenFiziksel
    kontrolü yüzünden önizlemeden sonraki sevk satırı reddeder (güvenli taraf).
    Hareket: SAYIM (işaretli fark), KaynakTip varsayılan 'KANAL_ALIM'; bakiye + hareket + outbox tek transaction.
    @BeklenenFiziksel verilirse ve mevcut Fiziksel farklıysa (önizlemeden sonra giriş/sayım/sevk olmuş) 50030 ile reddedilir.
    Fark yoksa hareket yazılmaz (HareketId NULL döner); tekrar uygulamak güvenlidir.
    Hata kodları: 50010 geçersiz adet, 50030 önizlemeden sonra fiziksel stok değişti.
*/
CREATE OR ALTER PROCEDURE dbo.usp_KanalStokAl
    @SkuId              INT,
    @WarehouseId        INT,
    @HedefSatilabilir   INT,
    @BeklenenFiziksel   INT           = NULL,
    @ReferansZaman      DATETIME2(3)  = NULL,
    @KaynakTip          VARCHAR(30)   = 'KANAL_ALIM',
    @KaynakId           NVARCHAR(100) = NULL,
    @Kullanici          NVARCHAR(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @HedefSatilabilir IS NULL OR @HedefSatilabilir < 0
        THROW 50010, N'Hedef satılabilir stok negatif olamaz.', 1;

    DECLARE @Sonuc TABLE (Fiziksel INT, Rezerve INT, Satilabilir INT);
    DECLARE @HareketId BIGINT, @Fiziksel INT, @Rezerve INT, @Satilabilir INT, @Mesaj NVARCHAR(400);
    DECLARE @EskiFiziksel INT, @MevcutRezerve INT, @MevcutGuvenlik INT;

    BEGIN TRANSACTION;

    SELECT @EskiFiziksel = sb.Fiziksel, @MevcutRezerve = sb.Rezerve, @MevcutGuvenlik = sb.GuvenlikStogu
      FROM dbo.StockBalance AS sb WITH (UPDLOCK, SERIALIZABLE)
     WHERE sb.SkuId = @SkuId
       AND sb.WarehouseId = @WarehouseId;

    DECLARE @SatirYok BIT = CASE WHEN @EskiFiziksel IS NULL THEN 1 ELSE 0 END;
    IF @SatirYok = 1
        SELECT @EskiFiziksel = 0, @MevcutRezerve = 0, @MevcutGuvenlik = 0;

    IF @BeklenenFiziksel IS NOT NULL AND @BeklenenFiziksel <> @EskiFiziksel
    BEGIN
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        SET @Mesaj = CONCAT(N'Önizlemeden sonra fiziksel stok değişti (önizleme ', @BeklenenFiziksel, N', şimdi ', @EskiFiziksel, N'); yeniden önizleyin.');
        THROW 50030, @Mesaj, 1;
    END

    DECLARE @SonradanRezerve INT = 0;
    IF @ReferansZaman IS NOT NULL
        SELECT @SonradanRezerve = ISNULL(SUM(CASE m.Tip WHEN 'REZERVE' THEN m.Adet ELSE -m.Adet END), 0)
          FROM dbo.StockMovement AS m
         WHERE m.SkuId = @SkuId AND m.WarehouseId = @WarehouseId AND m.Tarih > @ReferansZaman
           AND m.Tip IN ('REZERVE', 'REZERVE_IPTAL');

    DECLARE @HedefSimdi BIGINT = CAST(@HedefSatilabilir AS BIGINT) - @SonradanRezerve;
    IF @HedefSimdi < 0 SET @HedefSimdi = 0;
    DECLARE @Yeni BIGINT = @HedefSimdi + @MevcutRezerve + @MevcutGuvenlik;
    IF @Yeni > 2147483647
    BEGIN
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW 50010, N'Hesaplanan fiziksel stok çok büyük.', 1;
    END

    IF @SatirYok = 0 AND @Yeni = @EskiFiziksel
    BEGIN
        -- Fark yok: hareket yazılmaz
        COMMIT TRANSACTION;
        SELECT sb.Fiziksel, sb.Rezerve, sb.Satilabilir, CAST(NULL AS BIGINT) AS HareketId
          FROM dbo.StockBalance AS sb WHERE sb.SkuId = @SkuId AND sb.WarehouseId = @WarehouseId;
        RETURN;
    END

    IF @SatirYok = 1
        INSERT dbo.StockBalance (SkuId, WarehouseId, Fiziksel, Rezerve, GuvenlikStogu)
        OUTPUT inserted.Fiziksel, inserted.Rezerve, inserted.Satilabilir INTO @Sonuc (Fiziksel, Rezerve, Satilabilir)
        VALUES (@SkuId, @WarehouseId, CAST(@Yeni AS INT), 0, 0);
    ELSE
        UPDATE sb
           SET Fiziksel = CAST(@Yeni AS INT)
        OUTPUT inserted.Fiziksel, inserted.Rezerve, inserted.Satilabilir INTO @Sonuc (Fiziksel, Rezerve, Satilabilir)
          FROM dbo.StockBalance AS sb
         WHERE sb.SkuId = @SkuId
           AND sb.WarehouseId = @WarehouseId;

    SELECT @Fiziksel = Fiziksel, @Rezerve = Rezerve, @Satilabilir = Satilabilir FROM @Sonuc;
    DECLARE @Fark INT = @Fiziksel - @EskiFiziksel;

    EXEC dbo.usp_StokHareketYaz
        @SkuId, @WarehouseId, 'SAYIM', @Fark, @KaynakTip, @KaynakId, @Kullanici,
        @Fiziksel, @Rezerve, @Satilabilir, @HareketId OUTPUT;

    COMMIT TRANSACTION;

    SELECT @Fiziksel AS Fiziksel, @Rezerve AS Rezerve, @Satilabilir AS Satilabilir, @HareketId AS HareketId;
END
