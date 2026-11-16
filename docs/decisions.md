# Mimari Karar Kayıtları (ADR)

Her kayıt: bağlam, karar, sonuç. Yeni kararlar sona eklenir; eski kararlar silinmez, gerekirse "yerine geçti" diye işaretlenir.

---

## ADR-001: Solution yapısı (modüler monolit)
**Tarih:** 2026-11-16 (devralma sırasında geriye dönük kayıt)
**Bağlam:** Spesifikasyon modüler monolit ve hub-and-spoke istiyor.
**Karar:** `Domain ← Application ← (Infrastructure | Adapters.*) ← Api`. Adaptörler yalnızca `Application` (sözleşmeler) katmanına bağımlı. Kurallar `ProjectReferenceRulesTests` ile testle korunur. Testler `Tests.Unit` ve `Tests.Integration` olarak ikiye ayrıldı.
**Sonuç:** Çekirdek kanalları tanımaz; yeni kanal = yeni adaptör projesi.

## ADR-002: .NET 10 (spesifikasyondaki .NET 8 yerine)
**Tarih:** 2026-11-16
**Bağlam:** Spesifikasyon .NET 8 diyor; önceki ajan gerekçesiz `net10.0` kullanmıştı. .NET 8 LTS desteği Kasım 2026'da bitiyor; .NET 10 güncel LTS sürüm.
**Karar:** Ürün sahibinin onayıyla **.NET 10** kullanılır (`global.json` 10.0.100, `rollForward: latestFeature`).
**Sonuç:** Paket sürümleri .NET 10 uyumlu seçilir (Serilog.AspNetCore 10.x, Mvc.Testing 10.x).

## ADR-003: SQL Server sürümü
**Tarih:** 2026-11-16
**Bağlam:** Spesifikasyon 2019/2022 diyor. Hedef sunucu (iç ağ; adres user-secrets'ta) **SQL Server 2025 (17.0) Express**, sunucu collation'ı `Turkish_CI_AS`.
**Karar:** Geliştirme konteyneri hedef sunucuyla aynı ana sürümde tutulur: `mssql/server:2025-latest`. T-SQL, 2019 ile uyumlu yazılır (2022/2025'e özgü sözdizimi kullanılmaz); böylece 2019/2022'ye taşınabilirlik korunur.
**Sonuç / riskler:** Express sürümünde veritabanı başına **10 GB** sınırı ve SQL Agent yok (zamanlama Hangfire'da olduğu için sorun değil). `StockMovement`, `Outbox` ve Hangfire tabloları için arşivleme/temizlik politikası Aşama 4'te gerekecek. İleride imaj belirli bir CU etiketine sabitlenmeli.

## ADR-004: Migration aracı: DbUp (sürümlü SQL script'leri)
**Tarih:** 2026-11-16
**Bağlam:** Şemanın ağırlığı saklı yordamlar, filtreli indeksler, kolon düzeyi collation, `PERSISTED` hesaplanmış kolonlar ve `CHECK (ISJSON(...))` kısıtları üzerinde. EF migrations bunları büyük ölçüde ham SQL ile ifade etmeyi gerektirir. Ayrıca stok yazımları Dapper + saklı yordamlarla yapılacak.
**Karar:** **DbUp** + `db/migrations/NNNN_*.sql` sürümlü T-SQL script'leri. EF Core yalnızca genel CRUD için okuma/yazma modeli olarak kullanılır, şema üretmez.
**Sonuç:** Veritabanı oluşturma (CREATE DATABASE + RCSI/snapshot) yüksek yetki gerektirdiği için ayrı tutulur: `db/provision/001_create_database.sql` (sysadmin tarafından bir kez). Uygulama kullanıcısı yalnızca kendi veritabanında `db_owner` olur.

## ADR-005: FluentAssertions 7.x'te sabitleme
**Tarih:** 2026-11-16 (geriye dönük kayıt)
**Bağlam:** FluentAssertions 8.x ve sonrası ticari lisans gerektiriyor; 7.x Apache-2.0.
**Karar:** `FluentAssertions 7.2.2` sabitlenir; 8.x'e yükseltilmez.
**Sonuç:** Lisans sorunu yok. İleride gerekirse Shouldly/AwesomeAssertions değerlendirilebilir.

## ADR-006: Geçişli bağımlılık sabitleme (Newtonsoft.Json)
**Tarih:** 2026-11-16
**Bağlam:** `Hangfire.Core` → `Newtonsoft.Json >= 11.0.1` (GHSA-5crp-9r3c-p9vr, yüksek önem). `TreatWarningsAsErrors` nedeniyle NU1903 derlemeyi kırıyordu.
**Karar:** Uyarıyı bastırmak yerine `CentralPackageTransitivePinningEnabled` açılır ve `Newtonsoft.Json 13.0.4` merkezi olarak sabitlenir.
**Sonuç:** Derleme temiz; güvenlik uyarısı kapandı.

## ADR-007: Gizli bilgi yönetimi (geliştirme)
**Tarih:** 2026-11-16
**Bağlam:** Repo herkese açık (public) GitHub deposunda.
**Karar:** Bağlantı dizesi yalnızca `dotnet user-secrets` (yerel) veya ortam değişkeni (konteyner) ile verilir; `appsettings*.json` içinde boş durur. `.env` ve `*.xlsx` (iş verisi) `.gitignore`'da. Testlerde gerçek kimlik bilgisi kullanılmaz.
**Sonuç:** Kanal API anahtarları Aşama 2+ için `KimlikSifreli` + `IDataProtector` ile saklanacak (spesifikasyon §9).
