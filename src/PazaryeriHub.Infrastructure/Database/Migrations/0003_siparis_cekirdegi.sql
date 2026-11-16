/*
    0003: Sipariş çekirdeği (Aşama 3).

    1) Sipariş satırı stok idempotentliği:
       Sipariş satırlarının stok hareketleri KaynakTip = 'SIPARIS_SATIRI', KaynakId = 'OL:{OrderLine.Id}' ile yazılır.
       Bir satır için her hareket tipinden (REZERVE, REZERVE_IPTAL, SEVK) EN FAZLA BİR kayıt olabilir.
       Aynı sipariş paralel/tekrar işlense bile ikinci rezervasyon bu indekse takılır (2601) ve
       yordamın transaction'ı geri alınır: stok iki kez etkilenmez (ilke 4).

    2) Kanal job durumu: SiparisCek vb. job'ların son başarılı zamanı (örtüşmeli pencerenin başlangıcı),
       son hata ve ardışık hata sayısı. Spesifikasyonun tablo listesine EK (docs/decisions.md ADR-012).
*/

CREATE UNIQUE INDEX UX_StockMovement_SiparisSatiri
    ON dbo.StockMovement (KaynakId, Tip)
    WHERE KaynakTip = 'SIPARIS_SATIRI';

CREATE TABLE dbo.ChannelJobState
(
    ChannelId   INT             NOT NULL,
    IsTipi      VARCHAR(30)     COLLATE Latin1_General_100_BIN2 NOT NULL,
    SonBasarili DATETIME2(3)    NULL,
    SonDeneme   DATETIME2(3)    NULL,
    SonHata     NVARCHAR(1000)  COLLATE Turkish_CI_AS NULL,
    ArdisikHata INT             NOT NULL CONSTRAINT DF_ChannelJobState_ArdisikHata DEFAULT (0),
    CONSTRAINT PK_ChannelJobState PRIMARY KEY CLUSTERED (ChannelId, IsTipi),
    CONSTRAINT FK_ChannelJobState_Channel FOREIGN KEY (ChannelId) REFERENCES dbo.Channel (Id),
    CONSTRAINT CK_ChannelJobState_IsTipi CHECK (IsTipi IN ('SIPARIS_CEK', 'IADE_CEK', 'MUTABAKAT', 'STOK_GONDER'))
);

-- Sipariş listesi filtreleri
CREATE INDEX IX_Order_Channel_Tarih ON dbo.[Order] (ChannelId, SiparisTarihi DESC) INCLUDE (Durum, Toplam, StokYetersiz);
CREATE INDEX IX_Order_Durum_Tarih ON dbo.[Order] (Durum, SiparisTarihi DESC);
