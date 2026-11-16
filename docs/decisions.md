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

## ADR-008: Docker yerine yerel çalıştırma ve yerel SQL Server ile test
**Tarih:** 2026-11-16
**Bağlam:** Spesifikasyon docker-compose ile çalıştırma (Aşama 0) ve Testcontainers ile entegrasyon testleri (Aşama 1) istiyor. Geliştirme makinesinde Docker yok; hedef SQL Server (2025 Express) aynı makinede çalışıyor.
**Karar (ürün sahibinin onayıyla):**
- Uygulama yerelde `dotnet run` ile çalıştırılır (`.claude/launch.json` → `api`, http://localhost:5080). Bağlantı dizesi user-secrets'ta.
- Entegrasyon testleri yerel SQL Server'daki ayrı **`PazaryeriHub_Test`** veritabanında çalışır (bağlantı: test projesinin user-secrets'ı veya `PAZARYERIHUB_TEST_DB`). Testcontainers bağımlılığı kaldırıldı.
- `docker-compose.yml` ve `Dockerfile` repoda kalır ancak **doğrulanmamıştır**; ileride konteyner dağıtımı gerekirse doğrulanmalıdır.
**Sonuç:**
- Aşama 0 kabulü "`docker compose up` + `/health` 200" yerine "yerel çalıştırma + `/health` 200" olarak değerlendirilir.
- Testler her çalıştırmada tablo sıfırlamaz; her test kendi benzersiz SKU'sunu oluşturur. Test veritabanı zamanla büyür; gerekirse elle temizlenebilir (yalnızca `PazaryeriHub_Test`).
- Güvenlik: test fixture'ı adında "Test" geçmeyen veritabanında çalışmayı reddeder.

## ADR-009: Excel kütüphanesi (ClosedXML) ve içe aktarma kuralları
**Tarih:** 2026-11-16
**Bağlam:** Aşama 2 Excel içe/dışa aktarma istiyor. EPPlus 5+ ticari lisans gerektiriyor; NPOI daha düşük seviyeli.
**Karar:**
- **ClosedXML 0.105.1** (MIT) kullanılır; yalnızca `.xlsx`. Okuma/yazma `Infrastructure/Excel/ExcelWorkbook` içinde, iş kuralları `Application/Excel/ImportValidators` içinde (saf, birim testli).
- Başlıklar Türkçe; eşleştirme büyük/küçük harf, boşluk ve Türkçe karakter duyarsız (`Ürün Adı` = `URUN ADI`). Sütun sırası serbest; bilinmeyen sütunlar (ör. dışa aktarmadaki gri bilgi sütunları) yok sayılır.
- **Kısmi uygulama:** tüm satırlar doğrulanır, yalnızca geçerli satırlar uygulanır; hatalar Excel satır numarasıyla raporlanır. `validateOnly=true` hiçbir şey yazmaz.
- **Dosya içi tekrar** (aynı SKU, aynı barkod, aynı SKU+depo, aynı SKU+kanal): hangisinin doğru olduğu belirsiz olduğundan TÜM tekrar eden satırlar reddedilir.
- **Ürün gruplama:** Product tablosunda model kodu kolonu yok; yeni SKU'lar aynı *Ürün Adı + Marka*'ya sahip ürüne bağlanır (Türkçe, harf duyarsız). Mevcut SKU'nun satırı ürün alanlarını da günceller (son satır kazanır).
- **Stok dosyası:** `GIRIS` ekler (dosyayı iki kez yüklemek iki kez ekler; şablonda uyarı var), `SAYIM` eşitler ve fark yoksa hareket yazmaz (tekrar yüklemek güvenli). Her satır kendi transaction'ında defter yordamıyla yazılır; satırlar arası atomiklik yoktur.
- Sınırlar: 10 MB, 20.000 veri satırı.

## ADR-010: EF Core kullanım sınırı
**Tarih:** 2026-11-16
**Karar:** EF Core 10 yalnızca katalog/kanal/eşleştirme CRUD'u için. Şema DbUp'ta kalır (EF migration yok). `StockBalance` EF'te salt okunur; stok yazımları yalnızca saklı yordamlarla (`IStockLedger`). Kısıt ihlalleri (2601/2627/547) kullanıcıya sade 409/400 mesajı olarak döner, SQL metni sızmaz.

## ADR-011: Güvenlik stoğu değişikliği
**Tarih:** 2026-11-16
**Karar:** `usp_GuvenlikStoguAyarla` fiziksel/rezerve değiştirmediği için `StockMovement` yazmaz (hareket tipleri spesifikasyonda sabit); satılabilir değiştiği için aynı transaction'da outbox'a `StokDegisti` (Tip=`GUVENLIK_STOGU`) yazar.

## ADR-012: Sipariş çekirdeği: idempotentlik, stok uzlaştırma ve job durumu
**Tarih:** 2026-11-16
**Bağlam:** Aynı sipariş örtüşmeli pencere, yeniden deneme veya paralel çalışma nedeniyle defalarca gelebilir; stok iki kez etkilenmemeli (ilke 4). Sipariş kaydı ile stok yordamları ayrı transaction'larda çalışır (yordamlar kendi transaction'ını yönetir ve XACT_ABORT ile dış transaction'ı da geri alır).
**Karar:**
- Sipariş: `UNIQUE(ChannelId, KanalSiparisNo)`; eşzamanlı ikinci kayıt 2601/2627 alır ve mevcut kayıtla devam eder.
- Satır stok hareketleri `KaynakTip='SIPARIS_SATIRI'`, `KaynakId='OL:{OrderLine.Id}'`; filtreli `UNIQUE(KaynakId, Tip)` ile her satır için REZERVE / REZERVE_IPTAL / SEVK en fazla birer kez. İkinci deneme yordamın transaction'ını geri aldırır (2601) ve "zaten yapılmış" sayılır.
- **Hedef duruma uzlaştırma:** her işlemede siparişin durumuna göre (Yeni/Onaylandı/Hazırlanıyor → rezerve; Kargoda/TeslimEdildi/İade → sevk; İptal → rezerv iadesi) eksik stok adımı yapılır. Yarıda kalan işlem bir sonraki işlemede tamamlanır.
- **Oversell:** rezerve edilemeyen satır siparişi reddetmez; `StokYetersiz=1`. Sonraki işlemede stok varsa rezerve edilir ve bayrak kalkar.
- **Durum geçişi:** panelde yalnızca tek adım; kanaldan gelen ileri atlamada (ör. Yeni → Kargoda) ara adımlar da OrderEvent'e yazılır. Geri dönüş ve son durumdan (İptal/İade) çıkış her iki kaynakta reddedilir. Sipariş satırı `UPDLOCK` ile kilitlenir.
- **Eşleştirme sırası:** kanal barkodu → kanal ürün kodu (ChannelListing) → SKU barkodu → satıcı SKU kodu. Eşleşmeyen satır `SkuId=NULL` kaydedilir, stoğu etkilemez.
- **Depo:** çoklu depo stratejisi netleşene kadar rezervasyonlar `Siparis:DefaultWarehouseName` ("Ana Depo") deposundan.
- **SiparisCek:** kanal başına tekrarlayan job (`*/2 * * * *`), pencere başı = son başarılı çalışmanın başlangıcı − 10 dk (ilk çalışmada 24 saat geriye). `DisableConcurrentExecution`; otomatik yeniden deneme yok (bir sonraki tetikleme yeniden dener). Beklenmeyen hatada pencere ilerletilmez. Durum yeni `ChannelJobState` tablosunda (spesifikasyon listesine **ek**).
- Siparişlerde EF yerine Dapper: koşullu güncelleme, kilit ipuçları ve transaction sınırları açıkça yazılabilsin diye (ADR-010'un istisnası).
- Kanal adaptörleri: Aşama 6'ya kadar tüm kanallar bellek içi `MockChannelAdapter` (`Adapters.Mock`). Gerçek pazaryeri API'si çağrılmaz.
**Kapsam dışı / sonraya:** sipariş durumunun kanala bildirilmesi (`UpdateOrderStatusAsync`, Hangfire job'ı olarak) ve `IadeCek`.
