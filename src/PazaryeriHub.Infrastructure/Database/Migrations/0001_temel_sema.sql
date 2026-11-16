/*
    0001: Temel şema (CLAUDE.md §4).
    Kurallar:
      - Tarihler DATETIME2(3), UTC (SYSUTCDATETIME()). Para DECIMAL(18,2).
      - Esnek veri NVARCHAR(MAX) + CHECK (ISJSON(...) = 1).
      - İnsan metinleri (ad, marka, renk...) Turkish_CI_AS.
      - SKU, barkod, kanal ürün kodu, kanal sipariş no ve sabit kod/durum kolonları Latin1_General_100_BIN2
        (Türkçe i/İ/ı/I karşılaştırma sorunlarını önlemek için; büyük/küçük harf duyarlı, ikili karşılaştırma).
    Bu dosya bir kez çalışır (DbUp journal: dbo.SchemaVersions). Değişiklikler YENİ migration dosyasıyla yapılır.
*/

-------------------------------------------------------------------------------
-- Katalog
-------------------------------------------------------------------------------
CREATE TABLE dbo.Product
(
    Id              INT             IDENTITY(1,1) NOT NULL,
    Ad              NVARCHAR(300)   COLLATE Turkish_CI_AS NOT NULL,
    Marka           NVARCHAR(100)   COLLATE Turkish_CI_AS NULL,
    Kategori        NVARCHAR(200)   COLLATE Turkish_CI_AS NULL,
    OlusturmaTarihi DATETIME2(3)    NOT NULL CONSTRAINT DF_Product_OlusturmaTarihi DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_Product PRIMARY KEY CLUSTERED (Id)
);

CREATE TABLE dbo.Sku
(
    Id          INT             IDENTITY(1,1) NOT NULL,
    ProductId   INT             NOT NULL,
    SkuKodu     NVARCHAR(100)   COLLATE Latin1_General_100_BIN2 NOT NULL,
    Barkod      NVARCHAR(50)    COLLATE Latin1_General_100_BIN2 NULL,
    Renk        NVARCHAR(50)    COLLATE Turkish_CI_AS NULL,
    Beden       NVARCHAR(30)    COLLATE Turkish_CI_AS NULL,
    CONSTRAINT PK_Sku PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_Sku_Product FOREIGN KEY (ProductId) REFERENCES dbo.Product (Id),
    CONSTRAINT UQ_Sku_SkuKodu UNIQUE (SkuKodu)
);
CREATE INDEX IX_Sku_ProductId ON dbo.Sku (ProductId);
-- Barkod benzersiz, ancak NULL değerler hariç (filtreli unique index)
CREATE UNIQUE INDEX UX_Sku_Barkod ON dbo.Sku (Barkod) WHERE Barkod IS NOT NULL;

CREATE TABLE dbo.Warehouse
(
    Id  INT             IDENTITY(1,1) NOT NULL,
    Ad  NVARCHAR(100)   COLLATE Turkish_CI_AS NOT NULL,
    CONSTRAINT PK_Warehouse PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT UQ_Warehouse_Ad UNIQUE (Ad)
);

-------------------------------------------------------------------------------
-- Stok defteri
-------------------------------------------------------------------------------
CREATE TABLE dbo.StockBalance
(
    SkuId           INT         NOT NULL,
    WarehouseId     INT         NOT NULL,
    Fiziksel        INT         NOT NULL CONSTRAINT DF_StockBalance_Fiziksel DEFAULT (0),
    Rezerve         INT         NOT NULL CONSTRAINT DF_StockBalance_Rezerve DEFAULT (0),
    GuvenlikStogu   INT         NOT NULL CONSTRAINT DF_StockBalance_GuvenlikStogu DEFAULT (0),
    -- Negatif olabilir (güvenlik stoğu > serbest stok); kanala gönderimde 0'a sabitlenir (StockAllocator).
    Satilabilir     AS (Fiziksel - Rezerve - GuvenlikStogu) PERSISTED,
    RowVer          ROWVERSION  NOT NULL,
    CONSTRAINT PK_StockBalance PRIMARY KEY CLUSTERED (SkuId, WarehouseId),
    CONSTRAINT FK_StockBalance_Sku FOREIGN KEY (SkuId) REFERENCES dbo.Sku (Id),
    CONSTRAINT FK_StockBalance_Warehouse FOREIGN KEY (WarehouseId) REFERENCES dbo.Warehouse (Id),
    CONSTRAINT CK_StockBalance_Fiziksel CHECK (Fiziksel >= 0),
    CONSTRAINT CK_StockBalance_Rezerve CHECK (Rezerve >= 0),
    CONSTRAINT CK_StockBalance_RezerveFiziksel CHECK (Rezerve <= Fiziksel),
    CONSTRAINT CK_StockBalance_GuvenlikStogu CHECK (GuvenlikStogu >= 0)
);

-- Append-only hareket defteri. Adet: SAYIM dışındaki tiplerde pozitif miktar;
-- SAYIM'da işaretli fark (yeni fiziksel - eski fiziksel), 0 olabilir.
CREATE TABLE dbo.StockMovement
(
    Id          BIGINT          IDENTITY(1,1) NOT NULL,
    SkuId       INT             NOT NULL,
    WarehouseId INT             NOT NULL,
    Tip         VARCHAR(20)     COLLATE Latin1_General_100_BIN2 NOT NULL,
    Adet        INT             NOT NULL,
    KaynakTip   VARCHAR(30)     COLLATE Latin1_General_100_BIN2 NULL,
    KaynakId    NVARCHAR(100)   COLLATE Latin1_General_100_BIN2 NULL,
    Tarih       DATETIME2(3)    NOT NULL CONSTRAINT DF_StockMovement_Tarih DEFAULT (SYSUTCDATETIME()),
    Kullanici   NVARCHAR(100)   COLLATE Turkish_CI_AS NULL,
    CONSTRAINT PK_StockMovement PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_StockMovement_Sku FOREIGN KEY (SkuId) REFERENCES dbo.Sku (Id),
    CONSTRAINT FK_StockMovement_Warehouse FOREIGN KEY (WarehouseId) REFERENCES dbo.Warehouse (Id),
    CONSTRAINT CK_StockMovement_Tip CHECK (Tip IN ('GIRIS', 'REZERVE', 'SEVK', 'REZERVE_IPTAL', 'IADE_GIRIS', 'SAYIM')),
    CONSTRAINT CK_StockMovement_Adet CHECK (Adet > 0 OR Tip = 'SAYIM')
);
CREATE INDEX IX_StockMovement_SkuId_Tarih ON dbo.StockMovement (SkuId, Tarih DESC);

-------------------------------------------------------------------------------
-- Kanallar
-------------------------------------------------------------------------------
CREATE TABLE dbo.Channel
(
    Id              INT             IDENTITY(1,1) NOT NULL,
    Kod             VARCHAR(20)     COLLATE Latin1_General_100_BIN2 NOT NULL,
    Ad              NVARCHAR(100)   COLLATE Turkish_CI_AS NOT NULL,
    PayYuzde        DECIMAL(5,2)    NOT NULL CONSTRAINT DF_Channel_PayYuzde DEFAULT (100),
    Aktif           BIT             NOT NULL CONSTRAINT DF_Channel_Aktif DEFAULT (1),
    Ayarlar         NVARCHAR(MAX)   COLLATE Turkish_CI_AS NULL,
    KimlikSifreli   VARBINARY(MAX)  NULL,
    CONSTRAINT PK_Channel PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT UQ_Channel_Kod UNIQUE (Kod),
    CONSTRAINT CK_Channel_Kod CHECK (Kod IN ('TRENDYOL', 'HEPSIBURADA', 'BEYMEN', 'SOFTCOTTON')),
    CONSTRAINT CK_Channel_PayYuzde CHECK (PayYuzde BETWEEN 0 AND 100),
    CONSTRAINT CK_Channel_Ayarlar CHECK (Ayarlar IS NULL OR ISJSON(Ayarlar) = 1)
);

CREATE TABLE dbo.ChannelListing
(
    Id                  INT             IDENTITY(1,1) NOT NULL,
    SkuId               INT             NOT NULL,
    ChannelId           INT             NOT NULL,
    KanalUrunKodu       NVARCHAR(100)   COLLATE Latin1_General_100_BIN2 NULL,
    KanalBarkod         NVARCHAR(50)    COLLATE Latin1_General_100_BIN2 NULL,
    SonGonderilenStok   INT             NULL,
    SonSenkron          DATETIME2(3)    NULL,
    Durum               VARCHAR(20)     COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT DF_ChannelListing_Durum DEFAULT ('AKTIF'),
    KanalVerisi         NVARCHAR(MAX)   COLLATE Turkish_CI_AS NULL,
    CONSTRAINT PK_ChannelListing PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_ChannelListing_Sku FOREIGN KEY (SkuId) REFERENCES dbo.Sku (Id),
    CONSTRAINT FK_ChannelListing_Channel FOREIGN KEY (ChannelId) REFERENCES dbo.Channel (Id),
    CONSTRAINT UQ_ChannelListing_Sku_Channel UNIQUE (SkuId, ChannelId),
    CONSTRAINT CK_ChannelListing_Durum CHECK (Durum IN ('AKTIF', 'PASIF')),
    CONSTRAINT CK_ChannelListing_KanalVerisi CHECK (KanalVerisi IS NULL OR ISJSON(KanalVerisi) = 1)
);
CREATE INDEX IX_ChannelListing_Channel_KanalUrunKodu ON dbo.ChannelListing (ChannelId, KanalUrunKodu) WHERE KanalUrunKodu IS NOT NULL;
CREATE INDEX IX_ChannelListing_Channel_KanalBarkod ON dbo.ChannelListing (ChannelId, KanalBarkod) WHERE KanalBarkod IS NOT NULL;

CREATE TABLE dbo.ChannelStockSnapshot
(
    Id              BIGINT          IDENTITY(1,1) NOT NULL,
    ListingId       INT             NOT NULL,
    KanalStok       INT             NOT NULL,
    CekilmeZamani   DATETIME2(3)    NOT NULL CONSTRAINT DF_ChannelStockSnapshot_CekilmeZamani DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_ChannelStockSnapshot PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_ChannelStockSnapshot_Listing FOREIGN KEY (ListingId) REFERENCES dbo.ChannelListing (Id)
);
CREATE INDEX IX_ChannelStockSnapshot_Listing_Zaman ON dbo.ChannelStockSnapshot (ListingId, CekilmeZamani DESC);

-------------------------------------------------------------------------------
-- Siparişler
-------------------------------------------------------------------------------
CREATE TABLE dbo.[Order]
(
    Id              BIGINT          IDENTITY(1,1) NOT NULL,
    ChannelId       INT             NOT NULL,
    KanalSiparisNo  NVARCHAR(100)   COLLATE Latin1_General_100_BIN2 NOT NULL,
    Durum           VARCHAR(20)     COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT DF_Order_Durum DEFAULT ('Yeni'),
    Toplam          DECIMAL(18,2)   NOT NULL,
    SiparisTarihi   DATETIME2(3)    NOT NULL,
    MusteriOzet     NVARCHAR(MAX)   COLLATE Turkish_CI_AS NULL,
    KargoBilgisi    NVARCHAR(MAX)   COLLATE Turkish_CI_AS NULL,
    -- İlke 7: oversell'de sipariş reddedilmez, bu bayrakla işaretlenir (spesifikasyondaki kolon listesine ek).
    StokYetersiz    BIT             NOT NULL CONSTRAINT DF_Order_StokYetersiz DEFAULT (0),
    CONSTRAINT PK_Order PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_Order_Channel FOREIGN KEY (ChannelId) REFERENCES dbo.Channel (Id),
    -- İlke 4: idempotentlik
    CONSTRAINT UQ_Order_Channel_KanalSiparisNo UNIQUE (ChannelId, KanalSiparisNo),
    CONSTRAINT CK_Order_Durum CHECK (Durum IN ('Yeni', 'Onaylandi', 'Hazirlaniyor', 'Kargoda', 'TeslimEdildi', 'Iptal', 'Iade')),
    CONSTRAINT CK_Order_MusteriOzet CHECK (MusteriOzet IS NULL OR ISJSON(MusteriOzet) = 1),
    CONSTRAINT CK_Order_KargoBilgisi CHECK (KargoBilgisi IS NULL OR ISJSON(KargoBilgisi) = 1)
);
CREATE INDEX IX_Order_SiparisTarihi ON dbo.[Order] (SiparisTarihi DESC) INCLUDE (ChannelId, Durum, Toplam);
CREATE INDEX IX_Order_StokYetersiz ON dbo.[Order] (SiparisTarihi DESC) WHERE StokYetersiz = 1;

CREATE TABLE dbo.OrderLine
(
    Id          BIGINT          IDENTITY(1,1) NOT NULL,
    OrderId     BIGINT          NOT NULL,
    -- NULL: kanaldan gelen ürün henüz bir SKU ile eşleştirilemedi (sipariş yine de kaydedilir).
    SkuId       INT             NULL,
    Adet        INT             NOT NULL,
    BirimFiyat  DECIMAL(18,2)   NOT NULL,
    Komisyon    DECIMAL(18,2)   NULL,
    CONSTRAINT PK_OrderLine PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_OrderLine_Order FOREIGN KEY (OrderId) REFERENCES dbo.[Order] (Id),
    CONSTRAINT FK_OrderLine_Sku FOREIGN KEY (SkuId) REFERENCES dbo.Sku (Id),
    CONSTRAINT CK_OrderLine_Adet CHECK (Adet > 0)
);
CREATE INDEX IX_OrderLine_OrderId ON dbo.OrderLine (OrderId);
CREATE INDEX IX_OrderLine_SkuId ON dbo.OrderLine (SkuId) WHERE SkuId IS NOT NULL;

CREATE TABLE dbo.OrderEvent
(
    Id          BIGINT          IDENTITY(1,1) NOT NULL,
    OrderId     BIGINT          NOT NULL,
    EskiDurum   VARCHAR(20)     COLLATE Latin1_General_100_BIN2 NULL,
    YeniDurum   VARCHAR(20)     COLLATE Latin1_General_100_BIN2 NOT NULL,
    Kaynak      NVARCHAR(50)    COLLATE Turkish_CI_AS NOT NULL,
    Tarih       DATETIME2(3)    NOT NULL CONSTRAINT DF_OrderEvent_Tarih DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_OrderEvent PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_OrderEvent_Order FOREIGN KEY (OrderId) REFERENCES dbo.[Order] (Id)
);
CREATE INDEX IX_OrderEvent_Order_Tarih ON dbo.OrderEvent (OrderId, Tarih);

-------------------------------------------------------------------------------
-- Olay aktarımı ve senkron kuyruğu
-------------------------------------------------------------------------------
CREATE TABLE dbo.Outbox
(
    Id          BIGINT          IDENTITY(1,1) NOT NULL,
    OlayTipi    VARCHAR(50)     COLLATE Latin1_General_100_BIN2 NOT NULL,
    Payload     NVARCHAR(MAX)   COLLATE Turkish_CI_AS NOT NULL,
    Olusma      DATETIME2(3)    NOT NULL CONSTRAINT DF_Outbox_Olusma DEFAULT (SYSUTCDATETIME()),
    IslendiAt   DATETIME2(3)    NULL,
    CONSTRAINT PK_Outbox PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT CK_Outbox_Payload CHECK (ISJSON(Payload) = 1)
);
-- Yalnızca işlenmemiş olaylar (OutboxIsle: TOP(100) ... WITH (ROWLOCK, UPDLOCK, READPAST))
CREATE INDEX IX_Outbox_Bekleyen ON dbo.Outbox (Id) INCLUDE (OlayTipi) WHERE IslendiAt IS NULL;

CREATE TABLE dbo.SyncQueue
(
    Id          BIGINT          IDENTITY(1,1) NOT NULL,
    ListingId   INT             NOT NULL,
    Tip         VARCHAR(10)     COLLATE Latin1_General_100_BIN2 NOT NULL,
    Durum       VARCHAR(20)     COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT DF_SyncQueue_Durum DEFAULT ('BEKLIYOR'),
    Deneme      INT             NOT NULL CONSTRAINT DF_SyncQueue_Deneme DEFAULT (0),
    Hata        NVARCHAR(2000)  COLLATE Turkish_CI_AS NULL,
    BatchId     NVARCHAR(100)   COLLATE Latin1_General_100_BIN2 NULL,
    Olusma      DATETIME2(3)    NOT NULL CONSTRAINT DF_SyncQueue_Olusma DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_SyncQueue PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_SyncQueue_Listing FOREIGN KEY (ListingId) REFERENCES dbo.ChannelListing (Id),
    CONSTRAINT CK_SyncQueue_Tip CHECK (Tip IN ('STOK', 'FIYAT')),
    CONSTRAINT CK_SyncQueue_Durum CHECK (Durum IN ('BEKLIYOR', 'GONDERILDI', 'HATA')),
    CONSTRAINT CK_SyncQueue_Deneme CHECK (Deneme >= 0)
);
-- İlke 6 (debounce): aynı listing + tip için yalnızca bir bekleyen kayıt
CREATE UNIQUE INDEX UX_SyncQueue_Listing_Tip_Bekleyen ON dbo.SyncQueue (ListingId, Tip) WHERE Durum = 'BEKLIYOR';
CREATE INDEX IX_SyncQueue_Durum ON dbo.SyncQueue (Durum, Olusma) INCLUDE (ListingId, Tip, BatchId);
