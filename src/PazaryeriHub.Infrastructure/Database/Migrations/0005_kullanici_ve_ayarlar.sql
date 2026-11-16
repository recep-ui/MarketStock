/*
    0005: Panel kullanıcıları ve uygulama ayarları (Aşama 5). Spesifikasyonun tablo listesine EK (docs/decisions.md ADR-014).
      - AppUser   : panel girişi ve rol (Yonetici | Depo | Muhasebe | Destek). Parola yalnızca hash olarak tutulur.
                    5 hatalı girişten sonra hesap geçici kilitlenir.
      - AppSetting: uyarı eşikleri vb. basit anahtar/değer ayarları.
*/

CREATE TABLE dbo.AppUser
(
    Id              INT             IDENTITY(1,1) NOT NULL,
    -- Kullanıcı adı büyük/küçük harf duyarsız; Türkçe i/İ sorununa karşı Latin1 (Turkish değil).
    KullaniciAdi    NVARCHAR(100)   COLLATE Latin1_General_100_CI_AS NOT NULL,
    AdSoyad         NVARCHAR(200)   COLLATE Turkish_CI_AS NOT NULL,
    Rol             VARCHAR(20)     COLLATE Latin1_General_100_BIN2 NOT NULL,
    SifreHash       NVARCHAR(500)   COLLATE Latin1_General_100_BIN2 NOT NULL,
    Aktif           BIT             NOT NULL CONSTRAINT DF_AppUser_Aktif DEFAULT (1),
    BasarisizGiris  INT             NOT NULL CONSTRAINT DF_AppUser_BasarisizGiris DEFAULT (0),
    KilitBitis      DATETIME2(3)    NULL,
    SonGiris        DATETIME2(3)    NULL,
    OlusturmaTarihi DATETIME2(3)    NOT NULL CONSTRAINT DF_AppUser_OlusturmaTarihi DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_AppUser PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT UQ_AppUser_KullaniciAdi UNIQUE (KullaniciAdi),
    CONSTRAINT CK_AppUser_Rol CHECK (Rol IN ('Yonetici', 'Depo', 'Muhasebe', 'Destek'))
);

CREATE TABLE dbo.AppSetting
(
    Anahtar     VARCHAR(100)    COLLATE Latin1_General_100_BIN2 NOT NULL,
    Deger       NVARCHAR(1000)  COLLATE Turkish_CI_AS NOT NULL,
    Guncelleme  DATETIME2(3)    NOT NULL CONSTRAINT DF_AppSetting_Guncelleme DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_AppSetting PRIMARY KEY CLUSTERED (Anahtar)
);

INSERT dbo.AppSetting (Anahtar, Deger) VALUES ('KritikStokEsigi', N'3');

-- Stok hareketleri ekranı filtreleri
CREATE INDEX IX_StockMovement_Tarih ON dbo.StockMovement (Tarih DESC) INCLUDE (SkuId, WarehouseId, Tip, Adet, KaynakTip);
