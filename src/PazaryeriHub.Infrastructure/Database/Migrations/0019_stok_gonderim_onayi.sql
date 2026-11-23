/*
    0019 — Stok gönderiminde kanal bazında onay (ADR-048).

    - STOK yeteneği DENEME ("Onaylı"): hiçbir stok kaydı kendiliğinden kanala gitmez; Yönetici Senkron Merkezi'nde ürün/kanal
      bazında görüp onaylar. Onaylanan değer saklanır; gönderim anında değer değiştiyse kayıt yeniden onaya düşer.
    - Kullanıcı isteği: stok değişiklikleri kanallara onaysız gitmesin. Bu yüzden tüm kanallarda STOK = ACIK olanlar DENEME yapılır
      (Ayarlar → Kanallar'dan kanal bazında yeniden Açık yapılabilir).
    - Reddedilen kayıt IPTAL durumuna alınır (gönderilmez; stok yeniden değişince yeni kayıt açılır).
*/
ALTER TABLE dbo.SyncQueue ADD
    OnaylananDeger      INT            NULL,
    OnaylayanKullanici  NVARCHAR(100)  COLLATE Turkish_CI_AS NULL,
    OnayTarihi          DATETIME2(3)   NULL;
GO

ALTER TABLE dbo.SyncQueue DROP CONSTRAINT CK_SyncQueue_Durum;
ALTER TABLE dbo.SyncQueue ADD CONSTRAINT CK_SyncQueue_Durum CHECK (Durum IN ('BEKLIYOR', 'GONDERILDI', 'HATA', 'IPTAL'));
ALTER TABLE dbo.SyncQueue ADD CONSTRAINT CK_SyncQueue_OnaylananDeger CHECK (OnaylananDeger IS NULL OR OnaylananDeger >= 0);
GO

UPDATE dbo.ChannelCapability
   SET Durum = 'DENEME', Degistiren = N'migration 0019 (stok onayı)', DegisimTarihi = SYSUTCDATETIME()
 WHERE Yetenek = 'STOK' AND Durum = 'ACIK';
GO
