/*
    0008: Aynı SKU'nun aynı kanalda birden fazla ilanı + genel düşük stok eşiği (docs/decisions.md ADR-023).
      - UNIQUE(SkuId, ChannelId) kaldırılır: bir SKU bir kanalda birden fazla ilana bağlanabilir (ör. Trendyol "Çoklanan Ürün Satışı").
        Tam ortak havuz: her ilan aynı satılabilir stoğu görür, stok bölünmez.
      - İlanın kimliği artık kanal içinde barkod / kanal ürün kodudur: (ChannelId, KanalBarkod) ve (ChannelId, KanalUrunKodu)
        NULL hariç filtreli UNIQUE.
      - Ön kontrol: mevcut veride bu iki anahtarda çakışma varsa script hiçbir şeyi değiştirmeden durur (DbUp script başına
        transaction kullanır; THROW tüm script'i geri alır). Veri otomatik silinmez veya birleştirilmez.
      - AppSetting 'DusukStokEsigi' (varsayılan 0 = davranış değişmez): satılabilir <= eşik ise kanallara 0 gönderilir.
*/

IF EXISTS (SELECT 1 FROM dbo.ChannelListing WHERE KanalBarkod IS NOT NULL
           GROUP BY ChannelId, KanalBarkod HAVING COUNT(*) > 1)
    THROW 50080, N'0008 durduruldu: aynı kanalda aynı KanalBarkod birden fazla ilanda var. Kontrol sorgusuyla (ADR-023) çakışmaları giderin.', 1;

IF EXISTS (SELECT 1 FROM dbo.ChannelListing WHERE KanalUrunKodu IS NOT NULL
           GROUP BY ChannelId, KanalUrunKodu HAVING COUNT(*) > 1)
    THROW 50081, N'0008 durduruldu: aynı kanalda aynı KanalUrunKodu birden fazla ilanda var. Kontrol sorgusuyla (ADR-023) çakışmaları giderin.', 1;

ALTER TABLE dbo.ChannelListing DROP CONSTRAINT UQ_ChannelListing_Sku_Channel;
DROP INDEX IX_ChannelListing_Channel_KanalBarkod ON dbo.ChannelListing;
DROP INDEX IX_ChannelListing_Channel_KanalUrunKodu ON dbo.ChannelListing;

CREATE UNIQUE INDEX UX_ChannelListing_Channel_KanalBarkod ON dbo.ChannelListing (ChannelId, KanalBarkod) WHERE KanalBarkod IS NOT NULL;
CREATE UNIQUE INDEX UX_ChannelListing_Channel_KanalUrunKodu ON dbo.ChannelListing (ChannelId, KanalUrunKodu) WHERE KanalUrunKodu IS NOT NULL;
CREATE INDEX IX_ChannelListing_Sku_Channel ON dbo.ChannelListing (SkuId, ChannelId) INCLUDE (Durum);

IF NOT EXISTS (SELECT 1 FROM dbo.AppSetting WHERE Anahtar = 'DusukStokEsigi')
    INSERT dbo.AppSetting (Anahtar, Deger) VALUES ('DusukStokEsigi', N'0');
