/*
    0012: Kanal iade talepleri (ADR-027, M3). Spesifikasyonun tablo listesine EK; spesifikasyon akış 6 (IadeCek).
      - ReturnRequest: kanal iade talebi. UNIQUE(ChannelId, KanalIadeNo): aynı talep kaç kez gelirse gelsin tek kayıt.
        KVKK: müşteri adı/iletişim SAKLANMAZ; yalnızca sipariş no, gerekçe metni ve kanal durumu.
      - ReturnRequestLine: talep satırı. SatirAnahtar kanal içinde satırı tanımlar (Trendyol sipariş satırı id, Beymen productId);
        KanalKalemleri JSON = kanal karar servislerinin istediği kalem kimlikleri (Trendyol claimItem id'leri).
      - ReturnDecision: Yönetici kararları (ONAY | RED | TESLIM_ALINDI); yalnızca eklenir (denetim izi).
      - Depoya giriş: usp_IadeGiris, KaynakTip 'IADE_SATIRI', KaynakId 'RL:{satır}'; her iade satırı EN FAZLA bir kez.
*/

CREATE TABLE dbo.ReturnRequest
(
    Id              BIGINT IDENTITY(1,1) NOT NULL,
    ChannelId       INT            NOT NULL,
    KanalIadeNo     NVARCHAR(100)  COLLATE Latin1_General_100_BIN2 NOT NULL,
    KanalSiparisNo  NVARCHAR(100)  COLLATE Latin1_General_100_BIN2 NOT NULL,
    OrderId         BIGINT         NULL,
    KanalDurum      NVARCHAR(50)   COLLATE Turkish_CI_AS NULL,
    Gerekce         NVARCHAR(400)  COLLATE Turkish_CI_AS NULL,
    TalepTarihi     DATETIME2(3)   NOT NULL,
    IlkGorulme      DATETIME2(3)   NOT NULL CONSTRAINT DF_ReturnRequest_IlkGorulme DEFAULT (SYSUTCDATETIME()),
    SonGuncelleme   DATETIME2(3)   NOT NULL CONSTRAINT DF_ReturnRequest_SonGuncelleme DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_ReturnRequest PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_ReturnRequest_Channel FOREIGN KEY (ChannelId) REFERENCES dbo.Channel (Id),
    CONSTRAINT FK_ReturnRequest_Order FOREIGN KEY (OrderId) REFERENCES dbo.[Order] (Id),
    CONSTRAINT UQ_ReturnRequest_Channel_No UNIQUE (ChannelId, KanalIadeNo)
);
CREATE INDEX IX_ReturnRequest_Tarih ON dbo.ReturnRequest (TalepTarihi DESC) INCLUDE (ChannelId, KanalDurum, OrderId);
CREATE INDEX IX_ReturnRequest_Order ON dbo.ReturnRequest (OrderId) WHERE OrderId IS NOT NULL;

CREATE TABLE dbo.ReturnRequestLine
(
    Id              BIGINT IDENTITY(1,1) NOT NULL,
    ReturnRequestId BIGINT         NOT NULL,
    SatirAnahtar    NVARCHAR(200)  COLLATE Latin1_General_100_BIN2 NOT NULL,
    KanalKalemleri  NVARCHAR(MAX)  COLLATE Latin1_General_100_BIN2 NULL,
    Barkod          NVARCHAR(100)  COLLATE Latin1_General_100_BIN2 NULL,
    KanalUrunKodu   NVARCHAR(100)  COLLATE Latin1_General_100_BIN2 NULL,
    OrderLineId     BIGINT         NULL,
    SkuId           INT            NULL,
    Adet            INT            NOT NULL,
    CONSTRAINT PK_ReturnRequestLine PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_ReturnRequestLine_Request FOREIGN KEY (ReturnRequestId) REFERENCES dbo.ReturnRequest (Id),
    CONSTRAINT FK_ReturnRequestLine_OrderLine FOREIGN KEY (OrderLineId) REFERENCES dbo.OrderLine (Id),
    CONSTRAINT FK_ReturnRequestLine_Sku FOREIGN KEY (SkuId) REFERENCES dbo.Sku (Id),
    CONSTRAINT UQ_ReturnRequestLine_Anahtar UNIQUE (ReturnRequestId, SatirAnahtar),
    CONSTRAINT CK_ReturnRequestLine_Adet CHECK (Adet > 0),
    CONSTRAINT CK_ReturnRequestLine_Kalemler CHECK (KanalKalemleri IS NULL OR ISJSON(KanalKalemleri) = 1)
);
CREATE INDEX IX_ReturnRequestLine_OrderLine ON dbo.ReturnRequestLine (OrderLineId) WHERE OrderLineId IS NOT NULL;

CREATE TABLE dbo.ReturnDecision
(
    Id              BIGINT IDENTITY(1,1) NOT NULL,
    ReturnRequestId BIGINT         NOT NULL,
    Karar           VARCHAR(15)    COLLATE Latin1_General_100_BIN2 NOT NULL,
    GerekceKodu     NVARCHAR(100)  COLLATE Latin1_General_100_BIN2 NULL,
    Aciklama        NVARCHAR(500)  COLLATE Turkish_CI_AS NULL,
    Kullanici       NVARCHAR(100)  COLLATE Turkish_CI_AS NULL,
    Tarih           DATETIME2(3)   NOT NULL CONSTRAINT DF_ReturnDecision_Tarih DEFAULT (SYSUTCDATETIME()),
    CommandId       BIGINT         NULL,
    CONSTRAINT PK_ReturnDecision PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_ReturnDecision_Request FOREIGN KEY (ReturnRequestId) REFERENCES dbo.ReturnRequest (Id),
    CONSTRAINT FK_ReturnDecision_Command FOREIGN KEY (CommandId) REFERENCES dbo.ChannelCommand (Id),
    CONSTRAINT CK_ReturnDecision_Karar CHECK (Karar IN ('ONAY', 'RED', 'TESLIM_ALINDI'))
);
CREATE INDEX IX_ReturnDecision_Request ON dbo.ReturnDecision (ReturnRequestId, Tarih);

-- İade satırı başına tek depoya giriş (ilke 4)
CREATE UNIQUE INDEX UX_StockMovement_IadeSatiri ON dbo.StockMovement (KaynakId, Tip) WHERE KaynakTip = 'IADE_SATIRI';
