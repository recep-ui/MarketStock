/*
    0016: Kanalda bulunamayan ilan (ADR-040).
      - ChannelStockSnapshot.KanalStok NULL olabilir. NULL = mutabakat bu ilanı o çekimde kanalda BULAMADI
        (kara liste, arşiv, ilan kaldırılmış vb.). Mutabakat artık bulunamayan her ilan için de bir satır yazar;
        böylece "en son snapshot" eski (ör. iki gün önceki) kanal stoğunu güncel gibi göstermez.
      - Snapshot hiç yoksa ilan "hiç çekilmedi"dir; son snapshot NULL ise "son çekimde kanalda bulunamadı"dır.
      - Sapma hesabı yalnızca değeri olan (NOT NULL) son snapshot ile yapılır. Eski satırlar değişmez (yalnızca eklenir).
*/

ALTER TABLE dbo.ChannelStockSnapshot ALTER COLUMN KanalStok INT NULL;
