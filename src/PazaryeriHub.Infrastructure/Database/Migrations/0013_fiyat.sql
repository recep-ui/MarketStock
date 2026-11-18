/*
    0013: Fiyat (ADR-028, M4). ADR-015'in yalnızca fiyat kısmı iptal edilir; tek depo kararı geçerli.
      - Sku.ListeFiyati / Sku.SatisFiyati: SKU'nun temel fiyatları (KDV dahil, TL). NULL = fiyat tanımlı değil → kanala fiyat gitmez.
      - ChannelListing.FiyatOverride: ilan için elle girilen satış fiyatı (kural uygulanmaz; taban yine uygulanır).
      - ChannelListing.SonGonderilenSatisFiyati / SonGonderilenListeFiyati / SonFiyatSenkron: kanalın onayladığı son fiyat.
      - SyncQueue.GonderilenSatisFiyati / GonderilenListeFiyati: Tip='FIYAT' kaydıyla gönderilen değer (stoktaki GonderilenDeger karşılığı).
      - Kanal fiyat kuralı Channel.Ayarlar.fiyatKurali JSON'unda (çarpan, sabit ek, yuvarlama, taban): şema değişikliği yok.
      - PriceApproval: değişim eşiğini (AppSetting FiyatSapmaEsigiYuzde, varsayılan 30) aşan veya yetenek DENEME iken oluşan fiyat
        Yönetici onayı bekler. İlan başına tek bekleyen kayıt (filtreli UNIQUE). Karar kullanıcı adı ve zamanla yazılır.
      - PriceHistory: yalnızca eklenen fiyat geçmişi (SKU fiyatı, ilan fiyatı, kanal kuralı, kanala gönderim).
*/

ALTER TABLE dbo.Sku ADD
    ListeFiyati DECIMAL(18,2) NULL,
    SatisFiyati DECIMAL(18,2) NULL;
GO

ALTER TABLE dbo.Sku ADD
    CONSTRAINT CK_Sku_ListeFiyati CHECK (ListeFiyati IS NULL OR ListeFiyati > 0),
    CONSTRAINT CK_Sku_SatisFiyati CHECK (SatisFiyati IS NULL OR SatisFiyati > 0),
    CONSTRAINT CK_Sku_Liste_Satis CHECK (ListeFiyati IS NULL OR SatisFiyati IS NULL OR ListeFiyati >= SatisFiyati);

ALTER TABLE dbo.ChannelListing ADD
    FiyatOverride             DECIMAL(18,2) NULL,
    SonGonderilenSatisFiyati  DECIMAL(18,2) NULL,
    SonGonderilenListeFiyati  DECIMAL(18,2) NULL,
    SonFiyatSenkron           DATETIME2(3)  NULL;
GO

ALTER TABLE dbo.ChannelListing ADD
    CONSTRAINT CK_ChannelListing_FiyatOverride CHECK (FiyatOverride IS NULL OR FiyatOverride > 0);

ALTER TABLE dbo.SyncQueue ADD
    GonderilenSatisFiyati DECIMAL(18,2) NULL,
    GonderilenListeFiyati DECIMAL(18,2) NULL;
GO

CREATE TABLE dbo.PriceApproval
(
    Id                  BIGINT IDENTITY(1,1) NOT NULL,
    ListingId           INT            NOT NULL,
    EskiSatisFiyati     DECIMAL(18,2)  NULL,
    YeniSatisFiyati     DECIMAL(18,2)  NOT NULL,
    YeniListeFiyati     DECIMAL(18,2)  NOT NULL,
    DegisimYuzde        DECIMAL(9,2)   NULL,
    Neden               VARCHAR(10)    COLLATE Latin1_General_100_BIN2 NOT NULL,
    Durum               VARCHAR(12)    COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT DF_PriceApproval_Durum DEFAULT ('BEKLIYOR'),
    Olusma              DATETIME2(3)   NOT NULL CONSTRAINT DF_PriceApproval_Olusma DEFAULT (SYSUTCDATETIME()),
    Guncelleme          DATETIME2(3)   NOT NULL CONSTRAINT DF_PriceApproval_Guncelleme DEFAULT (SYSUTCDATETIME()),
    KararVeren          NVARCHAR(100)  COLLATE Turkish_CI_AS NULL,
    KararTarihi         DATETIME2(3)   NULL,
    CONSTRAINT PK_PriceApproval PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_PriceApproval_Listing FOREIGN KEY (ListingId) REFERENCES dbo.ChannelListing (Id) ON DELETE CASCADE,
    CONSTRAINT CK_PriceApproval_Neden CHECK (Neden IN ('ESIK', 'DENEME')),
    CONSTRAINT CK_PriceApproval_Durum CHECK (Durum IN ('BEKLIYOR', 'ONAYLANDI', 'REDDEDILDI', 'GECERSIZ')),
    CONSTRAINT CK_PriceApproval_Fiyat CHECK (YeniSatisFiyati > 0 AND YeniListeFiyati >= YeniSatisFiyati)
);
-- İlan başına tek bekleyen onay (yeni hesaplanan fiyat bekleyen kaydı günceller)
CREATE UNIQUE INDEX UX_PriceApproval_Bekleyen ON dbo.PriceApproval (ListingId) WHERE Durum = 'BEKLIYOR';
CREATE INDEX IX_PriceApproval_Listing ON dbo.PriceApproval (ListingId, Durum, KararTarihi) INCLUDE (YeniSatisFiyati);

CREATE TABLE dbo.PriceHistory
(
    Id          BIGINT IDENTITY(1,1) NOT NULL,
    Tip         VARCHAR(20)    COLLATE Latin1_General_100_BIN2 NOT NULL,
    SkuId       INT            NULL,
    ListingId   INT            NULL,
    ChannelId   INT            NULL,
    EskiSatis   DECIMAL(18,2)  NULL,
    YeniSatis   DECIMAL(18,2)  NULL,
    EskiListe   DECIMAL(18,2)  NULL,
    YeniListe   DECIMAL(18,2)  NULL,
    Detay       NVARCHAR(MAX)  COLLATE Turkish_CI_AS NULL,
    Kaynak      VARCHAR(20)    COLLATE Latin1_General_100_BIN2 NOT NULL,
    Kullanici   NVARCHAR(100)  COLLATE Turkish_CI_AS NULL,
    Tarih       DATETIME2(3)   NOT NULL CONSTRAINT DF_PriceHistory_Tarih DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_PriceHistory PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT CK_PriceHistory_Tip CHECK (Tip IN ('SKU_FIYAT', 'ILAN_FIYATI', 'KANAL_KURALI', 'KANALA_GONDERILDI', 'ONAY')),
    CONSTRAINT CK_PriceHistory_Detay CHECK (Detay IS NULL OR ISJSON(Detay) = 1)
);
CREATE INDEX IX_PriceHistory_Sku ON dbo.PriceHistory (SkuId, Tarih DESC) WHERE SkuId IS NOT NULL;
CREATE INDEX IX_PriceHistory_Listing ON dbo.PriceHistory (ListingId, Tarih DESC) WHERE ListingId IS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM dbo.AppSetting WHERE Anahtar = 'FiyatSapmaEsigiYuzde')
    INSERT dbo.AppSetting (Anahtar, Deger) VALUES ('FiyatSapmaEsigiYuzde', N'30');
