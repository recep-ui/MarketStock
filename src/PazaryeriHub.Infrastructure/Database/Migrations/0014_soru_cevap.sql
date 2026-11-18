/*
    0014: Müşteri soruları ve cevapları (ADR-029, M5). Spesifikasyonun tablo listesine EK.
      - CustomerQuestion: kanal sorusu. UNIQUE(ChannelId, KanalSoruNo): aynı soru kaç kez gelirse gelsin tek kayıt.
        KVKK: müşteri adı, müşteri kimliği ve iletişim bilgisi SAKLANMAZ; yalnızca soru metni, ürün bilgisi ve tarihler.
      - QuestionAnswerLog: gönderilen her cevap (metin, kullanıcı, zaman, kanal komutu). Yalnızca eklenir (denetim izi).
      - ChannelJobState.IsTipi'ne 'SORU_CEK' eklenir.
*/

CREATE TABLE dbo.CustomerQuestion
(
    Id              BIGINT IDENTITY(1,1) NOT NULL,
    ChannelId       INT             NOT NULL,
    KanalSoruNo     NVARCHAR(100)   COLLATE Latin1_General_100_BIN2 NOT NULL,
    SoruMetni       NVARCHAR(4000)  COLLATE Turkish_CI_AS NOT NULL,
    UrunAdi         NVARCHAR(500)   COLLATE Turkish_CI_AS NULL,
    UrunKodu        NVARCHAR(100)   COLLATE Latin1_General_100_BIN2 NULL,
    SkuId           INT             NULL,
    Durum           VARCHAR(15)     COLLATE Latin1_General_100_BIN2 NOT NULL,
    KanalDurum      NVARCHAR(50)    COLLATE Turkish_CI_AS NULL,
    KanalCevabi     NVARCHAR(4000)  COLLATE Turkish_CI_AS NULL,
    SoruTarihi      DATETIME2(3)    NOT NULL,
    SonCevapTarihi  DATETIME2(3)    NULL,
    IlkGorulme      DATETIME2(3)    NOT NULL CONSTRAINT DF_CustomerQuestion_IlkGorulme DEFAULT (SYSUTCDATETIME()),
    SonGuncelleme   DATETIME2(3)    NOT NULL CONSTRAINT DF_CustomerQuestion_SonGuncelleme DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_CustomerQuestion PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_CustomerQuestion_Channel FOREIGN KEY (ChannelId) REFERENCES dbo.Channel (Id),
    CONSTRAINT FK_CustomerQuestion_Sku FOREIGN KEY (SkuId) REFERENCES dbo.Sku (Id),
    CONSTRAINT UQ_CustomerQuestion_Channel_No UNIQUE (ChannelId, KanalSoruNo),
    CONSTRAINT CK_CustomerQuestion_Durum CHECK (Durum IN ('CEVAP_BEKLIYOR', 'CEVAPLANDI', 'KAPANDI'))
);
CREATE INDEX IX_CustomerQuestion_Durum ON dbo.CustomerQuestion (Durum, SoruTarihi DESC) INCLUDE (ChannelId, SonCevapTarihi);

CREATE TABLE dbo.QuestionAnswerLog
(
    Id          BIGINT IDENTITY(1,1) NOT NULL,
    QuestionId  BIGINT          NOT NULL,
    CevapMetni  NVARCHAR(2000)  COLLATE Turkish_CI_AS NOT NULL,
    Kullanici   NVARCHAR(100)   COLLATE Turkish_CI_AS NULL,
    Tarih       DATETIME2(3)    NOT NULL CONSTRAINT DF_QuestionAnswerLog_Tarih DEFAULT (SYSUTCDATETIME()),
    CommandId   BIGINT          NULL,
    CONSTRAINT PK_QuestionAnswerLog PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_QuestionAnswerLog_Question FOREIGN KEY (QuestionId) REFERENCES dbo.CustomerQuestion (Id),
    CONSTRAINT FK_QuestionAnswerLog_Command FOREIGN KEY (CommandId) REFERENCES dbo.ChannelCommand (Id)
);
CREATE INDEX IX_QuestionAnswerLog_Question ON dbo.QuestionAnswerLog (QuestionId, Tarih);

ALTER TABLE dbo.ChannelJobState DROP CONSTRAINT CK_ChannelJobState_IsTipi;
ALTER TABLE dbo.ChannelJobState ADD CONSTRAINT CK_ChannelJobState_IsTipi
    CHECK (IsTipi IN ('SIPARIS_CEK', 'IADE_CEK', 'MUTABAKAT', 'STOK_GONDER', 'KATALOG_CEK', 'REFERANS_CEK', 'SORU_CEK'));
