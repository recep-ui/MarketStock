/*
    0017: İlan başına stok kuralı ve kanal ürün görseli (ADR-042).
      - ChannelListing.StokKuralTip / StokKuralDeger: platform bazlı farklı stok. NULL = kural yok (kanal payı ile hesaplanan gider).
        UST_LIMIT: MIN(hesaplanan, Deger). SABIT: MIN(Deger, satılabilir, kanal üst limiti). Sonuç defteri asla aşmaz
        (StockAllocator, saf fonksiyon). Tip ve Deger birlikte dolu ya da birlikte NULL.
      - StokKuralDegistiren / StokKuralTarih: kuralı son değiştiren kullanıcı ve zaman (UTC).
      - UnmatchedChannelItem.GorselUrl: eşleşmeyen kanal ürününün küçük resmi (yalnızca https; kanalın kendi API'sinden).
        Eşleşen ilanlarda görsel ChannelListing.KanalVerisi JSON'unda ("gorsel") tutulur; şema değişikliği gerekmez.
*/

ALTER TABLE dbo.ChannelListing ADD
    StokKuralTip        VARCHAR(10)    COLLATE Latin1_General_100_BIN2 NULL,
    StokKuralDeger      INT            NULL,
    StokKuralDegistiren NVARCHAR(100)  COLLATE Turkish_CI_AS NULL,
    StokKuralTarih      DATETIME2(3)   NULL;
GO

ALTER TABLE dbo.ChannelListing ADD
    CONSTRAINT CK_ChannelListing_StokKuralTip CHECK (StokKuralTip IS NULL OR StokKuralTip IN ('UST_LIMIT', 'SABIT')),
    CONSTRAINT CK_ChannelListing_StokKuralDeger CHECK (StokKuralDeger IS NULL OR StokKuralDeger >= 0),
    CONSTRAINT CK_ChannelListing_StokKural_Birlikte CHECK ((StokKuralTip IS NULL AND StokKuralDeger IS NULL)
                                                       OR (StokKuralTip IS NOT NULL AND StokKuralDeger IS NOT NULL));

ALTER TABLE dbo.UnmatchedChannelItem ADD
    GorselUrl NVARCHAR(1000) COLLATE Latin1_General_100_BIN2 NULL;
GO

ALTER TABLE dbo.UnmatchedChannelItem ADD
    CONSTRAINT CK_UnmatchedChannelItem_GorselUrl CHECK (GorselUrl IS NULL OR GorselUrl LIKE 'https://%');
