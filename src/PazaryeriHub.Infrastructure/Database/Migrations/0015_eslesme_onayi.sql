/*
    0015: Eşleşme onayı (ADR-036). Spesifikasyonun tablo listesine EK.
      - ListingMatchApproval: kanal stok listesindeki "Kontrol et" uyarısının Yönetici tarafından "doğru eşleşme" diye onaylanması.
        Onay, onaylandığı andaki eşleşme bilgisinin imzasına (Imza, SHA-256) bağlıdır: SKU, iki taraftaki barkod/kod/ad/renk/beden
        veya uyarılar değişirse onay geçersiz sayılır (uygulama katmanında karşılaştırılır).
      - Kayıt silinmez: geri alma veya yenileme GeriAlan/GeriAlmaTarihi/KapanisNedeni ile kapatılır (geçmiş korunur).
        İlan başına en fazla bir açık onay (filtreli unique index). İlan silinirse onay geçmişi de silinir (ON DELETE CASCADE).
      - Eşleştirmeyi (ChannelListing.SkuId) değiştirmez.
*/

CREATE TABLE dbo.ListingMatchApproval
(
    Id              BIGINT IDENTITY(1,1) NOT NULL,
    ListingId       INT             NOT NULL,
    SkuId           INT             NOT NULL,
    Imza            CHAR(64)        COLLATE Latin1_General_100_BIN2 NOT NULL,
    Uyarilar        VARCHAR(200)    COLLATE Latin1_General_100_BIN2 NOT NULL,
    Onaylayan       NVARCHAR(100)   COLLATE Turkish_CI_AS NULL,
    OnayTarihi      DATETIME2(3)    NOT NULL CONSTRAINT DF_ListingMatchApproval_OnayTarihi DEFAULT (SYSUTCDATETIME()),
    GeriAlan        NVARCHAR(100)   COLLATE Turkish_CI_AS NULL,
    GeriAlmaTarihi  DATETIME2(3)    NULL,
    KapanisNedeni   VARCHAR(20)     COLLATE Latin1_General_100_BIN2 NULL,
    CONSTRAINT PK_ListingMatchApproval PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_ListingMatchApproval_Listing FOREIGN KEY (ListingId) REFERENCES dbo.ChannelListing (Id) ON DELETE CASCADE,
    CONSTRAINT CK_ListingMatchApproval_Kapanis CHECK (
        (GeriAlmaTarihi IS NULL AND KapanisNedeni IS NULL) OR (GeriAlmaTarihi IS NOT NULL AND KapanisNedeni IN ('GERI_ALINDI', 'YENILENDI')))
);
CREATE UNIQUE INDEX UX_ListingMatchApproval_Acik ON dbo.ListingMatchApproval (ListingId) WHERE GeriAlmaTarihi IS NULL;
