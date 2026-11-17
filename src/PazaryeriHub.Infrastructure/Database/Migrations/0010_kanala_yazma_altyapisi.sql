/*
    0010: Kanala yazma altyapısı (ADR-026). Spesifikasyonun tablo listesine EK.

    ChannelCapability: kanal başına yazma yeteneği açma/kapama.
      - Yetenek: STOK | SIPARIS_DURUM | IADE_KARAR | FIYAT | SORU_CEVAP
      - Durum:   KAPALI (hiçbir şey gitmez) | DENEME (her kayıt Yönetici onayıyla tek tek gider) | ACIK
      - Mevcut davranış bozulmasın diye STOK = ACIK, diğerleri KAPALI tohumlanır.

    ChannelCommand: kanala giden yazma işlemleri kuyruğu (stok/fiyat hariç; onlar SyncQueue'da).
      - Değer oluşturulma anında Payload'a yazılır (gönderilecek içerik onaydan önce görülebilsin).
      - UNIQUE(IdempotencyKey) WHERE Durum <> 'IPTAL': aynı bildirim ikinci kez oluşmaz; iptal edilen yeniden oluşturulabilir.
      - Debounce: aynı hedef için tek bekleyen kayıt (ONAY_BEKLIYOR | BEKLIYOR).
      - TalepNo: KanalKomutGonder'in READPAST ile aldığı kayıtlar (süreç çökerse süre dolunca serbest bırakılır).
*/

CREATE TABLE dbo.ChannelCapability
(
    ChannelId     INT           NOT NULL,
    Yetenek       VARCHAR(20)   COLLATE Latin1_General_100_BIN2 NOT NULL,
    Durum         VARCHAR(10)   COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT DF_ChannelCapability_Durum DEFAULT ('KAPALI'),
    Degistiren    NVARCHAR(100) COLLATE Turkish_CI_AS NULL,
    DegisimTarihi DATETIME2(3)  NOT NULL CONSTRAINT DF_ChannelCapability_Tarih DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_ChannelCapability PRIMARY KEY CLUSTERED (ChannelId, Yetenek),
    CONSTRAINT FK_ChannelCapability_Channel FOREIGN KEY (ChannelId) REFERENCES dbo.Channel (Id),
    CONSTRAINT CK_ChannelCapability_Yetenek CHECK (Yetenek IN ('STOK', 'SIPARIS_DURUM', 'IADE_KARAR', 'FIYAT', 'SORU_CEVAP')),
    CONSTRAINT CK_ChannelCapability_Durum CHECK (Durum IN ('KAPALI', 'DENEME', 'ACIK'))
);

INSERT dbo.ChannelCapability (ChannelId, Yetenek, Durum, Degistiren)
SELECT c.Id, y.Yetenek, CASE WHEN y.Yetenek = 'STOK' THEN 'ACIK' ELSE 'KAPALI' END, N'migration 0010'
  FROM dbo.Channel AS c
 CROSS JOIN (VALUES ('STOK'), ('SIPARIS_DURUM'), ('IADE_KARAR'), ('FIYAT'), ('SORU_CEVAP')) AS y (Yetenek);

CREATE TABLE dbo.ChannelCommand
(
    Id             BIGINT IDENTITY(1,1) NOT NULL,
    ChannelId      INT            NOT NULL,
    Tip            VARCHAR(20)    COLLATE Latin1_General_100_BIN2 NOT NULL,
    HedefAnahtar   NVARCHAR(200)  COLLATE Latin1_General_100_BIN2 NOT NULL,
    Payload        NVARCHAR(MAX)  COLLATE Turkish_CI_AS NOT NULL,
    IdempotencyKey NVARCHAR(300)  COLLATE Latin1_General_100_BIN2 NOT NULL,
    Durum          VARCHAR(15)    COLLATE Latin1_General_100_BIN2 NOT NULL,
    Deneme         INT            NOT NULL CONSTRAINT DF_ChannelCommand_Deneme DEFAULT (0),
    SonrakiDeneme  DATETIME2(3)   NULL,
    SonDeneme      DATETIME2(3)   NULL,
    Hata           NVARCHAR(1000) COLLATE Turkish_CI_AS NULL,
    KanalIslemNo   NVARCHAR(200)  COLLATE Latin1_General_100_BIN2 NULL,
    TalepNo        VARCHAR(50)    COLLATE Latin1_General_100_BIN2 NULL,
    TalepZamani    DATETIME2(3)   NULL,
    Olusturan      NVARCHAR(100)  COLLATE Turkish_CI_AS NULL,
    Olusma         DATETIME2(3)   NOT NULL CONSTRAINT DF_ChannelCommand_Olusma DEFAULT (SYSUTCDATETIME()),
    Onaylayan      NVARCHAR(100)  COLLATE Turkish_CI_AS NULL,
    OnayTarihi     DATETIME2(3)   NULL,
    Gonderilme     DATETIME2(3)   NULL,
    CONSTRAINT PK_ChannelCommand PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_ChannelCommand_Channel FOREIGN KEY (ChannelId) REFERENCES dbo.Channel (Id),
    CONSTRAINT CK_ChannelCommand_Tip CHECK (Tip IN ('SIPARIS_DURUM', 'IADE_KARAR', 'SORU_CEVAP')),
    CONSTRAINT CK_ChannelCommand_Durum CHECK (Durum IN ('ONAY_BEKLIYOR', 'BEKLIYOR', 'GONDERILDI', 'HATA', 'IPTAL')),
    CONSTRAINT CK_ChannelCommand_Payload CHECK (ISJSON(Payload) = 1)
);

CREATE UNIQUE INDEX UX_ChannelCommand_Idempotency ON dbo.ChannelCommand (IdempotencyKey) WHERE Durum <> 'IPTAL';
CREATE UNIQUE INDEX UX_ChannelCommand_Hedef_Bekleyen ON dbo.ChannelCommand (ChannelId, Tip, HedefAnahtar)
    WHERE Durum IN ('ONAY_BEKLIYOR', 'BEKLIYOR');
CREATE INDEX IX_ChannelCommand_Gonderim ON dbo.ChannelCommand (ChannelId, Durum, SonrakiDeneme) INCLUDE (Tip, TalepNo);
CREATE INDEX IX_ChannelCommand_Olusma ON dbo.ChannelCommand (Olusma DESC) INCLUDE (ChannelId, Tip, Durum);
