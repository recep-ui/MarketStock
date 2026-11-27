/*
    0020 — Eşleşmeyen kanal ürününde kanalın bildirdiği fiyat, ürün linki ve varyant (ADR-063).

    - Önceden eşleşmeyen kayıt bunları saklamıyordu; panelden veya depo geçişinde bağlanan ilan KanalVerisi'nde fiyatsız/linksiz
      açılıyor, bir sonraki katalog çekimine kadar "kanalda fiyat yok" görünüyordu (canlı, 2026-11-27: 50 Hepsiburada ilanı).
    - Değerler salt okumadır (kanalın kendi verisi); kanala hiçbir şey göndermez. Bağlanınca ilanın KanalVerisi'ne aynen yazılır.
*/
ALTER TABLE dbo.UnmatchedChannelItem ADD
    KanalSatisFiyati  DECIMAL(18,2)   NULL,
    KanalListeFiyati  DECIMAL(18,2)   NULL,
    UrunLinki         NVARCHAR(1000)  COLLATE Latin1_General_100_BIN2 NULL,
    Varyant           NVARCHAR(300)   COLLATE Turkish_CI_AS NULL;
GO

ALTER TABLE dbo.UnmatchedChannelItem ADD
    CONSTRAINT CK_UnmatchedChannelItem_KanalFiyat CHECK ((KanalSatisFiyati IS NULL OR KanalSatisFiyati > 0)
                                                      AND (KanalListeFiyati IS NULL OR KanalListeFiyati > 0));
