/*
    0004: Senkron hattı (Aşama 4). Spesifikasyondaki SyncQueue kolonlarına EK (docs/decisions.md ADR-013):
      - SonrakiDeneme  : üstel geri çekilme; bu zamandan önce yeniden gönderilmez.
      - SonDeneme      : son gönderim denemesinin zamanı (takılı kalan talepleri kurtarmak için).
      - GonderilenDeger: bu kayıtla kanala gönderilen stok değeri (onaylanınca ChannelListing.SonGonderilenStok olur).
    Değer kuyruğa yazılırken DEĞİL gönderim anında hesaplanır: debounce ile biriken değişikliklerin en güncel hali gider.
*/

ALTER TABLE dbo.SyncQueue ADD
    SonrakiDeneme   DATETIME2(3) NULL,
    SonDeneme       DATETIME2(3) NULL,
    GonderilenDeger INT          NULL;
GO

-- StokGonder: kanal başına gönderilecek bekleyen kayıtlar
CREATE INDEX IX_SyncQueue_Gonderilecek
    ON dbo.SyncQueue (Tip, SonrakiDeneme)
    INCLUDE (ListingId, BatchId, Deneme)
    WHERE Durum = 'BEKLIYOR';

-- BatchSorgula: sonucu beklenen toplu işlemler
CREATE INDEX IX_SyncQueue_Batch
    ON dbo.SyncQueue (BatchId)
    INCLUDE (ListingId, GonderilenDeger, Deneme)
    WHERE Durum = 'BEKLIYOR' AND BatchId IS NOT NULL;

CREATE INDEX IX_ChannelListing_SkuId ON dbo.ChannelListing (SkuId) INCLUDE (ChannelId, Durum, SonGonderilenStok);
