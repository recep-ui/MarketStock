/*
    0002: Başlangıç verisi.
    - Varsayılan depo ("Ana Depo"). Çoklu depo kararı netleşince yenileri eklenir; şema zaten çoklu depoyu destekler.
    - Dört kanal kaydı. Kimlik bilgileri girilip adaptörler doğrulanana kadar Aktif = 0.
      PayYuzde = 100: "tam ortak havuz" modu (CLAUDE.md §5).
*/
IF NOT EXISTS (SELECT 1 FROM dbo.Warehouse WHERE Ad = N'Ana Depo')
    INSERT dbo.Warehouse (Ad) VALUES (N'Ana Depo');

MERGE dbo.Channel AS hedef
USING (VALUES
    ('TRENDYOL',    N'Trendyol'),
    ('HEPSIBURADA', N'Hepsiburada'),
    ('BEYMEN',      N'Beymen'),
    ('SOFTCOTTON',  N'softcotton.com.tr')
) AS kaynak (Kod, Ad)
ON hedef.Kod = kaynak.Kod
WHEN NOT MATCHED THEN
    INSERT (Kod, Ad, PayYuzde, Aktif) VALUES (kaynak.Kod, kaynak.Ad, 100, 0);
