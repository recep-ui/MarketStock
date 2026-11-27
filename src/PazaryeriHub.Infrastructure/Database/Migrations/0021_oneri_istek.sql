/*
    0021 — Öneri ve istekler (ADR-064). Panel kullanıcılarının uygulamayla ilgili öneri/istek notları.
      - Yazan, oturumdaki kullanıcıdan alınır (istemci gönderemez); kullanıcı adı ve o anki ad soyad metin olarak saklanır
        (FK yok: kullanıcı silinse de not kimin yazdığıyla kalır, AuditLog ile aynı yaklaşım).
      - Durum: YENI → TAMAMLANDI / REDDEDILDI (yalnızca Yönetici değiştirir); geri YENI'ye alınabilir.
*/
CREATE TABLE dbo.Oneri
(
    Id               INT             IDENTITY(1,1) NOT NULL,
    Metin            NVARCHAR(2000)  COLLATE Turkish_CI_AS NOT NULL,
    KullaniciAdi     NVARCHAR(100)   COLLATE Latin1_General_100_CI_AS NOT NULL,
    AdSoyad          NVARCHAR(200)   COLLATE Turkish_CI_AS NOT NULL,
    Durum            VARCHAR(20)     COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT DF_Oneri_Durum DEFAULT ('YENI'),
    Olusma           DATETIME2(3)    NOT NULL CONSTRAINT DF_Oneri_Olusma DEFAULT (SYSUTCDATETIME()),
    DurumDegistiren  NVARCHAR(100)   COLLATE Latin1_General_100_CI_AS NULL,
    DurumTarihi      DATETIME2(3)    NULL,
    CONSTRAINT PK_Oneri PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT CK_Oneri_Durum CHECK (Durum IN ('YENI', 'TAMAMLANDI', 'REDDEDILDI')),
    CONSTRAINT CK_Oneri_Metin CHECK (LEN(Metin) > 0)
);
GO

CREATE INDEX IX_Oneri_Durum_Olusma ON dbo.Oneri (Durum, Olusma DESC);
