/*
    PazaryeriHub veritabanı ilk kurulum script'i.
    Bir sysadmin (veya dbcreator) tarafından, uygulama ilk kez çalışmadan önce BİR KEZ çalıştırılır.
    Uygulama kullanıcısı (ör. 'ms') CREATE DATABASE yetkisine sahip değilse bu adım zorunludur.

    Kullanım (SSMS):
        1) Query menüsü > "SQLCMD Mode" AÇIK olmalı (kapalıysa ':setvar' satırları hata verir).
        2) Sysadmin yetkili bir kullanıcıyla, uygulamanın bağlanacağı SQL Server instance'ına bağlanın.
        3) Aşağıdaki iki ':setvar' değerini kontrol edip çalıştırın (F5). Sonda sonuç tablosu görünmeli.
        4) Entegrasyon testleri için aynı script'i DbName = "PazaryeriHub_Test" ile BİR KEZ DAHA çalıştırın.
    Not: sqlcmd'de ':setvar' komut satırındaki '-v' değerini ezer; değerleri burada değiştirin.

    Tablolar burada DEĞİL; uygulamanın sürümlü migration script'leriyle (DbUp, Aşama 1) kurulur.
*/
:setvar DbName "PazaryeriHub"
:setvar AppLogin "ms"

USE master;
GO

IF DB_ID(N'$(DbName)') IS NULL
BEGIN
    CREATE DATABASE [$(DbName)] COLLATE Turkish_CI_AS;
END
GO

-- Spesifikasyon §4: zorunlu izolasyon ayarları
ALTER DATABASE [$(DbName)] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
ALTER DATABASE [$(DbName)] SET ALLOW_SNAPSHOT_ISOLATION ON;
GO

USE [$(DbName)];
GO

-- Uygulama login'ini veritabanına kullanıcı olarak ekle.
-- db_owner: migration'lar (tablo/yordam oluşturma) ve Hangfire şeması kurulumu için gerekli.
-- Üretimde ayrı bir "migration" kullanıcısı + daha dar yetkili bir "uygulama" kullanıcısı düşünülebilir.
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(AppLogin)')
BEGIN
    CREATE USER [$(AppLogin)] FOR LOGIN [$(AppLogin)];
END
ALTER ROLE db_owner ADD MEMBER [$(AppLogin)];
GO

SELECT name, collation_name, is_read_committed_snapshot_on, snapshot_isolation_state_desc
FROM sys.databases WHERE name = N'$(DbName)';
GO
