/*
    0006: Kanal kataloğu içe alma (docs/decisions.md ADR-020, ADR-021).
      - Product.ModelKodu : kanalların "Model Kodu" alanı (ürün grubu). Eşleştirmenin 3. adımı (model + renk + beden) bunu kullanır.
                            Kod kolonu olduğu için BIN2 (Türkçe i/İ sorunu yok).
      - UnmatchedChannelItem : kanal dosyasında / API'sinde olup hiçbir SKU'ya bağlanamayan ürünler.
                            (ChannelId, KanalAnahtar) tekil: aynı dosya tekrar yüklenince yeni kayıt açılmaz, SonGorulme güncellenir.
                            KanalAnahtar = kanal barkodu, yoksa kanal ürün kodu.
                            Ürün adı benzerliği yalnızca ÖNERİ olarak tutulur (OnerilenSkuId); otomatik bağlama yapılmaz.
                            Kişisel veri tutulmaz (yalnızca ürün alanları).
*/

ALTER TABLE dbo.Product ADD ModelKodu NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NULL;
GO

CREATE INDEX IX_Product_ModelKodu ON dbo.Product (ModelKodu) WHERE ModelKodu IS NOT NULL;

CREATE TABLE dbo.UnmatchedChannelItem
(
    Id              INT             IDENTITY(1,1) NOT NULL,
    ChannelId       INT             NOT NULL,
    Kaynak          VARCHAR(10)     COLLATE Latin1_General_100_BIN2 NOT NULL,
    KanalAnahtar    NVARCHAR(100)   COLLATE Latin1_General_100_BIN2 NOT NULL,
    KanalUrunKodu   NVARCHAR(100)   COLLATE Latin1_General_100_BIN2 NULL,
    KanalBarkod     NVARCHAR(50)    COLLATE Latin1_General_100_BIN2 NULL,
    KanalStokKodu   NVARCHAR(100)   COLLATE Latin1_General_100_BIN2 NULL,
    ModelKodu       NVARCHAR(100)   COLLATE Latin1_General_100_BIN2 NULL,
    Renk            NVARCHAR(50)    COLLATE Turkish_CI_AS NULL,
    Beden           NVARCHAR(30)    COLLATE Turkish_CI_AS NULL,
    UrunAdi         NVARCHAR(300)   COLLATE Turkish_CI_AS NULL,
    Marka           NVARCHAR(100)   COLLATE Turkish_CI_AS NULL,
    Kategori        NVARCHAR(200)   COLLATE Turkish_CI_AS NULL,
    KanalDurumu     NVARCHAR(100)   COLLATE Turkish_CI_AS NULL,
    Neden           VARCHAR(20)     COLLATE Latin1_General_100_BIN2 NOT NULL,
    NedenAciklama   NVARCHAR(400)   COLLATE Turkish_CI_AS NULL,
    OnerilenSkuId   INT             NULL,
    OneriSkoru      DECIMAL(5,2)    NULL,
    Durum           VARCHAR(20)     COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT DF_UnmatchedChannelItem_Durum DEFAULT ('BEKLIYOR'),
    IlkGorulme      DATETIME2(3)    NOT NULL CONSTRAINT DF_UnmatchedChannelItem_IlkGorulme DEFAULT (SYSUTCDATETIME()),
    SonGorulme      DATETIME2(3)    NOT NULL CONSTRAINT DF_UnmatchedChannelItem_SonGorulme DEFAULT (SYSUTCDATETIME()),
    Cozen           NVARCHAR(100)   COLLATE Turkish_CI_AS NULL,
    CozulmeTarihi   DATETIME2(3)    NULL,
    CONSTRAINT PK_UnmatchedChannelItem PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_UnmatchedChannelItem_Channel FOREIGN KEY (ChannelId) REFERENCES dbo.Channel (Id),
    -- Önerilen SKU silinirse öneri düşer (kayıt kalır)
    CONSTRAINT FK_UnmatchedChannelItem_OnerilenSku FOREIGN KEY (OnerilenSkuId) REFERENCES dbo.Sku (Id) ON DELETE SET NULL,
    CONSTRAINT UQ_UnmatchedChannelItem_Channel_Anahtar UNIQUE (ChannelId, KanalAnahtar),
    CONSTRAINT CK_UnmatchedChannelItem_Kaynak CHECK (Kaynak IN ('EXCEL', 'API')),
    CONSTRAINT CK_UnmatchedChannelItem_Neden CHECK (Neden IN ('ESLESME_YOK', 'BELIRSIZ', 'CAKISMA')),
    CONSTRAINT CK_UnmatchedChannelItem_Durum CHECK (Durum IN ('BEKLIYOR', 'BAGLANDI', 'YOKSAYILDI')),
    CONSTRAINT CK_UnmatchedChannelItem_OneriSkoru CHECK (OneriSkoru IS NULL OR OneriSkoru BETWEEN 0 AND 1)
);

CREATE INDEX IX_UnmatchedChannelItem_Channel_Durum ON dbo.UnmatchedChannelItem (ChannelId, Durum) INCLUDE (SonGorulme);
CREATE INDEX IX_UnmatchedChannelItem_OnerilenSku ON dbo.UnmatchedChannelItem (OnerilenSkuId) WHERE OnerilenSkuId IS NOT NULL;
