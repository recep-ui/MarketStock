/*
    0009: Satıcı Bilgileri (kanal referans verisi; ADR-025). Spesifikasyonun tablo listesine EK.
      - ChannelReference: kanalın kargo firması ve iade red gerekçesi listeleri. Yalnızca diğer modüllerin ihtiyaç duyduğu
        tipler tutulur (Teslimat: KARGO_FIRMASI, İade kararı: IADE_RED_GEREKCE). Kişisel veri yok.
      - Kaynak: 'API' (kanal servisinden çekildi) | 'DOKUMAN' (kanal dokümanında sabit liste olarak verilmiş).
      - Kanalda artık bulunmayan kayıt silinmez, Aktif = 0 olur (geçmiş kararlar kimliğe referans verebilir).
      - ChannelJobState.IsTipi'ne 'REFERANS_CEK' eklenir.
*/

CREATE TABLE dbo.ChannelReference
(
    Id            INT IDENTITY(1,1) NOT NULL,
    ChannelId     INT               NOT NULL,
    Tip           VARCHAR(30)       COLLATE Latin1_General_100_BIN2 NOT NULL,
    KanalKimlik   NVARCHAR(100)     COLLATE Latin1_General_100_BIN2 NOT NULL,
    Ad            NVARCHAR(400)     COLLATE Turkish_CI_AS NOT NULL,
    Veri          NVARCHAR(MAX)     COLLATE Turkish_CI_AS NULL,
    Kaynak        VARCHAR(10)       COLLATE Latin1_General_100_BIN2 NOT NULL,
    CekilmeZamani DATETIME2(3)      NOT NULL,
    Aktif         BIT               NOT NULL CONSTRAINT DF_ChannelReference_Aktif DEFAULT (1),
    CONSTRAINT PK_ChannelReference PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_ChannelReference_Channel FOREIGN KEY (ChannelId) REFERENCES dbo.Channel (Id),
    CONSTRAINT UQ_ChannelReference_Channel_Tip_Kimlik UNIQUE (ChannelId, Tip, KanalKimlik),
    CONSTRAINT CK_ChannelReference_Tip CHECK (Tip IN ('KARGO_FIRMASI', 'IADE_RED_GEREKCE')),
    CONSTRAINT CK_ChannelReference_Kaynak CHECK (Kaynak IN ('API', 'DOKUMAN')),
    CONSTRAINT CK_ChannelReference_Veri CHECK (Veri IS NULL OR ISJSON(Veri) = 1)
);

ALTER TABLE dbo.ChannelJobState DROP CONSTRAINT CK_ChannelJobState_IsTipi;
ALTER TABLE dbo.ChannelJobState ADD CONSTRAINT CK_ChannelJobState_IsTipi
    CHECK (IsTipi IN ('SIPARIS_CEK', 'IADE_CEK', 'MUTABAKAT', 'STOK_GONDER', 'KATALOG_CEK', 'REFERANS_CEK'));
