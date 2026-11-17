/*
    0007: KatalogCek job'ı (ADR-022).
      - ChannelJobState.IsTipi'ne 'KATALOG_CEK' eklenir.
      - ChannelJobState.SonSonuc: son çalışmanın özet raporu (JSON; kişisel veri yok). Panel "Kanaldan Ürün Çek" durumunu buradan okur.
*/

ALTER TABLE dbo.ChannelJobState DROP CONSTRAINT CK_ChannelJobState_IsTipi;
ALTER TABLE dbo.ChannelJobState ADD CONSTRAINT CK_ChannelJobState_IsTipi
    CHECK (IsTipi IN ('SIPARIS_CEK', 'IADE_CEK', 'MUTABAKAT', 'STOK_GONDER', 'KATALOG_CEK'));

ALTER TABLE dbo.ChannelJobState ADD SonSonuc NVARCHAR(MAX) COLLATE Turkish_CI_AS NULL;
GO

ALTER TABLE dbo.ChannelJobState ADD CONSTRAINT CK_ChannelJobState_SonSonuc CHECK (SonSonuc IS NULL OR ISJSON(SonSonuc) = 1);
