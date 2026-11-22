/*
    0018: İşlem geçmişi (ADR-045). Kim, ne zaman, hangi yazma işlemini yaptı; girişler ve başarısız giriş denemeleri.
      - Yalnızca metot, yol (sorgu dizesi olmadan), HTTP sonuç kodu, IP ve süre yazılır. İstek gövdesi ve parola YAZILMAZ.
      - Kullanici metin olarak tutulur (FK yok): kullanıcı silinse de geçmiş kalır.
      - Yalnızca Yönetici görür (/api/audit).
*/

CREATE TABLE dbo.AuditLog
(
    Id          BIGINT          IDENTITY(1,1) NOT NULL,
    Tarih       DATETIME2(3)    NOT NULL CONSTRAINT DF_AuditLog_Tarih DEFAULT (SYSUTCDATETIME()),
    Kullanici   NVARCHAR(100)   COLLATE Latin1_General_100_CI_AS NULL,
    Rol         VARCHAR(20)     COLLATE Latin1_General_100_BIN2 NULL,
    Tip         VARCHAR(20)     COLLATE Latin1_General_100_BIN2 NOT NULL,
    Metot       VARCHAR(10)     COLLATE Latin1_General_100_BIN2 NULL,
    Yol         NVARCHAR(400)   COLLATE Latin1_General_100_CI_AS NULL,
    Durum       INT             NULL,
    IpAdresi    VARCHAR(45)     COLLATE Latin1_General_100_BIN2 NULL,
    SureMs      INT             NULL,
    CONSTRAINT PK_AuditLog PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT CK_AuditLog_Tip CHECK (Tip IN ('ISTEK', 'GIRIS', 'GIRIS_HATALI', 'CIKIS'))
);
GO

CREATE INDEX IX_AuditLog_Tarih ON dbo.AuditLog (Tarih DESC);
CREATE INDEX IX_AuditLog_Kullanici_Tarih ON dbo.AuditLog (Kullanici, Tarih DESC);
GO
