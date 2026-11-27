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

## ADR-013: Senkron hattı (Outbox → SyncQueue → kanal)
**Tarih:** 2026-11-16
**Karar:**
- **OutboxIsle (10 sn):** `TOP(100) … WITH (ROWLOCK, UPDLOCK, READPAST)` + `UPDATE … OUTPUT` ile olay alınır; alma + kuyruğa yazma tek transaction (hata → olaylar işlenmemiş kalır). Bir çalışmada en fazla 50 parti. Olay tipleri: `StokDegisti` (SkuId), `EslestirmeDegisti` (ListingId; eşleştirme API'si ve Excel yazar), `KanalStokYenile` (ChannelId; kanal payı/aktiflik/ayar değişince ya da `POST /api/sync/channels/{id}/refresh`).
- **Debounce:** `UX_SyncQueue_Listing_Tip_Bekleyen` + `NOT EXISTS … WITH (UPDLOCK, HOLDLOCK)`. Kuyrukta **değer tutulmaz**; değer gönderim anında en güncel stoktan hesaplanır.
- **Gönderimdeki kayıt** `BEKLIYOR` kalır (BatchId dolu); bu sırada gelen değişiklik yeni kayıt açamaz. Batch onaylanınca hedef yeniden hesaplanır, farklıysa yeni kayıt açılır (değişiklik kaybolmaz). Spesifikasyondaki 3 durum korunmuştur (ek durum eklenmedi).
- **Talep:** StokGonder kayıtları `BatchId = 'claim:{guid}'` ile talep eder (READPAST); süreç çökerse BatchSorgula 10 dk sonra serbest bırakır.
- **Yeniden deneme:** deneme sayacı talep anında artar. Başarısızlıkta üstel bekleme 30 sn × 2^(n−1) (en fazla 30 dk); **5. başarısızlıkta HATA** + `UYARI` logu; Senkron özeti/listesinde görünür; `POST /api/sync/queue/{id}/retry` ile sıfırlanır. Kanal kodu/barkodu olmayan eşleştirme denemeden HATA olur.
- **Kısmi batch hatası:** yalnızca hatalı kalemler yeniden denenir.
- **Hedef stok:** `StockAllocator(Σ Satilabilir, Channel.PayYuzde, Channel.Ayarlar.stokUstLimit)`; yalnızca AKTIF eşleştirme + aktif kanal.
- **Mutabakat:** kanal başına, gece 00:00 UTC (03:00 TR) + `POST /api/sync/reconciliation/{kanal}/run` (Hangfire job'ı olarak kuyruğa alınır; dış çağrı doğrudan yapılmaz). Sonuç `ChannelStockSnapshot`; rapor `GET /api/sync/reconciliation?onlyDeviations=true` (beklenen ≠ kanal veya kanalda bulunamadı).
- **Şema ekleri (0004):** `SyncQueue.SonrakiDeneme`, `SonDeneme`, `GonderilenDeger` (spesifikasyon listesine EK).
- **Kuyruk izolasyonu:** `Hangfire:Queues` ayarı; testlerde paylaşılan sunucu boş bir kuyruğu dinler, böylece gerçek job'ları almaz.
- Aşama 0'daki geçici `HeartbeatJob` kaldırıldı (kayıtlı ise başlangıçta silinir).
**Kapsam dışı:** FİYAT senkronu: şemada fiyat kaynağı yok (açık soru). Sipariş durumunun kanala bildirilmesi.

## ADR-014: Panel, kimlik ve roller
**Tarih:** 2026-11-16
**Karar:** React 18 + TypeScript + Vite + TanStack Table (`panel/`); derlenmiş panel API'den servis edilir (`Panel:DistPath`), geliştirmede Vite `/api` proxy. Çerez oturumu (HttpOnly, SameSite=Strict); `AppUser` tablosu (0005), PasswordHasher (PBKDF2), 5 hatalı girişte 5 dk kilit. Roller: Yönetici (tümü), Depo (stok yazma, sipariş durumu, iade girişi), Muhasebe (okuma), Destek (okuma; müşteri adı/ilçe API'de maskeli). Tohum kullanıcı parolaları repoda yok (user-secrets / test çalışmasında rastgele). Playwright kurulu Chrome ile, test veritabanında, demo veriyle (`POST /api/dev/demo-data`, yalnızca Development/Testing).

## ADR-015: Tek depo ve fiyatın kapsam dışı olması
**Tarih:** 2026-11-16 (ürün sahibi kararı)
**Karar:**
- **Tek depo:** Tüm stok "Ana Depo"dadır. Yeni depo ekleme uç noktası kaldırıldı. Spesifikasyondaki `WarehouseId` kolonları ve yordam parametreleri korunur (ileride çoklu depoya geçiş şema değişikliği gerektirmez); sipariş rezervasyonları `Siparis:DefaultWarehouseName` deposundan yapılır.
- **Fiyat:** ~~Fiyatlar pazaryeri satıcı panellerinden girilir. Bu uygulama fiyat tutmaz ve göndermez.~~ **2026-11-18: bu madde ADR-028 ile iptal edildi** (ürün sahibi kararı). Tek depo maddesi geçerli.

## ADR-016: Kanal kimlik bilgileri ve adaptör modu
**Tarih:** 2026-11-16
**Karar:**
- Kanal API bilgileri JSON olarak `Channel.KimlikSifreli`'de ASP.NET Core Data Protection (`IDataProtector`, amaç `PazaryeriHub.ChannelCredentials.v1`) ile şifreli tutulur. Panelden yalnızca yazılır/silinir (`PUT/DELETE /api/channels/{id}/credentials`, Yönetici); hiçbir yanıtta dönmez (`hasCredentials` bayrağı hariç). Anahtar halkası varsayılan olarak kullanıcı profilinde; sunucuda `DataProtection:KeysPath` ile kalıcı ve yedekli bir klasör verilmelidir (anahtar kaybolursa bilgiler yeniden girilir).
- Adaptör modu `Kanallar:{KOD}:Mod` = `Mock` (varsayılan) | `Gercek`. Gerçek adaptörü olmayan kanallar her zaman Mock'tur.
- Gerçek adaptör HTTP hattı: çağrı loglama (kanal, metot, uç nokta, süre, durum; başlık/gövde yok) → hız sınırı (Trendyol: kayan pencere 40/10 sn; doküman 50/10 sn) → yeniden deneme (429/5xx/ağ, 3 deneme, üstel + jitter, Retry-After) → deneme başına 30 sn zaman aşımı. Üstünde kayıt bazlı Hangfire yeniden denemesi (5 → HATA) vardır.
- Ham kanal hataları `ChannelApiException` ile sade Türkçe mesaja çevrilir; Senkron Merkezi bu mesajı gösterir.

## ADR-017: Trendyol adaptörü
**Tarih:** 2026-11-16
**Karar (kaynak: developers.trendyol.com/v2.0, bkz. docs/kanal-api-notlari.md):**
- Eşleştirme anahtarı **barkod** (`KanalBarkod`, yoksa `KanalUrunKodu`). Stok gönderiminde yalnızca `barcode` + `quantity` (fiyat yok, ADR-015); ürün başına 20.000 üst sınırına sabitlenir; istek başına ≤1000 kalem.
- Trendyol'da birim **sipariş paketi**dir; bir sipariş birden çok pakete bölünebilir. Kanal sipariş no = `"{orderNumber}/{paketId}"`. Bölünen (UnPacked) eski paket İptal sayılır; kalemleri yeni paketlerle rezerve edilir.
- Durum eşlemesi: Created/Awaiting → Yeni; Picking/Invoiced → Hazırlanıyor; Shipped/AtCollectionPoint → Kargoda; Delivered → Teslim edildi; Cancelled/UnSupplied/UnPacked → İptal; Returned/UnDelivered → İade. Bilinmeyen durumdaki paket atlanır ve loglanır.
- Doğrulanmamış noktalar kodda `// TODO: doğrulanmalı`: sipariş tarih aralığının hangi tarihe uygulandığı ve üst sınırı; `price` alanının birim fiyat olduğu; batch `requestItem` içinde `barcode` olduğu; `barcodes` sorgu biçimi; UnDelivered/UnSupplied anlamları.
- Kapsam dışı: fiyat gönderimi; Trendyol'a sipariş durumu bildirimi (paket satır kimlikleri saklanınca eklenecek).

## ADR-018: Hepsiburada adaptörü
**Tarih:** 2026-11-16
**Karar (kaynak: developers.hepsiburada.com — portal istemci tarafında çizildiği için uygulama içi tarayıcıyla okundu):**
- Kimlik: Basic Auth + zorunlu User-Agent. Kullanıcı adı/şifrenin tam karşılığı dokümanda net değil → panelden **Merchant ID, kullanıcı adı, şifre/servis anahtarı** ve isteğe bağlı User-Agent ayrı ayrı girilir. Ortam: Test = `*-sit` adresleri, Canlı = `-sit` çıkarılmış adresler.
- Eşleştirme anahtarı **HBSKU** ("Kanal Ürün Kodu"): ödemesi tamamlanmış sipariş yanıtı yalnızca `items.sku` (HBSKU) taşır; stok güncelleme `hepsiburadaSku` kabul eder.
- Stok: `POST listing-external/listings/merchantid/{id}/stock-uploads` `[{hepsiburadaSku, availableStock}]` (≤4000), sonuç `GET …/stock-uploads/id/{id}` (`status: Done`, `errors`).
- Siparişler kalem bazlı ve durumlar ayrı uçlarda: ödeme bekleyen (→ Yeni; doküman stok rezerve edilmesini istiyor), ödemesi tamamlanmış/Open (→ Onaylandı), kargoya verilen (→ Kargoda), teslim edilen (→ Teslim edildi), iptal (→ İptal). Kargo/teslim/iptal listeleri satır taşımadığından sipariş kayıt servisi artık **kayıtlı siparişe satırsız durum güncellemesini** kabul eder; satırsız yeni sipariş reddedilir.
- Durum listeleri yalnızca son 1 ayı döner ve tarih penceresi en fazla 24 saattir → çekme aralığı 24 saatlik parçalara bölünür.
- **Doğrulanmamış (TODO):** kullanıcı adı/şifre karşılığı; begindate/enddate biçimi ve saat dilimi (şimdilik `yyyy-MM-dd HH:mm`, Türkiye saati); POST stok yanıtındaki `id`; sonuç `errors[]` şeması; liste yanıtlarının kök şeması (kök dizi veya `items`/`listings`); `orderDate` saat dilimi; teslim edilemeyen uç yolu; kısmi iptal (şimdilik sipariş bütünüyle İptal); "aynı anda en fazla 5 bekleyen yükleme" sınırı; iade (Talep Entegrasyonu) uçları okunmadı → `FetchReturnsAsync` desteklenmiyor.

## ADR-019: Beymen adaptörü
**Tarih:** 2026-11-16
**Karar (kaynak: https://partner-docs.beymen.com/en):**
- Kimlik: `Basic Base64(ApiKey:ApiPassword)`; Prod `partner-api.beymen.com`, Stage `stage-partner-api.beymen.com`.
- Eşleştirme anahtarı Beymen **productId (GUID)** ("Kanal Ürün Kodu"); barkod "Kanal Barkod". Stok: `POST /batch/products/price-stock` `{items:[{productId, quantity}]}` (≤1000, 0–1.000.000), sonuç `GET /batch/products/price-stock/{processId}`; HTTP 400 "The matching is incomplete." = beklemede.
- Kanal sipariş no = `"{orderNumber}/{shipmentNumber}"` (paket bölünebilir). Durumlar: NewOrder 1 → Yeni, ReadyToShip 2 → Hazırlanıyor, Shipped 4 → Kargoda, DeliveredToCustomer 8 → Teslim edildi, Cancelled 16 → İptal (teslim edilemeyen gönderi `isReturnedShipment` → İade), Divided 32 → İptal (kalemler yeni paketlerde).
- Siparişlerde son güncellenme alanı yok; tarih filtresi sipariş tarihine göre → her çalışmada en az **7 gün** geriye bakılır (durum değişiklikleri için) + iptaller ayrıca iptal tarihine göre çekilir.
- İadeler `GET /claims`; kanal stoğu `GET /products/price-stock/search` (productId ile filtre yok → sayfalanıp süzülür).
- **Doğrulanmamış (TODO):** tarih parametre biçimi (ISO 8601 UTC gönderiliyor); hız limiti (dokümanda yok; 5 istek/sn uygulanıyor); 7 günlük geriye bakışın hacme uygunluğu; sipariş durumu bildirimi (picking/kargo) henüz yok.

## ADR-020: Kanal kataloğu içe alma ve eşleştirme sırası
**Tarih:** 2026-11-17
**Bağlam:** Ana veritabanında katalog boş; ürünler kanallarda zaten listeli. Satıcı panellerinden indirilen ürün dosyaları (Trendyol "Ürünler", Beymen "Ürün Listesi") eşleştirmenin ve ilk kataloğun kaynağı olacak.
**Karar:**
- Kanal dosyası başlık imzasıyla tanınır (Trendyol: Partner ID, Barkod, Tedarikçi Stok Kodu, Model Kodu, Ürün Rengi, Beden; Beymen: Id, Satış Kanalı, Barkod, Model Kodu, Satıcı Renk, Beden). Seçilen kanalla uyuşmayan dosya reddedilir. Yalnızca tanımlı ürün sütunları okunur; Beymen dosyasındaki firma/e-posta/adres sütunları okunmaz.
- Barkod ve kodlar metin olarak okunur ve kırpılır. Bilimsel gösterim veya ondalıklı kod **hatalı satır**dır. Sayı olarak kaydedilmiş tam sayı kodlar kabul edilir ama uyarı verilir. Dosya içinde tekrar eden barkod veya kanal kodu hatalı satırdır.
- *(Aynı SKU'ya birden fazla satır ve "belirsiz/çakışma" anlamları ADR-023 ile güncellendi.)*
- Eşleştirme sırası (saf fonksiyon `ChannelCatalogMatcher`):
  0. Bu kanaldaki mevcut eşleştirme (barkod veya kanal kodu).
  1. Kanal barkodu = `Sku.Barkod`.
  2. Satıcı stok kodu = `SkuKodu`; yalnızca stok kodu dosyada tekilse. Trendyol'da stok kodu çoğu zaman model/renk düzeyindedir.
  3. Model kodu + renk + beden; yalnızca tek aday varsa. Karşılaştırma Türkçe büyük harfle yapılır ve bulanık değildir.

  Aynı SKU'ya birden fazla satır düşerse hiçbiri bağlanmaz.
- Ürün adı benzerliği (trigram Dice ≥ 0,60; marka, renk ve beden uyumlu olmalı) **yalnızca öneri** üretir. Otomatik bağlama yapılmaz; öneri panelde yalnızca SKU kutusunu ön doldurur.
- Mevcut eşleştirme farklı bir barkod veya kod gösteriyorsa **üzerine yazılmaz** ("çakışma"). Kanal durumu yalnızca "kapalı" olduğunda (Trendyol "Ürün arşivlendi", Beymen "Beymen'e Seçilmedi") eşleştirmeyi PASIF yapar; aksi halde elle verilmiş durum korunur.
- Eşleşmeyen, belirsiz ve çakışan satırlar `UnmatchedChannelItem` tablosuna yazılır (`UNIQUE(ChannelId, KanalAnahtar)`, tekrar yüklemede yeni kayıt açılmaz). Panelden "Bağla" veya "Yok say" ile kapatılır; sonraki yüklemede eşleşen kayıt otomatik BAGLANDI olur.
- **Eşleşmeyenlerden ürün/SKU oluşturma** yalnızca açıkça seçilirse yapılır. Ürün, Model Kodu + Marka ile gruplanır. SKU kodu sırasıyla: tekil stok kodu → MODEL-RENK-BEDEN → barkod. Şu satırlardan SKU **açılmaz**:
  - belirsiz, çakışan veya öneri almış satırlar;
  - dosyada aynı model + renk + beden ile birden fazla kez geçen satırlar (Trendyol "Çoklanan Ürün Satışı"; ayrı SKU açmak stoğu böler).
- Varsayılan önizlemedir (`validateOnly=true`); yazmak için açıkça `validateOnly=false` gönderilir.
- **Stoğu hiç girilmemiş SKU kanala gönderilmez** (`SyncStore` Syncable koşulu: SKU'nun bakiye satırı olmalı). Böylece katalogdan oluşturulan SKU'lar pazaryerindeki stoğu 0'a çekmez. Stok veya sayım girildiğinde (sayım ile 0 dahil) normal akışla gönderilir.
**Doğrulanmamış:** Beymen dosyasındaki `Id` ile API `productId`'nin aynı değer olduğu (ilk gerçek stok gönderiminde kontrol edilmeli).

## ADR-021: Product.ModelKodu
**Tarih:** 2026-11-17
**Karar:** `Product` tablosuna `ModelKodu NVARCHAR(100) BIN2 NULL` eklendi (migration 0006). Bu, spesifikasyonun tablo listesine bir **ektir**. Kanalların "Model Kodu" alanı ürün grubunu tanımlar ve eşleştirmenin 3. adımı için gereklidir. Kolon ürün Excel'ine isteğe bağlı "Model Kodu" sütunu olarak eklendi; boş hücre mevcut değeri silmez.

## ADR-022: Kanal API'sinden katalog çekme (KatalogCek)
**Tarih:** 2026-11-17
**Karar:**
- `IChannelAdapter.FetchCatalogAsync(cursor, ct)` → `ChannelCatalogPage(Items, NextCursor)`. Spesifikasyondaki sözleşmeye (§6) bir **ektir**. İmleç adaptöre özeldir ve opaktır.
- Panelden elle tetiklenen `CatalogFetchJob` (Hangfire, ilke 5) sayfaları birleştirir. Güvenlik sınırı: 2.000 sayfa veya 50.000 öğe. Sonuç, Excel içe alma ile **aynı** eşleştirme ve yazma yolundan geçer (`ApplyAsync`, kaynak `API`). Varsayılan önizlemedir. Son durum ve rapor `ChannelJobState` tablosunda (`IsTipi = KATALOG_CEK`, `SonSonuc` JSON; migration 0007) tutulur. Aynı kanal için ikinci istek iş çalışırken 409 döner.
- Kanal başına yalnızca resmi dokümanla doğrulanmış alanlar kullanılır:

  | Kanal | Uç nokta | Alanlar |
  |---|---|---|
  | Trendyol | `GET /product/sellers/{sellerId}/products/approved` (filterApprovedProducts; size ≤ 100, page × size ≤ 10.000, ötesi nextPageToken; 2026-11-17 okundu) | productMainId → model, title, brand.name, category.name; varyant barcode, stockCode, archived (→ PASIF), onSale/locked; renk/beden öznitelikleri |
  | Hepsiburada | listing sorgusu (FetchStock ile aynı uç) | hepsiburadaSku → kanal kodu, merchantSku → stok kodu, isSalable. Barkod/model/renk/beden yok, bu yüzden bu kanaldan SKU oluşturulamaz. |
  | Beymen | `GET /products/price-stock/search` | id → kanal kodu, barcode |
  | softcotton | — | mock |

- **Doğrulanmamış (TODO):**
  - Trendyol renk/beden öznitelik adlarının "Renk"/"Beden" olduğu (kategoriye göre değişebilir).
  - Beymen'in model/renk/beden veren bir listeleme ucu.
- Deneme modunda Hangfire varsayılan kuyruğu dinler; zamanlanmış job'lar cron boş olduğu için kapalıdır. Böylece elle tetiklenen işler çalışır.

## ADR-023: Aynı SKU'nun aynı kanalda birden fazla ilanı ve genel düşük stok eşiği
**Tarih:** 2026-11-17
**Bağlam:** Trendyol'da aynı varyant birden fazla ilanla satılabiliyor ("Çoklanan Ürün Satışı"). `UNIQUE(SkuId, ChannelId)` yüzünden ilanlardan yalnızca biri bağlanabiliyordu. Yok sayılan ilanın stoğu güncellenmiyordu ve siparişi SKU'ya çözülmediği için **rezerve edilmiyordu**; bu bir oversell açığıydı.
**Karar (seçenek a; SkuAlias seçeneği reddedildi):**
- **Stok politikası "tam ortak havuz":** tek StockBalance havuzu. Her kanal ve her ilan aynı satılabilir stoğu görür (PayYuzde = 100). Aynı SKU'nun aynı kanaldaki her ilanına tam stok gönderilir; stok bölünmez. Stok 0 olunca tüm ilanlara 0 gider.
- **Migration 0008:**
  - `UQ_ChannelListing_Sku_Channel` kaldırıldı.
  - İlanın kimliği kanal içinde barkod veya kanal ürün kodu oldu: `UX_ChannelListing_Channel_KanalBarkod` ve `UX_ChannelListing_Channel_KanalUrunKodu` (NULL hariç filtreli UNIQUE).
  - `IX_ChannelListing_Sku_Channel` eklendi.
  - **Ön kontrol:** mevcut veride bu anahtarlarda çakışma varsa script THROW 50080/50081 ile durur. DbUp script başına transaction kullandığı için hiçbir şey değişmez; veri silinmez veya birleştirilmez.
  - "En az biri dolu" CHECK kısıtı **eklenmedi**: test veritabanında eski kodsuz ilanlar var. Bu kural uygulama katmanında doğrulanıyor.
- **Değişmeyen davranış** (zaten ilan bazlıydı): stok hedef hesabı, kuyruk tekilliği (debounce), gönderim, kısmi batch hatası eşlemesi (barkod/kod artık kanal içinde tekil, eşleme belirsiz değil), mutabakat ve sipariş satırı çözümleme.
- **Eşleştirme API'si (`PUT /api/listings`):** isteğe bağlı `id` alır. Verilmezse aynı kanal + barkod/kodlu ilan güncellenir; yoksa **yeni ilan** açılır. Aynı SKU'nun mevcut ilanının üzerine yazılmaz. Barkod/kod başka bir SKU'ya bağlıysa 409 döner.
- **Excel eşleştirme içe aktarma:** satır anahtarı kanal + barkod/kod oldu. Aynı SKU + kanal için birden fazla satır serbest. Barkod başka bir SKU'ya bağlıysa satır hatası verilir; ilan sessizce başka SKU'ya taşınmaz.
- **Katalog içe alma (ADR-020'yi günceller):**
  - "Aynı SKU'ya birden fazla satır düşerse hiçbiri bağlanmaz" kuralı kaldırıldı.
  - Model + renk + beden adımında bizde tek aday SKU varsa, dosyadaki tüm satırlar o SKU'nun ayrı ilanları olur.
  - **BELIRSIZ:** satır için birden fazla aday SKU var.
  - **CAKISMA:** barkod veya kod bu kanalda başka bir SKU'ya bağlı, ya da barkod ile kod farklı ilanlara bağlı. Aynı SKU'nun yeni barkodu çakışma değil, ikinci ilandır.
  - "Eşleşmeyenlerden SKU oluştur": aynı model + renk + bedendeki satırlar için **tek SKU** açılır ve her satıra bir ilan bağlanır. SKU barkodu grubun ilk satırınınkidir.
- **Genel düşük stok eşiği:**
  - `AppSetting.DusukStokEsigi` (varsayılan 0 = davranış değişmez). Satılabilir ≤ N ise kanallara 0 gönderilir.
  - `StockAllocator.Allocate(..., lowStockThreshold)` saf fonksiyon olarak kaldı. Eşik, ortak havuz değeri üzerinde, **paydan önce** uygulanır.
  - `SyncStore` eşiği aynı sorguda okur; panel, mutabakat ve gönderim aynı hesabı kullanır.
  - Eşik değişince `PUT /api/settings` (Yönetici) aynı transaction'da `StokEsigiDegisti` outbox olayı yazar. OutboxIsle bu olayda tüm aktif ilanları yeniden hesaplar ve **yalnızca değişen hedefleri** kuyruğa alır. Trendyol'un "aynı istek 15 dk içinde tekrar edilemez" kuralı nedeniyle `KanalStokYenile` kullanılmadı.
  - Ürün bazlı eşik kapsam dışı.
- **Panel:**
  - Stok Listesi'nde bir kanalın birden fazla ilanı tek hücrede "(n ilan)" olarak gösterilir. Herhangi bir ilan saparsa hücre kırmızı olur; ipucunda ilan başına barkod, beklenen ve kanaldaki değer var.
  - Ayarlar → "Kanala 0 gönderme eşiği" alanı eklendi.
- **Oversell penceresi** (yapılandırmadan; bizim tarafımızda en kötü yaklaşık 140 sn):

  | Adım | En fazla |
  |---|---|
  | SiparisCek (`*/2 * * * *`) | 120 sn |
  | OutboxIsle | 10 sn |
  | StokGonder | 10 sn (kanal başına çalışma başına 100 kayıt) |

  Doğrulanamayanlar: Hangfire tekrarlayan job tetikleme gecikmesi, job süreleri, kanalların toplu işlem uygulama süresi ve siparişin kanal API'sinde görünme gecikmesi.
- **Doğrulanmadı:** Trendyol'un "Çoklanan Ürün Satışı" durumundaki ilanların stok güncellemesini kabul edip etmediği. Reddederse ilgili kuyruk kaydı 5 denemede HATA'ya düşer ve Senkron Merkezi'nde görünür.
- **Ayrı adım (kapsam dışı):** set ürün çarpanı (Carpan). İlan başına çarpan kolonu, `FLOOR(satılabilir / çarpan)` ve rezervasyonda adet × çarpan gerektirir; ayrı ADR ile planlanacak.

**Migration öncesi kontrol sorgusu:**
```sql
SELECT 'KanalBarkod' AS Anahtar, c.Kod AS Kanal, l.KanalBarkod AS Deger, COUNT(*) AS IlanSayisi,
       STRING_AGG(CAST(l.Id AS varchar(20)), ',') AS IlanIdleri, STRING_AGG(CAST(l.SkuId AS varchar(20)), ',') AS SkuIdleri
  FROM dbo.ChannelListing l JOIN dbo.Channel c ON c.Id = l.ChannelId
 WHERE l.KanalBarkod IS NOT NULL GROUP BY c.Kod, l.KanalBarkod HAVING COUNT(*) > 1
UNION ALL
SELECT 'KanalUrunKodu', c.Kod, l.KanalUrunKodu, COUNT(*),
       STRING_AGG(CAST(l.Id AS varchar(20)), ','), STRING_AGG(CAST(l.SkuId AS varchar(20)), ',')
  FROM dbo.ChannelListing l JOIN dbo.Channel c ON c.Id = l.ChannelId
 WHERE l.KanalUrunKodu IS NOT NULL GROUP BY c.Kod, l.KanalUrunKodu HAVING COUNT(*) > 1;
```
2026-11-17 sonucu: test veritabanında 993 ilan, 0 çakışma; ana veritabanında 0 ilan.

## ADR-024: Kanal stoğu ile defter stoğunun uzlaştırılması
**Tarih:** 2026-11-17
**Bağlam:**
- Mutabakat kanal değerini yalnızca `ChannelStockSnapshot`'a yazıyordu.
- OutboxIsle yalnızca hedefi son gönderilenden farklı ilanları gönderdiği için kanalda elle değişen stok kendiliğinden düzelmiyordu.
- Refresh uç noktasının (`KanalStokYenile`, kanalın tüm aktif ilanlarını zorla gönderir) panelde karşılığı yoktu.

**Karar 1 — Sapmayı kanala yaz:**
- `POST /api/sync/reconciliation/push {listingIds}` (Yönetici) seçilen ilanlar için `EslestirmeDegisti` outbox olayı yazar. OutboxIsle bu olayda hedef aynı olsa bile kuyruğa alır.
- **Yeni hat açılmadı:** bekleyen kayıt tekilliği, Polly hız limiti ve yeniden deneme aynen geçerli.
- Gönderilemeyen ilanlar gerekçesiyle atlanır: bulunamadı, ilan pasif, kanal pasif, SKU'ya stok girilmemiş.
- **15 dk koruması:** Trendyol ve Beymen'de aynı değer son 15 dk içinde gönderildiyse satır atlanır (doküman: "aynı istek 15 dk tekrar gönderilemez"; reddetme yanıtı doğrulanmadı).
- **Panel (Senkron Merkezi):** satır başına "Kanala gönder", seçilen veya listelenen sapmaları gönderme, SKU araması, "Kanalı tamamen yenile" (mevcut refresh, onaylı).

**Karar 2 — Kanal stoğunu deftere al:**
- **Önizleme:** `GET /api/stock/channel-intake/preview` (Yönetici + Depo). Kaynak kanalda AKTIF ilanı olan SKU'lar için son mutabakat değerleri ve önizleme anı (veritabanı saati) döner.
- **Uygulama:** `POST /api/stock/channel-intake/apply` (yalnızca Yönetici; fiziksel bir sayım değil, dış kaynak).
- **Formül** (yeni yordam `usp_KanalStokAl`, bakiye satırı kilitli):
  - Hedef satılabilir = MAX(0, kanal değeri − çekimden sonraki net rezervasyon). Önizlemede önerilir, kullanıcı düzenleyebilir.
  - Yeni Fiziksel = Rezerve + Güvenlik stoğu + MAX(0, hedef − önizlemeden sonraki net rezervasyon). Rezerve ve güvenlik stoğu uygulama anındaki değerlerdir.
  - Net rezervasyon = REZERVE − REZERVE_IPTAL hareketleri.
  - Gerekçe: kanal değeri bizim gönderdiğimiz satılabilir stoktur. Çekimden veya önizlemeden sonra gelen siparişler bu değerde yer almaz, bu yüzden düşülür; aksi halde satılabilir stok fazla görünür. Sipariş çekimden önce verilip sonra kaydedildiyse iki kez düşülür; sonuç daha düşük stok olur, yani oversell açısından güvenli taraf.
  - Rezerve ≤ Fiziksel her zaman sağlanır.
- **Çakışan hareket:** önizlemedeki Fiziksel değişmişse (giriş, sayım, sevk) satır 50030 ile reddedilir.
- **Yarım hata:** her satır kendi transaction'ında; sonuç satır başına döner (UYGULANDI / FARK_YOK / REDDEDILDI). Fark yoksa hareket yazılmaz; tekrar uygulamak güvenlidir.
- **Denetim izi:** StockMovement'ta Tip = SAYIM, KaynakTip = `KANAL_ALIM`, KaynakId = `KANAL_ALIM:{parti}:{kanal}`, Kullanici = oturumdaki kullanıcı.
- **Yayılım:** uygulanan SKU'ların tüm aktif ilanları Karar 1 ile zorla gönderilir; böylece tüm kanallar aynı değeri görür.
- **Kanallar farklı değer gösterirse:** kullanıcı bir kaynak kanal seçer; satırlar kaynak değeriyle doldurulur ve her satır elle düzenlenebilir. Tüm kanalların değerleri ve en düşük değer gösterilir. Aynı kanalda birden fazla ilan farklı değer gösteriyorsa en düşüğü alınır ("ilanlar farklı" işareti).
- **Varsayılan olarak seçilmeyen satırlar** (`ChannelIntakeRules`, saf fonksiyon):
  - kanalda değer yok;
  - ilanlar farklı;
  - kanal payı %100 değil;
  - kanal 0 gösteriyor ve düşük stok eşiği > 0;
  - değer kanal üst limitinde;
  - çekim 24 saatten eski;
  - çekimden sonra defterde giriş, iade girişi veya sayım var;
  - ilan için bekleyen gönderim var;
  - fark yok.

  "Çekimden sonra sipariş" yalnızca bilgi işaretidir, seçimi engellemez.
- **Tek depo (ADR-015):** birden fazla depo varsa özellik kapalıdır.
- **Migration:** tablo değişikliği yok (KaynakTip serbest metin). Yalnızca yeni yordam `Procedures/08_usp_KanalStokAl.sql`; yordamlar her açılışta yeniden çalışır.

**Doğrulanmadı / risk:**
- Trendyol ve Beymen'in 15 dk içinde tekrarlanan aynı isteğe nasıl yanıt verdiği.
- Kanalların sipariş iptalinde kendi stoğunu artırıp artırmadığı.
- Siparişin kanal API'sinde görünme gecikmesi.
- Mutabakat anı (`CekilmeZamani`) uygulama sunucusunun saatiyle, hareket zamanı (`Tarih`) veritabanı saatiyle yazılıyor; iki saat arasındaki fark çekimden sonraki rezervasyon hesabını birkaç saniye kaydırabilir.

## ADR-025: Satıcı Bilgileri: kanal referans verisi (M1)
**Tarih:** 2026-11-18
**Bağlam:** Entegrasyon genişletme planının ilk modülü (onaylı sıra: Satıcı Bilgileri → Sipariş/Teslimat → İade → Fiyat → Soru-Cevap). Yalnızca diğer modüllerin gerçekten kullanacağı referans verisi çekilir. Ürün oluşturma ve içerik güncelleme kapsamdan çıktığı için adres, marka ve kategori listeleri çekilmez.
**Karar:**
- **Tipler:**
  - `KARGO_FIRMASI`: M2'de "kendi kargomuzla gönderim" için firma seçimi.
  - `IADE_RED_GEREKCE`: M3'te Yönetici'nin iade reddi.
- **Migration 0009:**
  - `ChannelReference(Id, ChannelId, Tip, KanalKimlik BIN2, Ad, Veri JSON, Kaynak [API|DOKUMAN], CekilmeZamani, Aktif)` + `UNIQUE(ChannelId, Tip, KanalKimlik)`. Spesifikasyon listesine **ek**.
  - `ChannelJobState.IsTipi`'ne `REFERANS_CEK` eklendi.
- **Sözleşme:** `IChannelAdapter.FetchReferenceAsync(ChannelReferenceType, ct)` → `ChannelReferenceList(Items, Source, Note)`. Spesifikasyon §6'ya **ek**; salt okuma. Kanal bir tipi doğrulanmış kaynakla vermiyorsa `NotSupportedException`.
- **Kanal bazında:**

  | Kanal | Kargo firmaları | İade red gerekçeleri |
  |---|---|---|
  | Trendyol | API: `GET /product/lookup/cargo-providers` → `[{code,name}]` (Cargo Providers Service) | API: `GET /order/claim-issue-reasons` → `[{id,name}]` (getClaimsIssueReasons); `id` createClaimIssue'ya gider |
  | Hepsiburada | **Desteklenmiyor:** `GET shipping-external/cargoFirms/{merchantId}` var, yanıt şeması dokümanda yok | **Desteklenmiyor:** red servisi `ClaimRejectionReason`'ı serbest metin alıyor, değer listesi yok |
  | Beymen | Doküman: "Cargo Companies" sabit tablosu (32 kayıt) | Doküman: `SellerReasonType` enum'u (0–11); Veri'de sayısal değer + `gorselZorunlu` (PackageIsEmpty dışında görsel zorunlu) |
  | Mock | Bellek içi örnek | Bellek içi örnek |

- **ReferansCek job'ı** (`ReferenceFetchJob`, Hangfire):
  - Kanal başına haftalık (`Senkron:ReferenceCron`, varsayılan Pazartesi 01:00 UTC); zamanlanmış çalışmada pasif kanal atlanır.
  - Panelden elle tetiklenebilir (`POST /api/channels/{id}/references/refresh`, Yönetici; çalışırken 409).
  - Upsert MERGE ile yapılır; ikinci çekim yeni kayıt açmaz.
  - Kanalda kaybolan kayıt **silinmez**, `Aktif=0` olur.
  - Kanal **boş liste** dönerse mevcut kayıtlar korunur (geçici hata listeyi silmesin).
  - Desteklenmeyen tip mevcut kayıtlara dokunmaz ve hata sayılmaz.
  - Sonuç tip başına `ChannelJobState.SonSonuc`'ta tutulur.
- **Okuma:** `GET /api/channels/{id}/references?type=&includeInactive=` (tüm roller).
- **Panel:** Ayarlar → "Kanal referans verisi" kartı: kanal ve tip seçimi, pasifleri göster, son çekme durumu, "Kanaldan yenile" (Yönetici).
- **Deneme modu:** `Senkron__ReferenceCron` kapatılan cron'lar listesine eklendi. Elle yenileme salt okuma olduğu için deneme modunda çalışır.
- **Playwright:** kurulu tarayıcı kanalı `E2E_BROWSER_CHANNEL` ile seçilebilir (varsayılan chrome). Makinede Chrome kaldırıldığı için testler `msedge` ile çalıştırıldı.
**Doğrulanmadı:**
- Trendyol: lookup kodlarının (ör. `YKMP`) sipariş tarafında (alternatif teslimat / kargo değişikliği) aynen kullanıldığı (dokümana göre bu kodlar ürün oluşturma/güncelleme içindir) → M2'de okunacak.
- Hepsiburada: `cargoFirms` yanıt şeması ve `ClaimRejectionReason` geçerli değerleri.
- Beymen: dokümandaki sabit tabloların güncelliği (servis yok).

## ADR-026: Kanala yazma altyapısı ve sipariş durumu bildirimi (M2)
**Tarih:** 2026-11-18
**Bağlam:**
- Gerçek kanallara yeni yazma işlemleri ekleniyor (sipariş durumu ve kargo; ileride iade kararı, fiyat, soru cevabı).
- Bunların canlı kanala kazara gitmemesi gerekiyor.
- `UpdateOrderStatusAsync` üç gerçek adaptörde de `NotSupportedException` fırlatıyordu ve hiçbir yerden çağrılmıyordu.
- Kanal paket ve satır kimlikleri saklanmıyordu (ADR-017).

**Karar 1 — Yazma kapısı (dört koşul):**
- **Global kilit:** `Kanallar:YazmaKapali` (yapılandırma; veritabanında değil). Deneme modu `true` yapar ve ayarlanamazsa başlamaz.
- **Mod:** yalnızca `Gercek` adaptörler `GuardedChannelAdapter` ile sarılır. Mock bellek içidir, dışarı bir şey gitmez.
- **Kanal** aktif olmalı.
- **Yetenek:** `ChannelCapability` (migration 0010) KAPALI/DENEME/ACIK.
  - Varsayılan KAPALI. STOK, mevcut davranış bozulmasın diye ACIK tohumlandı.
  - Yetenekler: STOK, SIPARIS_DURUM, IADE_KARAR, FIYAT, SORU_CEVAP.
- `GuardedChannelAdapter` yazma metotlarını (stok, fiyat, sipariş durumu) kapıdan geçirir; okumalar doğrudan geçer. Kapı bileşim kökünde uygulandığı için yeni bir kod yolu kapıyı atlayamaz (birim testli).
- Yetenek değişikliği kullanıcı adı ve zamanla yazılır ve UYARI düzeyinde loglanır.

**Karar 2 — Kanal komut kuyruğu (`ChannelCommand`, migration 0010):**
- Stok ve fiyat dışındaki yazmalar içindir (stok/fiyat SyncQueue'da kalır).
- Oluşturma anında yetenek kontrol edilir: KAPALI → oluşturulmaz; DENEME → `ONAY_BEKLIYOR`; ACIK → `BEKLIYOR`. Gönderilecek içerik (Payload) oluşturma anında yazılır, onaydan önce görülebilir.
- **İdempotentlik ve debounce:**
  - `UNIQUE(IdempotencyKey) WHERE Durum <> 'IPTAL'`: aynı bildirim ikinci kez oluşmaz; iptal edilen yeniden oluşturulabilir.
  - Hedef başına tek bekleyen kayıt.
- **KanalKomutGonder** (Hangfire, kanal başına, `Senkron:CommandCron` = 30 sn): READPAST ile talep eder; deneme sayacı talep anında artar.
  - Başarı → GONDERILDI.
  - Yazma engeli → hata sayılmaz; deneme geri alınır, kayıt bekler.
  - `ArgumentException` / `NotSupportedException` → hemen HATA.
  - Diğer hatalar → üstel bekleme (30 sn × 2^(n−1), en fazla 30 dk); **5. başarısızlıkta HATA** + UYARI logu.
- **Panel ve API:** Senkron Merkezi → "Kanal işlemleri"; Onayla / Yeniden dene / İptal (Yönetici).

**Karar 3 — Sipariş durumu bildirimi:**
- **Sözleşme sapması (spesifikasyon §6):** `UpdateOrderStatusAsync(OrderStatusUpdate)`. İstek sipariş no, paket kimliği, satır kimlikleri ve adet, hedef durum, kargo ve "kendi kargo" bilgisini taşır.
  - Ek: `DescribeOrderNotification(target, ownCargo)`, statik kural (desteklenir / gerekmez / zorunlu alanlar).
  - `NormalizedOrder.ChannelPackageId` ve `NormalizedOrderLine.ChannelLineId` eklendi.
- **Migration 0011:** `[Order].KanalPaketNo`, `OrderLine.KanalSatirNo`. Eski siparişlerde boş kalır; sonraki çekim boş alanları doldurur. Dolmazsa kayıt "kanal kimliği eksik" diye reddedilir.
- **Yankı yok:** yalnızca son geçişi PANEL kaynaklı olan sipariş bildirilir. Kanaldan gelen durum "gerekmiyor" olur.
- **Bildirim modu** (`Channel.Ayarlar.bildirimModu`):
  - `ONAYLA_GONDER` (varsayılan): Depo durumu değiştirir, ardından seçilen siparişler için "Kanala bildir" der (onay kutusu ile).
  - `OTOMATIK`: panel geçişi Hazırlanıyor bildirimini kendiliğinden kuyruğa alır. Kargoda kargo bilgisi istediği için otomatik değildir.
  - Otomatik oluşturma durum geçişinden **ayrı transaction**'dadır: oluşturulamazsa sipariş "bildirilmedi" listesinde kalır.
- **Kargo:** firma, takip no ve takip linki opsiyoneldir; kanal kuralı gerektiriyorsa zorunludur. Firma kodu, kanalın M1 kargo referans listesi doluysa o listede olmalıdır. "Kargoda" bildirimi kanal kargosunda gerekmez.
- **Kanal bazında** (resmi doküman, 2026-11-18):

  | Kanal | Hazırlanıyor | Kargoda (kendi kargo) | Kargoda (kanal kargosu) |
  |---|---|---|---|
  | Trendyol | `PUT /order/sellers/{sellerId}/shipment-packages/{packageId}` `{lines:[{lineId,quantity}],params:{},status:"Picking"}`. **Invoiced opsiyonel** (Order Process Flow), gönderilmez; fatura üretilmez. | `PUT …/{packageId}/alternative-delivery` `{isPhoneNumber:false, trackingInfo:<takip linki>, params:{}}` (link zorunlu) | bildirim yok (kargo teslim alınca Shipped) |
  | Hepsiburada | `POST oms-external/packages/merchantid/{merchantId}` `{lineItemRequests:[{id,quantity}]}` (kalem id = `items[].id`) | **desteklenmiyor**: intransit uç paket numarası istiyor, paketleme yanıtı doğrulanmadı | bildirim yok |
  | Beymen | `PUT /orders/{id}/status/picking` (id = sipariş GUID) | `PUT /orders/{id}/shipment/status` `{trackingNumber, trackingUrl, cargoCompanyCode, status:4}` (no + kod zorunlu) | bildirim yok |

- **Trendyol Order V2:** dokümana göre `/order/sellers/{sellerId}/orders` **15.10.2026'dan itibaren kullanımdan kalktı**, `/v2/orders` zorunlu.
  - Sipariş çekme V2'ye taşındı (size 50, en fazla iki haftalık aralık).
  - V2 alanları: `shipmentPackageId`, `shipmentPackageStatus`, `packageHistories`, `packageTotalPrice`, `orderDate`, `lines[].lineId/lineUnitPrice/stockCode`. Eski alanlar geriye uyumluluk için okunur.
  - Gerçek hesapta görülen sipariş 401'lerinin olası nedeni budur (doğrulanmadı).
- **Yetki:** bildirim Yönetici + Depo (`OrderWrite`); yetenek, bildirim modu ve komut eylemleri yalnızca Yönetici.
- **Panel:**
  - Ayarlar → "Kanala yazma yetenekleri" (bildirim modu dahil).
  - Siparişler → "Kanala bildirim" kartı (filtre, sipariş no araması, toplu seçim, kendi kargo formu, sonuç tablosu); sipariş detayında bildirim geçmişi.
  - Senkron Merkezi → "Kanal işlemleri".

**Plandan sapmalar:**
- "Kanalda doğrulandı" işareti uygulanmadı. Panelden Hazırlanıyor yapılan sipariş, kanal Picking bildirdiğinde bizde durum değişmediği için iz bırakmıyor. Doğrulama, bir sonraki çekimde kanal durumu ilerlediğinde zaman çizelgesinde görülür.
- M2 tek commit (altyapı + bildirim birbirine bağlı).

**Doğrulanmadı:**
- Trendyol: Picking tekrarında dönen yanıt.
- Trendyol: alternatif teslimatın hesapta açık olma şartı ve `isPhoneNumber` çelişkisi.
- Trendyol: V2'de size üst sınırı.
- Hepsiburada: paketleme yanıtı (paket numarası).
- Hepsiburada: intransit `shippedDate` biçimi.
- Beymen: picking yanıt kodu ve tekrar çağrı sonucu.
- Yazma isteklerinde Polly yeniden denemesi (5xx'te) Hepsiburada paketleme POST'unu tekrarlayabilir; ikinci paketleme kanal tarafından reddedilmeli (doğrulanmadı).

## ADR-027: Kanal iade talepleri: okuma, depoya giriş ve karar bildirimi (M3)
**Tarih:** 2026-11-18
**Bağlam:**
- Trendyol ve Beymen iade okuma metotları yazılıydı ama hiçbir yerden çağrılmıyordu; spesifikasyon akış 6 (IadeCek) kurulmamıştı.
- İade için tablo yoktu. Panel yalnızca durumu İade olan siparişlerde satır bazında "Depoya girdi" (`OL:`) yapabiliyordu.
- Hepsiburada iade okuma `NotSupportedException` fırlatıyordu.

**Karar:**
- **Migration 0012:**
  - `ReturnRequest`: `UNIQUE(ChannelId, KanalIadeNo)`, müşteri kişisel verisi yok.
  - `ReturnRequestLine`: `UNIQUE(ReturnRequestId, SatirAnahtar)`; `KanalKalemleri` JSON = kanalın karar servisinin istediği kalem kimlikleri.
  - `ReturnDecision`: yalnızca eklenir (denetim izi; kullanıcı, zaman, gerekçe, komut).
  - `UX_StockMovement_IadeSatiri`: `KaynakTip='IADE_SATIRI'` için `UNIQUE(KaynakId, Tip)`.
- **IadeCek** (`ReturnFetchJob`, `Siparis:ReturnFetchCron` = 15 dk, kanal başına; elle `POST /api/returns/fetch/{kanal}`):
  - Pencere: son başarılı − 1 saat, ilk çalışmada 7 gün. Aynı talep kaç kez gelirse gelsin tek kayıt.
  - Kanal okumayı desteklemiyorsa atlanır, hata sayılmaz.
  - Sipariş eşleşmesi: kanal sipariş no birebir; yoksa aynı sipariş numarasında tek paket varsa o.
  - Satır eşleşmesi: kanal satır kimliği (M2 `KanalSatirNo`), sonra SKU.
- **Depoya giriş (Depo + Yönetici):** `usp_IadeGiris`, KaynakId `RL:{satır}`.
  - Yalnızca siparişi eşleşmiş ve **SEVK hareketi olan** satır girilir. Satılmayan mal stoğu şişirmez.
  - Toplam iade girişi sipariş adedini aşamaz.
  - Eski sipariş ekranı yolu (`OL:`) ile kanal talebi yolu (`RL:`) birbirini engeller; çift giriş olmaz.
  - Depoya giriş kanala karar bildirimini **tetiklemez**.
- **Karar (yalnızca Yönetici):** ONAY | RED | TESLIM_ALINDI.
  - `ReturnDecision` ve `IADE_KARAR` kanal komutu aynı transaction'da yazılır (M2 kuyruğu; yetenek `IADE_KARAR`, varsayılan KAPALI).
  - Aynı karar ikinci kez oluşmaz.
  - Red gerekçesi zorunludur. Kanalın izin verdiği listede olmalı, ve M1 red gerekçeleri doluysa o listede bulunmalı.
- **Sözleşme eki (§6):** `DecideReturnAsync(ReturnDecisionRequest)`, `DescribeReturnDecision(kind)`.
  - `NormalizedReturn.ChannelStatus`; `NormalizedReturnLine.LineKey` ve `ChannelItemIds`.
  - Gerçek adaptörde yazma kapısından (`IADE_KARAR`) geçer.
- **Kanal bazında** (resmi doküman, 2026-11-18):

  | Kanal | Okuma | Onay | Red | Teslim alındı |
  |---|---|---|---|---|
  | Trendyol | getClaims (startDate/endDate = lastModifiedDate) | `PUT /claims/{claimId}/items/approve` `{claimLineItemIdList, params:{}}` (yalnızca WaitingInAction) | `POST /claims/{claimId}/issue?claimIssueReasonId&claimItemIdList&description`. Diğer gerekçeler dosya eki istiyor; **yalnızca 1651, 451, 2101**. | yok |
  | Beymen | `GET /claims` | `PATCH /claims/{id}/approve` `[{productId, quantity}]` | `PATCH /claims/{id}/dispute`. Görsel zorunlu; **yalnızca PackageIsEmpty (9)**. | `PATCH /claims/{id}/receive` |
  | Hepsiburada | **desteklenmiyor**: talep listesi yanıt şeması portalda yok | (dokümanda var, kullanılmıyor) | (dokümanda var, kullanılmıyor) | — |

- **Panel:** İade / İptal → "Kanal iade talepleri" (varsayılan sekme).
  - Liste ve detay: satırlar, "Depoya ulaştı" butonu ve girilemiyorsa nedeni.
  - Yönetici için Onayla / Teslim alındı / Reddet (gerekçe kanalın listesinden; yalnızca izin verilenler).
  - Kararlar ve kanal komut durumu.
- **Demo veri:** kargodaki Trendyol siparişi için bir iade talebi.

**Doğrulanmadı:**
- Trendyol: `orderShipmentPackageId`'nin iade paketi mi asıl paket mi olduğu (doküman "return package" diyor) → sipariş, numaraya göre tek paketse eşleşir.
- Trendyol: claim `orderLine.id` ile V2 `lineId`'nin aynı değer olduğu.
- Trendyol: `claimItemIdList`'in virgülle verilmesi; eksiz `issue` POST'unun gövdesi.
- Beymen: satır `status` kodlarının anlamı; `images` alanının biçimi.
- Hepsiburada: GET /claims yanıtı (gerçek bir örnek `docs/` altına konmalı).

## ADR-028: Fiyat: SKU fiyatı, kanal kuralı, onay eşiği ve gönderim (M4)
**Tarih:** 2026-11-18 (ürün sahibi kararı; ADR-015'in fiyat maddesini iptal eder)
**Bağlam:**
- Fiyatlar kanal panellerinden elle giriliyordu.
- Ürün sahibinin istekleri:
  - SKU başına liste ve satış fiyatı tutulsun.
  - Kanal başına kural (çarpan, sabit ek, yuvarlama, taban) ve ilan başına elle fiyat olsun.
  - Gönderim varsayılan kapalı olsun; açmadan önce önizleme gösterilsin.
  - Taban asla delinmesin.
  - Son gönderilene göre %X'ten büyük değişim (varsayılan %30) otomatik gitmesin, onay beklesin.
  - KDV dahil/hariç bilgisi kanalın resmi dokümanından doğrulansın; varsayım yapılmasın.

**Karar:**
- **Migration 0013:**
  - `Sku.ListeFiyati` / `Sku.SatisFiyati` (KDV dahil, TL; NULL = fiyat yok → kanala gitmez).
  - `ChannelListing.FiyatOverride`, `SonGonderilenSatisFiyati`, `SonGonderilenListeFiyati`, `SonFiyatSenkron`.
  - `SyncQueue.GonderilenSatisFiyati` / `GonderilenListeFiyati`.
  - `PriceApproval` (ilan başına tek bekleyen; filtreli UNIQUE).
  - `PriceHistory` (yalnızca eklenir).
  - `AppSetting.FiyatSapmaEsigiYuzde` = 30.
  - Kanal kuralı `Channel.Ayarlar.fiyatKurali` = `{carpan, sabitEk, yuvarlama: YOK|TAM|KURUS_99|KURUS_90, taban}`.
- **Hesap** (`Domain/Pricing/PriceAllocator`, saf):
  - satış = ilan fiyatı ?? yuvarla(SKU satış × çarpan + ek)
  - liste = yuvarla(SKU liste × çarpan + ek) (yoksa satış)
  - satış = MAX(satış, taban)
  - liste = MAX(liste, satış)
  - ,99 ve ,90 yuvarlama hesaplananı hiçbir zaman düşürmez.
- **Karar** (`PriceSyncPolicy`, saf). Sıra:
  1. Gönderilemez (fiyat yok, kural geçersiz, varyant fiyatları farklı) → kuyruğa girmez.
  2. Son gönderilenle aynı → gönderilmez.
  3. Bu değer son gönderimden sonra onaylanmış → gönder.
  4. Bu değer reddedilmiş → gönderme, yeni onay da açma.
  5. Yetenek DENEME → onay (ilk gönderim dahil).
  6. Eşik aşıldı → onay.
  7. Aksi halde gönder.
  - İlk gönderim (son gönderilen yok) eşiğe takılmaz; açmadan önceki önizlemede "ilk gönderim" olarak sayılır.
- **Hat:**
  - SKU fiyatı, ilan fiyatı, kanal kuralı, eşik, onay kararı ve FIYAT yeteneğinin açılması aynı transaction'da `FiyatDegisti` outbox olayı yazar.
  - OutboxIsle yalnızca FIYAT yeteneği KAPALI olmayan kanalların aktif ilanlarını değerlendirir: `SyncQueue(Tip=FIYAT)` (debounce) veya `PriceApproval`.
  - `FiyatGonder` (`Senkron:PricePushCron`, 10 sn; kanal başına) gönderim anında yeniden hesaplar ve karar verir; `BatchSorgula` tipine göre işler.
  - Onaya düşen veya artık gönderilecek bir şeyi kalmayan kuyruk kaydı silinir. Gönderilemeyen kayıt sade gerekçeyle HATA olur.
  - Yazma kapısı engellerse kayıt deneme sayılmadan bekler.
  - Stok hattı değişmedi (StokGonder yalnızca STOK kayıtlarını alır).
- **Kanala yazan yerler:** `SyncJobs.cs`'teki `PushStockAsync` ve `PushPriceAsync` çağrıları ile KanalKomutGonder. Gerçek adaptörde hepsi `GuardedChannelAdapter` yazma kapısından geçer (FIYAT yeteneği). Deneme modunda `Senkron__PricePushCron` da kapatılır.
- **Yetenek açma:** `PUT /api/channels/{id}/capabilities/FIYAT` DENEME/ACIK için adaptörün `DescribePriceUpdate().CanEnable` (uç doğrulandı **ve** KDV dahil dokümanda yazıyor) şartı var; yoksa 400 ve gerekçe. FiyatGonder da aynı şartı ayrıca kontrol eder.
- **Kanallar** (resmi doküman, 2026-11-18):

  | Kanal | Uç | Alanlar | KDV | Fiyat yeteneği |
  |---|---|---|---|---|
  | Trendyol | `POST /inventory/sellers/{id}/products/price-and-inventory` | `{barcode, salePrice, listPrice}`; liste ≥ satış; yalnızca fiyat gönderilebilir | **dokümanda yok** | **açılamaz** |
  | Hepsiburada | `POST listing-external…/listings/merchantid/{id}/price-uploads`, sonuç `…/price-uploads/id/{id}` | `[{hepsiburadaSku, price}]` (tek fiyat); eşik dışı fiyat listingi kilitler (MinLock/MaxLock) | **dokümanda yok** | **açılamaz** |
  | Beymen | `POST /batch/products/price-stock` | `{productId, salesPrice, platformPrice}`; liste → salesPrice, satış → platformPrice; salesPrice ≥ platformPrice; aynı ürünün beden varyantlarına aynı fiyat | **"VAT included" (iki alan)** | açılabilir |
  | softcotton | — | mock | — | mock |

- **Beymen varyant kuralı:** aynı ürünün aynı kanaldaki aktif ilanları farklı fiyata çıkıyorsa o ürünün hiçbir ilanı gönderilmez; önizlemede gerekçe görünür.
- **Yetki:**
  - Okuma: tüm roller.
  - Yalnızca Yönetici: SKU fiyatı, ilan fiyatı, kanal kuralı, eşik, onay/red ve Excel fiyat içe aktarma.
  - Her karar kullanıcı adı ve zamanla `PriceApproval` ve `PriceHistory`'ye yazılır.
- **Panel:** yeni "Fiyat" ekranı:
  - Onay bekleyenler (onayla/reddet).
  - Kanal kuralı, KDV durumu ve kaynak.
  - Önizleme ("N ilanın fiyatı değişecek": eski → yeni, ilk gönderim, onay gerekir, gönderilemez), elle ilan fiyatı.
  - Gönderimi aç (ACIK/DENEME) ve kapat.
  - SKU fiyatı, Excel (şablon, içe/dışa aktarma), eşik ve geçmiş.
  - Ayarlar → yetenek tablosunda FIYAT seçilince önce önizleme özeti gösterilir.
- **Excel:** `prices` türü: SKU Kodu, Satış Fiyatı, Liste Fiyatı. Virgül ve nokta kabul edilir; "1,234" gibi belirsiz değer reddedilir. Hatalar satır numarasıyla raporlanır.

**Doğrulanmadı / açık:**
- **Trendyol ve Hepsiburada KDV:** resmi dokümanda yok → fiyat yeteneği açılamaz. Resmi bir kaynak (ör. entegrasyon ekibinin yazılı yanıtı) `docs/` altına konunca `DescribePriceUpdate` güncellenir.
- **Beymen:** müşteriye gösterilen fiyatın `platformPrice` olduğu dokümandaki "Beymen sales price" ifadesinden çıkarıldı; ilk gerçek denemede kontrol edilmeli.
- **Hepsiburada:** price-uploads POST yanıtındaki takip alanı (`id` varsayıldı); sonuç `errors[]` şeması.
- **Trendyol:** fiyat-only batch sonucunda `requestItem` içinde barkod olduğu (stoktaki varsayımla aynı).
- Kanalların "15 dk aynı istek" kuralı: fiyat ve stok gövdeleri farklı olduğu için çakışmaz; ancak aynı fiyatı 15 dk içinde tekrar göndermek (ör. HATA sonrası yeniden dene) reddedilebilir.

## ADR-029: Müşteri soruları (Soru-Cevap, M5)
**Tarih:** 2026-11-18
**Bağlam:**
Ürün sahibinin kararları:
- Soru-cevap yalnızca resmi dokümanda doğrulanan kanallarda açılır; önce Trendyol. Doğrulanmayan kanal mock kalır.
- Destek cevap yazıp gönderebilir; müşteri adı maskeli kalır. Bunun için yeni bir "SoruCevap" yetkisi gerekir.
- İkinci kişi onayı yok. Ama gönderilen her cevap kullanıcı adı ve zamanla kaydedilir ve göndermeden önce "Gönderilecek cevap" onay kutusu zorunludur.
- KVKK: soru metni ve ürün bilgisi saklanır; müşteri adı ve iletişim bilgisi saklanmaz.

**Karar:**
- **Migration 0014:**
  - `CustomerQuestion`: `UNIQUE(ChannelId, KanalSoruNo)`; soru metni, ürün adı/kodu, SKU (ilan eşleşmesiyle), durum (CEVAP_BEKLIYOR | CEVAPLANDI | KAPANDI), kanal durumu, kanaldaki cevap, son cevap tarihi. Müşteri adı/kimliği **kolon olarak yok**.
  - `QuestionAnswerLog`: yalnızca eklenir (cevap metni, kullanıcı, zaman, kanal komutu).
  - `ChannelJobState.IsTipi` += `SORU_CEK`.
- **Sözleşme eki (§6):** `FetchQuestionsAsync(since)`, `AnswerQuestionAsync(QuestionAnswerRequest)`, `DescribeQuestionAnswer()`.
  - `NormalizedQuestion` müşteri alanı taşımaz; adaptörler müşteri alanlarını modellemez, dolayısıyla okumaz.
  - Gerçek adaptörde cevap yazma kapısından (`SORU_CEVAP`) geçer.
- **SoruCek** (`Senkron:QuestionFetchCron`, 5 dk, kanal başına; elle `POST /api/questions/fetch/{kanal}`, Yönetici):
  - Pencere: son başarılı − 1 saat, ilk çalışmada 7 gün.
  - Aynı soru kaç kez gelirse gelsin tek kayıt; durum değişince güncellenir.
  - Soru API'si doğrulanmamış kanal atlanır, hata sayılmaz.
- **Cevap** (`POST /api/questions/{id}/answer`; SoruCevap yetkisi = Yönetici + Destek):
  - Onay kutusu (`confirmed`) zorunlu.
  - Uzunluk kanal kuralına göre kontrol edilir.
  - Soru cevap bekliyor olmalı ve son cevap tarihi geçmemiş olmalı.
  - Soru başına tek etkin cevap var; değiştirmek için kanal işleminden iptal edilir.
  - Denetim kaydı ve `SORU_CEVAP` kanal komutu aynı transaction'da yazılır.
  - Yetenek KAPALI ise kayıt oluşmaz (409). DENEME ise Yönetici onayı bekler (M2 kuyruğu).
- **Kanallar** (resmi doküman, 2026-11-18):

  | Kanal | Okuma | Cevap | Durum |
  |---|---|---|---|
  | Trendyol | `GET /qna/sellers/{id}/questions/filter` (startDate/endDate ms, ≤2 hafta → 14 günlük parçalar; page 0'dan, size ≤50) | `POST …/questions/{id}/answers` `{text}` (10–2000; yalnızca WAITING_FOR_ANSWER) | uygulandı |
  | Beymen | `GET /questions/filter` (`items[]{id, question, answer, productName, modelCode, status, maxPermittedResponseTime, createdDate}`) | `POST /questions/{id}/answer` `{answer}` | uygulandı (durum kodları varsayım) |
  | Hepsiburada | `GET api-asktoseller-merchant-sit…/api/v1.0/issues` (merchantId başlıkta) — **liste kök şeması ve tarih biçimi yok** | `POST …/issues/{number}/answer` multipart (Answer ≤2000) | **desteklenmiyor (mock)** |

- **Panel:** yeni "Soru-Cevap" ekranı.
  - Liste: kanal, durum ve arama filtresi; cevap bekleyenler ve son tarihi en yakın olanlar önce.
  - Detay ve cevap formu: karakter sayacı, "Gönderilecek cevap" önizlemesi ve onay kutusu. Kutu işaretlenmeden "Cevabı gönder" pasiftir.
  - Gönderilen cevaplar: kullanıcı, zaman, kanal işlemi durumu.
  - Muhasebe ve Depo yalnızca görür.

**Doğrulanmadı:**
- Trendyol: startDate/endDate'in soru oluşturma mı son güncelleme tarihine mi uygulandığı (parametre açıklaması "orders" diyor). Örtüşmeli pencere kullanılıyor.
- Beymen:
  - `status` sayısal; dokümandaki `QnAStatus` enum sırasına göre 0 PendingResponse … 5 PlatformResponded varsayıldı.
  - Tarihler 'Z'siz → Türkiye saati varsayıldı.
  - Cevap uzunluk sınırı dokümanda yok; 10–2000 uygulanıyor.
- Hepsiburada: liste yanıt örneği (`docs/` altına) ve tarih parametre biçimi gerekiyor.

## ADR-030: Kanal başına sipariş çekme aralığı (webhook yerine kısa polling)
**Tarih:** 2026-11-18
**Bağlam:**
- Webhook ertelendi; uygulamanın herkese açık HTTPS adresi yok.
- Sipariş çekme tüm kanallarda 2 dakikada birdi. Bu, overselling penceresini (kanalda satılan ürünün bizde rezerve edilmesine kadar geçen süre) uzatıyor.
- Ürün sahibi, aralığın hız limitleri resmi dokümandan doğrulanarak kısaltılmasını istedi.

**Karar:**
- `Siparis:ChannelFetchCron` sözlüğü (kanal kodu → cron) eklendi; `OrderOptions.FetchCronFor(kanal)`:
  - Genel `Siparis:FetchCron` boşsa hiçbir kanalda SiparisCek kaydedilmez. Bu genel kapama şalteridir; testler ve deneme modu böyle çalışır.
  - Kanal ayarı doluysa o, boşsa genel ayar kullanılır.
- **Varsayılanlar:**

  | Kanal | Aralık | Dayanak (resmi doküman, 2026-11-18) |
  |---|---|---|
  | Trendyol | **1 dk** | "Service Limitations → Order Services": Get Shipment Packages **30 istek/dk** (en düşük kademe, 50.000 ürün) … 100 istek/dk. Çalışma başına çoğunlukla 1 istek (V2 orders, 50'lik sayfa, 10 dk örtüşmeli pencere). İstemci tarafı sınırlayıcı 40 istek / 10 sn. |
  | Hepsiburada | **1 dk** | Sipariş servisleri **1000 istek/sn**. Çalışma başına yaklaşık 5 istek (ödeme bekleyen, açık, kargolanan, teslim edilen, iptal). |
  | Beymen | 2 dk (değişmedi) | Hız limiti dokümanda yok; her çalışmada 7 gün geriye bakılıyor. Kısaltmak için önce limit teyit edilmeli. |
  | softcotton | 2 dk | mock |

- Overselling penceresi Trendyol ve Hepsiburada'da yaklaşık 140 sn'den yaklaşık 80 sn'ye iner: en fazla 60 sn çekme beklemesi, artı OutboxIsle, StokGonder ve kanalın işleme süresi.
- Yapılandırmayla değiştirilebilir, ör. `Siparis__ChannelFetchCron__TRENDYOL="*/2 * * * *"`.

**Doğrulanmadı:**
- Mağazanın Trendyol listeleme kademesi bilinmiyor; en düşük kademe (30 istek/dk) esas alındı.
- Yoğun dönemde bir çalışmanın birden fazla sayfa çekmesi gerekebilir. 1 dakikada 30 isteğe yaklaşılırsa Polly 429'da bekler.

## ADR-031: Katalogdan ürün oluşturmada varyant ayrımı, ürün linki ve kanal fiyatı
**Tarih:** 2026-11-18
**Bağlam:**
- Trendyol'dan "Eşleşmeyenlerden ürün/SKU oluştur" ile 584 ürün çekildi ve 209 SKU oluştu; 82 SKU 2 ile 15 arası ilana bağlandı.
- Kod barkodları model kodu + renk + beden ile grupluyordu. Trendyol'da renk ve beden yalnızca "Renk"/"Beden" adlı özelliklerden okunuyordu (TODO). Havlularda bu adlar gelmedi, bu yüzden aynı modelin farklı renk ve ebatları tek SKU'da birleşti.
- Örnek: BAMBU85150'ye `Bambu85150000…006` ve stok kodu farklı `Bambu50100004` (50×100) bağlandı; CHAIN75150'nin 3 rengi tek SKU oldu.
- Sonuç: tek stok havuzu. Yazma açılsaydı bir rengin stoğu diğerlerine de gönderilir, fazla satış olurdu.
- Deftere stok yazılmamıştı (0 hareket); veri silinip yeniden oluşturuldu.

**Karar:**
- **Trendyol katalog okuma** (filterApprovedProducts yanıtı, dokümanda doğrulandı):
  - `content.contentId`: aynı productMainId altındaki her renk ayrı içerik.
  - `variants[].attributes`: tüm varyant özellikleri.
  - `variants[].productUrl`: trendyol.com ürün sayfası.
  - `variants[].price.salePrice` / `listPrice`.
  - Beden yerine "Ebat", "Boyut", "Ölçü", "Numara" da okunur (TODO: doğrulanmalı — kategoriye göre değişebilir).
- **Varyant anahtarı** `c{contentId}|Özellik=Değer…`. Renk okunamasa bile içerik kimliği ayırır.
- **Ürün oluştururken gruplama** (`ChannelCatalogService.GroupKey`):
  1. Varyant anahtarı varsa: model + anahtar + stok kodu.
  2. Renk veya beden biliniyorsa: model + renk + beden + stok kodu.
  3. Aksi halde her barkod ayrı SKU.
  - Farklı satıcı stok kodları hiçbir durumda birleşmez. Aynı varyantın ikinci ilanı (çoklanan ilan, ADR-023) yine aynı SKU'ya bağlanır.
- **Eşleştirici:** "model + renk + beden" adımı renk ve beden ikisi de bilinmiyorsa uygulanmaz. Yeni bir renk yalnızca model koduyla mevcut SKU'nun stok havuzuna bağlanmaz; eşleşmeyenlere düşer.
- **İlan kaydı:** `ChannelListing.KanalVerisi` JSON'a renk, beden, varyant (okunur metin), `urunLinki`, `kanalSatisFiyati`, `kanalListeFiyati` eklendi. Şema değişikliği yok. Salt okuma; kanala hiçbir şey yazılmaz.
- **Panel:**
  - Stok Listesi'nde ürün adının altında ve Eşleştirme'de "Trendyol ↗" linki. Yeni sekmede, referrer gönderilmeden açılır; yalnızca https adresler gösterilir.
  - Fiyat önizlemesinde "Kanalda görünen" fiyat sütunu.
  - Hepsiburada ve Beymen dokümanında ürün sayfası adresi yok → link yok; adres biçimi tahmin edilmez.
- **Kanal Stoğu → Defter:** Bazı ilanların kanal değeri dönmediğinde (kara liste, arşiv) artık yanıltıcı "Kanal değeri 24 saatten eski" yerine `ILAN_DEGERI_YOK` ("İlanlardan bazıları kanal stok sorgusunda dönmedi") gösterilir. Eski çekim kontrolü değeri olan ilanlara uygulanır.

**Doğrulanmadı:**
- Havlu/nevresim kategorilerinde Trendyol varyant özellik adları. Yeniden çekimden sonra Eşleştirme ekranındaki "Kanalda" sütununda görülecek.
- `price.salePrice`'ın KDV dahil olup olmadığı (ADR-028); ekranda "kanalda görünen" olarak etiketlenir.

### ADR-031 eki: Hepsiburada ve Beymen (2026-11-18)
- **Beymen:** "Kanaldan ürün çek" artık `GET /products` ("Product Listing") ucundan çekiyor; önceden `products/price-stock/search` kullanılıyordu ve yalnızca id + barkod geliyordu.
  - Okunanlar: ad, model kodu, stok kodu, marka, kategori, renk (`platformColor.name`, yoksa `merchantColorCode`), beden (`size.name`), durum.
  - Fiyat: `salesPrice` → liste, `platformSalesPrice` → satış (ADR-028 ile aynı eşleme).
  - Her productId ayrı varyanttır → varyant anahtarı `p{id}`; ürün oluştururken asla birleşmez.
  - Renk ve beden dolu geldiği için Beymen ürünleri Trendyol'dan oluşan SKU'larla model + renk + beden adımında eşleşebilir. Renk adları kanallar arasında farklıysa eşleşmeyenlere düşer.
  - Mutabakat (stok okuma) değişmedi: `products/price-stock/search`.
  - TODO: doğrulanmalı — `/products` için en büyük sayfa boyutu ve 10.000 kayıt sınırı dokümanda yok; 100'lük sayfalar kullanılır.
- **Hepsiburada:** Listing sorgusundan `price` (alan tablosu: "Listingin fiyatıdır") ve `productName` okunur.
  - Her HBSKU ayrı varyanttır (`h{HBSKU}`). Bu uçta model, renk ve beden yok.
  - TODO: doğrulanmalı — JSON alan adları; dokümanda alan tablosu ve XML örneği var, JSON örneği yok.
- **Ürün linki:** Hepsiburada ve Beymen dokümanlarında ürün sayfası adresi alanı yok → link yalnızca Trendyol'da. Adres biçimi tahmin edilmez.

## ADR-032: Otomatik testler ayrı veritabanında (PazaryeriHub_OtoTest)
**Tarih:** 2026-11-18 (ürün sahibi onayı)
**Bağlam:**
- Entegrasyon testleri ve Playwright, deneme modunun kullandığı `PazaryeriHub_Test`'i paylaşıyordu.
- Kullanıcı deneme modunda gerçek Trendyol/Beymen verisi çekip "deftere al" yaptıktan sonra testlerin çalıştırdığı OutboxIsle bu olayları da işledi. Kullanıcının stokları test uygulamasındaki mock kanala "gönderilmiş" sayıldı. Gerçek kanala istek gitmedi; kayıtlar elle düzeltildi.
- Ayrıca testler kullanıcının API bilgisini ve kanal aktifliğini değiştirebiliyordu (ADR'siz düzeltme: fikstür geri yükleme).

**Karar:**
- **Bağlantı:** `TestDatabase` aynı bağlantı dizesinin sunucu ve kimliğini kullanır, veritabanı adını her zaman `PazaryeriHub_OtoTest` yapar.
  - Kaynak sırası değişmedi: `PAZARYERIHUB_TEST_DB` veya user-secrets `ConnectionStrings:PazaryeriHubTest`.
- **Oluşturma:** Veritabanı yoksa oluşturulur: Turkish_CI_AS, RCSI, snapshot isolation, AUTO_CLOSE OFF (DatabaseBootstrapper ile aynı).
- **Güvenlik kilidi:** Hedef `PazaryeriHub_Test` olursa testler başlamaz.
- **Playwright:** Aynı veritabanını kullanır (`Database__AutoCreate=true`).
- **Deneme modu** (`scripts/deneme-modu.ps1`): `PazaryeriHub_Test`'te kalır. Kullanıcının elle test verisi testlerden etkilenmez.
- **Fikstür geri yükleme** (kanal ayarları, API bilgileri, yetenekler, AppSetting, ChannelJobState) ek güvenlik olarak kalır.

**Doğrulama:** `PazaryeriHub_Test`'in 13 tablosunun satır sayısı ve CHECKSUM özeti testlerden önce, entegrasyon testlerinden sonra (135/135) ve Playwright'tan sonra (11/11) birebir aynı.

## ADR-033: Kanal durum kartları ve anlaşılır hata gösterimi
**Tarih:** 2026-11-19 (ürün sahibi onayı; UI/UX Pro Max kontrol listesiyle inceleme)
**Bağlam:**
- Bir kanalın çalışıp çalışmadığı dört ayrı yerdeydi. Kanalın Mock mu Gerçek mi çalıştığı ve seçilen ortam panelde hiç görünmüyordu (Beymen "veri çekmiyor" vakasının nedeni).
- Deneme modunda yetenekler "Açık" görünürken global yazma kilidi her şeyi engelliyordu; bu yalnızca küçük bir açıklama metnindeydi.
- Hata metinleri ham gösteriliyordu ("Kanal hata döndü (HTTP 503): …"); kullanıcıya ne yapacağı söylenmiyordu. Durum yalnızca kırmızı satır rengiyle ayrılıyordu.

**Karar:**
- **`GET /api/channels/status`** (okuma, tüm roller): kanal başına mod (`Kanallar:{KOD}:Mod`), API bilgisi var mı, **yalnızca** seçilen ortam (Prod/Stage/Test), global yazma kilidi, yetenekler, iş durumları ve genel durum.
  - API bilgisinin başka hiçbir alanı yanıta girmez (entegrasyon testi gizli değerin yanıtta olmadığını doğrular).
  - Genel durum saf fonksiyon `ChannelHealth.Evaluate`: KAPALI (pasif) → SAHTE (mock) → HATA (API bilgisi yok / kalıcı hata) → DIKKAT (yalnızca geçici hata veya hiç çekim yok) → CALISIYOR.
- **Hata sınıflandırma:** saf fonksiyon `ChannelErrorExplainer.Explain`. Adaptörlerin zaten sadeleştirdiği metni türe ayırır: KIMLIK, ERISIM_REDDI, HIZ_LIMITI, KANAL_ERISILEMIYOR, YAZMA_KILITLI, DESTEKLENMIYOR, TEKRAR_ENGELI, GECERSIZ_VERI, BILINMEYEN.
  - Her tür için başlık ("ne oldu"), öneri ("ne yapmalı") ve geçici mi bilgisi. " | " ile birleşik sorunlar listeye ayrılır (en fazla 3 + "N sorun daha").
  - `SyncQueueItem`, `JobStateItem`, `ChannelCommandDto`, `ChannelJobStateDto` yanıtlarına hesaplanan `errorInfo` eklendi; kayıtlı metin değişmedi.
  - **Teklif edilen migration (0015, hata türü sütunu) yapılmadı:** tür, saklanan metinden okuma anında hesaplanıyor. Şema değişmeden eski kayıtlar da sınıflanıyor; yeni adaptör mesajı eklenirse sınıflandırıcı ve birim testi güncellenmeli.
- **Panel:**
  - Ayarlar sekmelere ayrıldı (Kanallar / Stok kuralları / Kullanıcılar; `?sekme=`, `?kanal=`). Kanallar sekmesinde durum kartları, seçili kanalın ayarları (genel, API bilgisi, yetenekler, referanslar) ve tüm kanallar için yetenek özeti var.
  - Deneme modunda üstte "Kanala yazma kilitli" bandı görünür; yetenek özetinde "Açık · kilitli" yazar.
  - Genel Bakış'ın üstünde aktif kanalların kompakt kartları var. "Kanal işleri" Türkçe iş adlarıyla, "x dk önce" biçiminde, hatalılar önde ve sorunsuzlar isteğe bağlı gösteriliyor.
  - `ErrorExplain` bileşeni: başlık, öneri ve katlanır "Teknik ayrıntı". Senkron Merkezi, kanal işlemleri, elle çekme satırı ve Genel Bakış'ta kullanılıyor. `ErrorBox` sunucu hatalarında "Tekrar dene" sunar.
  - Durum her yerde simge + metin + renkle gösterilir. `:focus-visible` odak halkası, `prefers-reduced-motion`, sayısal sütunlarda `tabular-nums` ve çekim durumunda `aria-live` eklendi.
- Mevcut bileşenler (`Card`, `badge`, `alert`, `tabs`, `stats`, `kv`) ve renk değişkenleri korundu; yeni renk eklenmedi.

**Doğrulama:** birim (ChannelStatusTests 24), entegrasyon (ChannelStatusEndpointTests), Playwright (kanal durumu senaryosu; senkron hata senaryosu yeni gösterime göre güncellendi). Deneme modunda kullanıcının verisiyle elle kontrol edildi.

## ADR-034: Genel arama, SKU detay ekranı, satış raporları ve sipariş Excel'i
**Tarih:** 2026-11-19 (ürün sahibi isteği: öneri listesinden 3, 4 ve 7 numaralı maddeler)
**Bağlam:**
- Bir ürünün durumu (bakiye, kanal ilanları, satış, hareketler) dört ayrı ekrana dağılmıştı; tek ürüne odaklanan bir görünüm yoktu.
- SKU, barkod veya sipariş no ile bir kaydı bulmak için önce doğru ekrana gitmek gerekiyordu.
- Genel Bakış yalnızca bugünün kanal cirosunu gösteriyordu; dönem karşılaştırması, iptal/iade oranı ve en çok satan bilgisi yoktu.

**Karar:**
- **Genel arama** `GET /api/search?q=&limit=` (okuma, tüm roller; en az 2 karakter, en fazla 100; her grupta en fazla 8, üst sınır 20):
  - SKU: barkod (birebir) → SKU kodu öneki → kanal barkodu / kanal ürün kodu (birebir) → ürün adı (içerir) sırasıyla.
  - Sipariş: kanal sipariş no öneki.
  - SKU kodu ve sipariş no BIN2 collation'lı olduğu için önek hem olduğu gibi hem de `UPPER` ile aranır (Türkçe olmayan kural, i → I); kolon collation'ı değişmedi.
  - Müşteri bilgisi dönmez. Panelde üst çubuktaki "Hızlı bul" kutusu (`/` veya Ctrl+K, ↑/↓, Enter, Esc). SKU → `/stok/:id`, sipariş → `/siparisler?order=:id`.
- **SKU detay** `GET /api/skus/{id}/detail` + `/stok/:id` ekranı:
  - Ürün bilgisi, fiyat, depo bakiyeleri, kanal ilanları (Stok Listesi'yle aynı hesap: `BuildRowsAsync` + `ComputeTargetsAsync`), 7/30 günlük net satış ve son satış zamanı, son 20 hareket ve son 20 sipariş.
  - Stok Listesi, kritik stok, raporlar, sipariş satırları ve aramadan bağlantı verilir. Hareketler ekranı `?sku=` ile ön filtre alır.
- **Satış raporu** `GET /api/reports/sales?from=&to=&channelId=` (okuma, tüm roller):
  - Günler Türkiye saatine göre, iki uç dahil. Varsayılan son 30 gün, en fazla 366 gün; ters dönem 400.
  - **Net = İptal ve İade dışındaki siparişler.** Ciro `Order.Toplam`'dan (kanalın bildirdiği tutar); SKU cirosu satır tutarından (adet × birim fiyat). İptal oranı = iptal / tüm siparişler; iade oranı = iade / (tüm − iptal).
  - Genel Bakış'ın "bugünkü ciro"su değişmedi (İade'yi düşmez). Raporla aynı gün için küçük fark olabilir; bilinçli olarak bırakıldı.
  - Saf kısım `ReportPeriod` ve `SalesReportMath` (dönem çözümü, UTC aralığı, boş günleri 0 ile doldurma, oranlar); birim testli.
  - En çok satan ilk 10 (adede göre) ve "stoğu var, dönemde satmadı" ilk 10 (fiziksel stoğa göre). Eşleşmemiş satır adedi ayrıca bildirilir.
- **Sipariş Excel'i** `GET /api/orders/export` (sipariş listesiyle aynı filtreler; okuma, tüm roller):
  - Sipariş satırı başına bir satır; **müşteri özeti ve kargo bilgisi bilerek seçilmez** (KVKK; bu yüzden Destek dahil tüm rollere açık).
  - En fazla 50.000 satır; aşılırsa 400 ve "tarih aralığını daraltın".
  - Filtre koşulu `OrderService.FilterSql` / `FilterParameters` ile liste ve dışa aktarma arasında paylaşılır (davranış değişmedi).
- **Grafik:** kütüphane eklenmedi. Tek serili SVG çubuk (günlük net ciro): lejant yok, başlık adlandırır; üzerine gelince gün, ciro, sipariş ve kanal kırılımı; aynı veri "Tablo olarak göster"de. Renk mevcut `--primary` değişkeni; yeni renk eklenmedi. Kanal kırılımı renkle değil tabloyla verilir.
- **Kapsam dışı:** muhasebe raporu, KDV ayrımı, kâr/maliyet (maliyet verisi yok). Stok tükenme tahmini (öneri 2) ayrı iş.

**Doğrulama:** birim (SalesReportMathTests 13, SearchTermTests 4), entegrasyon (SearchDetailReportTests 7; rapor testi geçmişte rastgele bir günde önce/sonra farkıyla, başka testlerin verisinden yalıtılmış), Playwright (2 yeni senaryo + ekran turuna Raporlar).

## ADR-035: Stok Listesi'nde kanal bazlı görünüm ve yanlış eşleştirme uyarıları
**Tarih:** 2026-11-19 (ürün sahibi isteği: "eşleştirme korunsun; yanlış eşleştirmelere karşı her pazaryerinin çekilen stok listesini ayrı ayrı görebileyim")
**Bağlam:**
- Kanallar arası eşleştirme (bir SKU'nun birden fazla kanal ilanına bağlanması, ortak stok havuzu) korunacak.
- Stok Listesi yalnızca defter merkezliydi: her SKU satırında kanal hücreleri vardı. Bir kanal ilanının *kanalda neye karşılık geldiği* (kanalın verdiği ad, renk/beden, stok kodu) görünmüyordu; yanlış bağlanmış bir ilan fark edilmiyordu.
- Kanalın verdiği ürün adı ve marka ilan verisine (`KanalVerisi`) yazılmıyordu; yalnızca eşleşmeyenler tablosunda vardı.

**Karar:**
- **`GET /api/stock-overview/channels/{channelId}`** (okuma, tüm roller; `search`, `onlyDeviations`, `onlyReview`, sayfalama). Kanalın tüm ilanları; her satırda kanalın bildirdiği (barkod, kanal ürün kodu, stok kodu, ad, marka, renk/beden, varyant, son mutabakatta çekilen stok ve zamanı, ürün linki) ↔ bağlandığı SKU (kod, barkod, ad, renk/beden) ve gitmesi gereken stok (Stok Listesi'yle aynı `ComputeTargetsAsync`).
  - Özet sayılar (ilan, kontrol et, sapma, hiç çekilmemiş, kanalda olup bekleyen eşleşmeyen, son çekim) aramadan bağımsız, **kanalın tamamı** içindir.
  - Uyarı ve kanal adı bellekte hesaplandığı için kanalın tüm ilanları okunur (bugün ~800 ilan; çok büyürse SQL tarafına taşınmalı).
- **Yanlış eşleştirme kontrolü** saf fonksiyon `ListingMatchCheck.Evaluate` (birim testli). **Yalnızca uyarı üretir; eşleştirmeyi değiştirmez.** Bir alan için uyarı ancak iki tarafta da değer varsa:
  - `BARKOD_FARKLI` — stok kodu eşleşmeyi doğruluyorsa uyarılmaz (ör. çoklanan ilan, ADR-023).
  - `STOK_KODU_FARKLI` — barkod doğruluyorsa uyarılmaz (kanalın kendi kodu). Karşılaştırma büyük/küçük harf ve baş/son boşluk duyarsız.
  - `RENK_FARKLI`, `BEDEN_FARKLI` — `CatalogText.Normalize` ile (Türkçe büyük harf; bulanık değil).
  - `AD_BENZEMIYOR` — trigram Dice benzerliği < 0,30 (`ChannelCatalogMatcher.NameSimilarity`; öneriyle aynı ölçü, eşik bilinçli olarak öneri eşiği 0,60'tan düşük: kanal adları marka/renk/ölçü ekleri taşır).
  - Dayanak: `BARKOD` / `STOK_KODU` / `DIGER` (kodla doğrulanamadı: elle bağlama, model eşleşmesi veya kod bilinmiyor). `DIGER` tek başına uyarı değildir.
- **Kanal adı ve markası:** katalog çekimi, kanal ürün Excel'i ve eşleşmeyenden elle bağlamada `KanalVerisi`'ne `urunAdi` (300) ve `marka` (100) da yazılır (ürün verisi; kişisel veri yok). Migration yok.
  - Eski ilanlarda ad yoksa aynı kanalda barkod/kanal ürün koduyla eşleşen **bağlanmış** (`BAGLANDI`) eşleşmeyen kaydından okunur. İkisi de yoksa "kanal adı bilinmiyor".
  - Yan etki: alan eklendiği için bir sonraki katalog çekiminde mevcut ilanlar bir kez "güncellendi" sayılır (stok/eşleştirme değişmez).
- **Panel:** Stok Listesi'nde sekmeler: "Defter (tüm kanallar)" (önceki görünüm, değişmedi) + her kanal (`?kanal=KOD`). Kanal sekmesinde özet kutuları (kontrol et / sapma tıklanınca süzer), arama, "Yalnızca kontrol gerekenler", "Yalnızca stok sapmaları"; kontrol gereken satır sarı, sapmalı satır kırmızı; bağlı SKU'dan detay ekranına, eşleşmeyen sayısından Eşleştirme ekranına bağlantı.
- **Kapsam dışı / sonraki adım:** uyarıyı "doğru eşleşme" diye kalıcı onaylama (yeni tablo gerekir) ve kanal sekmesinden doğrudan eşleşme kaldırma/değiştirme. Düzeltme bugün Eşleştirme ekranından yapılır.

**Doğrulama:** birim (ListingMatchCheckTests 14), entegrasyon (ChannelStockListTests 3: yan yana görünüm + uyarılar + eski ilan adı; filtreler + eşleşmeyen sayısı + yetki + 404; elle bağlamada ad/marka yazılır ve eşleştirme korunur), Playwright (kanal sekmeleri senaryosu: Trendyol'da çoklanan ilan "Barkod farklı", Hepsiburada'da sapma, SKU detayına geçiş, Defter sekmesi değişmedi).

## ADR-036: Yanlış eşleştirme uyarısını "doğru eşleşme" olarak kalıcı onaylama
**Tarih:** 2026-11-19 (ürün sahibi isteği; ADR-035'in "sonraki adım" maddesi)
**Bağlam:** ADR-035 uyarıları her açılışta yeniden hesaplanıyordu. Doğru olduğu bilinen bir eşleşme (ör. farklı barkodlu çoklanan ilan) her seferinde "Kontrol et" listesinde kalıyor, gerçek sorunları gölgeliyordu.

**Karar:**
- **Tablo `ListingMatchApproval` (migration 0015):** ilan, onaylandığı andaki SKU, imza, uyarı kodları, onaylayan ve zaman.
  - **Kayıt silinmez:** geri alma (`GERI_ALINDI`) ya da yeniden onay (`YENILENDI`) `GeriAlan` / `GeriAlmaTarihi` / `KapanisNedeni` ile kapanır.
  - İlan başına en fazla bir açık onay (filtreli unique index). İlan silinirse geçmişi de silinir (`ON DELETE CASCADE`; ilan silmeyi engellemez).
- **Onay bir imzaya bağlı:** saf `ListingMatchCheck.Signature` (SHA-256). İmza şunlardan hesaplanır:
  - SKU Id;
  - iki taraftaki barkod, kod, stok kodu, ad, renk ve beden;
  - uyarı kodları (sırasız).

  Metinler `CatalogText.Normalize`'dan, kodlar kırpılarak geçer; yalnızca biçim farkı onayı bozmaz.
  - İmza **sunucuda**, ilanın bugünkü bilgisinden hesaplanır; istemciden alınmaz.
  - Bilgi sonradan değişirse onay **geçersiz** sayılır: ilan başka SKU'ya bağlanır, kanal adı değişir, yeni uyarı çıkar vb. Uyarı "önceki onay geçersiz" notuyla geri gelir. Bir onay sessizce başka bir eşleşmeyi örtmez.
- **Etki:** `NeedsReview = uyarı var VE geçerli onay yok`. "Kontrol et" sayısı ve filtresi onaylıları dışarıda bırakır; özete "doğru eşleşme olarak onaylı" sayısı eklendi.
  - Uyarılar silinmez; onaylı satırda soluk gösterilir, yanında "✓ Doğru eşleşme · kullanıcı · zaman" yazar.
  - **Eşleştirme (`ChannelListing.SkuId`) değişmez.**
- **Uçlar:** `POST` / `DELETE /api/listings/{id}/match-approval`, yetki `CatalogWrite` (Yönetici; eşleştirme yetkisiyle aynı). Diğer roller onayı görür.
  - Uyarısız ilan onaylanamaz (409). Aynı bilgi zaten onaylıysa yeni kayıt açılmaz. Eşzamanlı iki onay tek kayıt bırakır (`UPDLOCK` + unique index → 409).
  - Açık onay yoksa geri alma 404. Onay ve geri alma Serilog'a yazılır.
- **Panel:** kanal sekmesinde "Doğru eşleşme, onayla" ve "Onayı geri al" (onay penceresiyle). Geçersizleşmiş onay satırda ayrıca belirtilir.

**Doğrulama:** birim (ListingMatchSignatureTests 6; EmbeddedScriptsTests 0015'i içerir), entegrasyon (ListingMatchApprovalTests 3: onay listeden çıkarır, uyarılar kalır, tekrar onay tek kayıt, uyarısız 409, eşleştirme değişmez; bilgi değişince geçersiz, yeniden onay ve geri alma geçmişi korur, ikinci geri alma 404; roller 403, Destek görür, ilan silinince geçmiş silinir), Playwright (onayla → Kontrol et'ten çıkar → onaylayan görünür → geri al → döner; Destek onaylayamaz).

## ADR-037: Hepsiburada test ortamı (SIT) doğrulama aracı
**Tarih:** 2026-11-19 (ürün sahibi kararı: tek seferlik araç, yalnızca test ortamına yazma izni)
**Bağlam:** Hepsiburada canlı API bilgisini, test ortamında şu adımlar tamamlanınca veriyor: (1) ürün gönderimi ve trackingId, (2) envanterdeki ürünlerle stok ve fiyat gönderimi, (3) test siparişi oluşturup API ile listeleme ve paketleme. Uygulamada ürün oluşturma yoktu (kapsam dışı) ve deneme modunda kanala yazma kilitli.

**Karar:**
- **Uygulamaya özellik eklenmedi.** Tek seferlik komut satırı aracı `tools/PazaryeriHub.Tools.HbSit` yazıldı (`kontrol | katalog | listeleme | siparis | hepsi`, `--evet`, `--hbsku`).
  - Kimlik bilgisi panelde girilenden okunur: `PazaryeriHub_Test` veritabanı, aynı `IDataProtector` anahtarları. Veritabanına yazılmaz.
  - Her yazma adımı ne göndereceğini gösterip onay ister.
  - Rapor `hb-sit-rapor/<zaman>-<adım>/` klasörüne yazılır (`rapor.md` + ham `cagrilar.json`; Authorization yok). Klasör `.gitignore`'da.
- **Yeni test uçları:** `HepsiburadaSitClient` (adaptör projesinde). Uçlar ve alan adları resmi dokümandan; ayrıntılar `docs/kanal-api-notlari.md`.
  - **Prod kimlik bilgisiyle oluşturulamaz.** Her isteğin adresi `https://*-sit.hepsiburada.com` olmalı.
  - Yanıt şemaları dokümanda olmadığı için yanıtı yorumlamadan (`SitCall`) döndürür.
- **Mevcut adaptör yeniden kullanılır:** Stok/fiyat gönderimi + sonuç, listing sorgulama, sipariş çekme ve paketleme mevcut `HepsiburadaAdapter` ile yapılır; Hepsiburada'nın denetleyeceği kod budur.
  - Araçtaki `SitRecorder` handler'ı tüm istekleri kaydeder ve SIT dışı adresi gönderilmeden reddeder. Adaptör bu araçta canlıya gidemez.
- **Müşteri verisi:** Test siparişinde açıkça "PazaryeriHub Test" değerleri gönderilir; telefon ve e-posta gönderilmez.
- **SIT'te doğrulananlar** (adaptör yorumları güncellendi): stok ve fiyat POST'u `{"id"}` döndürüyor, sorgu `Ready → Done`. Listing yanıtında `productName` yok; Hepsiburada'dan kanal ürün adı gelmemesinin nedeni bu.
- **Test siparişinde servisin kendi hata mesajına göre:**
  - `OrderNumber` verilebilir; 10 haneli sayısal numara kabul edildi.
  - "ShippingMethodId" için tek deneme yapıldı (DeliveryOptionId = 1). Etkisiz kaldı ve araçtan kaldırıldı.
  - Alan tahmini yapılmadı (CLAUDE.md §10).

**Sonuç (2026-11-19):**
- ✅ Katalog: trackingId alındı. Ancak dokümanın test barkodları fastlisting'de "HB product not found by barcode".
- ✅ Stok ve fiyat gönderimi (Done), activate/deactivate, listing sorgulama.
- ⚠️ Toplu kilit kaldırma uç olarak çalışıyor (kilitsiz ürün için 1163).
- ❌ Envanter güncelleme 400 (deserialize).
- ❌ Test siparişi: "ShippingMethodId cannot be empty".
- Bu iki açık soru Hepsiburada'ya traceId'leriyle iletilecek.

**Doğrulama:** birim (HepsiburadaSitClientContractTests 14: adresler, gövde alanları, Basic Auth/User-Agent, Prod reddi, opsiyonel alanların yalnızca verilince gitmesi, hata gövdesinin rapora dönmesi), SIT'te gerçek çalıştırma raporları.

## ADR-038: Kanal verisi gerçek değilse ve eşleşme yoksa ekranlar nedenini söyler
**Tarih:** 2026-11-20 (kullanıcı geri bildirimi: Hepsiburada'dan çekilen ürünler Kanal Stoğu → Defter'e gelmiyor)
**Bağlam:**
- Hepsiburada kanalı test (SIT) ortamına bağlıydı. Çekilen 30 deneme ilanı defterle eşleşmedi (barkod yok, satıcı stok kodu farklı) ve kanalda 0 ilan oluştu.
- Kanal Stoğu → Defter boş tabloda "Önce 'Kanaldan şimdi çek' ile mutabakat çalıştırın" diyordu. Kullanıcı mutabakatı defalarca çalıştırdı; 0 ilanlı kanalda bu hiçbir şey değiştirmez.
- Katalog çekme raporu yalnızca "0 eşleşti" diyor, nedenini söylemiyordu.

**Karar** (yalnızca panel; sunucu ve eşleştirme kuralları değişmedi):
- `ChannelDataSourceNote`: kanal mock modda ise ("gerçek kanala istek gitmez") ya da API bilgisi test ortamına bağlıysa ("gelen ürünler deneme ilanı, deftere alınmamalı") uyarır.
  - Kaynak: `/api/channels/status` (mode, environment).
  - Eşleştirme → "Kanaldan ürün çek" ve Kanal Stoğu → Defter ekranlarında gösterilir.
- Kanal Stoğu → Defter: kanalın eşleşmiş ilanı yoksa (`/api/stock-overview/channels/{id}` → totalListings = 0) açık uyarı verir: kaç ürünün eşleşme beklediği ve Eşleştirme bağlantısı.
  - Boş tablo mesajı artık nedene göre: ilan yok / fark yok veya çekilmedi / mutabakat gerekli.
- Katalog raporu (`CatalogNoMatchHelp`): hiç eşleşme ve SKU oluşmadıysa nedenleri yazar (stok kodu uyuşmayan sayısı, barkodsuz sayısı ve barkodsuzdan SKU oluşturulmadığı) ve sonraki adımı söyler.

**Doğrulama:** Playwright (yeni senaryo; sunucu yanıtları `page.route` ile belirli hâle getirildi: Hepsiburada test ortamı, 0 ilan, 3 eşleşmeyen) ve tam set 17/17.

## ADR-039: Fiyat gönderiminde KDV kuralı gönderim anında da uygulanır
**Tarih:** 2026-11-20 (canlıya geçiş denetimi; `docs/canliya-gecis-denetimi.md` madde 2)
**Bağlam:**
- Fiyat yeteneğinin açılabilmesi adaptör kuralına bağlı: `PriceUpdateRule.CanEnable` (KDV dahil olduğu resmi dokümanda doğrulanmış olmalı; ADR-028).
- Bu kural yalnızca yetenek değiştirilirken, o an kayıtlı adaptöre göre kontrol ediliyordu. Kanal mock moddayken mock adaptör izin verdiği için yetenek açılabiliyordu.
- Kanal gerçek moda geçince yetenek AÇIK kalıyordu. Deneme veritabanında Hepsiburada FİYAT = AÇIK bulundu. Kilit kalksa KDV durumu belirsiz fiyatlar canlı kanala gidebilirdi.

**Karar:**
- `GuardedChannelAdapter.PushPriceAsync`, yetenek kapısından sonra gerçek adaptörün `DescribePriceUpdate()` kuralına da bakar.
- `CanEnable` false ise `ChannelWriteBlockedException` atılır ve istek kanala çıkmaz.
- Fiyat hattı (FiyatGonder) bu istisnayı zaten "engellendi" olarak işliyor: kayıtlar deneme sayılmadan bekler ve uyarı günlüğe yazılır. Stok ve diğer yazmalar etkilenmez.
- Yetenek kaydı otomatik değiştirilmez (kullanıcının kararı). Raporda KAPALI yapılması önerildi.

**Doğrulama:** birim testi (gerçek Hepsiburada adaptörü + açık kapı → fiyat isteği çıkmaz, stok çıkar), unit 368/368, entegrasyon 149/149.

## ADR-040: Son mutabakatta kanalda bulunamayan ilan "kanalda yok" olarak kaydedilir
**Tarih:** 2026-11-20 (canlıya geçiş denetimi; `docs/canliya-gecis-denetimi.md` madde 5)
**Bağlam:**
- Mutabakat, kanalda bulunan ilanlar için `ChannelStockSnapshot` satırı yazıyordu; bulunamayan ilan için hiçbir şey yazılmıyordu.
- Ekranlar ilanın EN SON snapshot'ını okuduğu için bulunamayan ilan eski (ör. iki gün önceki) kanal stoğunu güncel gibi gösteriyor, sapma da bu eski değerle hesaplanıyordu. Deneme veritabanında 2026-11-20 Trendyol mutabakatında 75 ilan bulunamadı; 19'u 18.11 tarihli değeri gösteriyordu (ör. `AxelH50100000` → kanalda 100).

**Karar:**
- Migration `0016_kanalda_yok.sql`: `ChannelStockSnapshot.KanalStok` NULL olabilir. NULL = "bu çekimde kanalda bulunamadı".
- Mutabakat, kanal stok sorgusunda dönmeyen her AKTIF ilan için de `KanalStok = NULL` satırı yazar. Böylece son snapshot her zaman son çekimin sonucudur; eski satırlar değişmez (yalnızca eklenir, geçmiş korunur).
- Anlamlar: snapshot yok → "hiç çekilmedi"; son snapshot NULL → "son çekimde kanalda bulunamadı"; son snapshot değerli → kanal stoğu.
- Sözleşmelere `MissingInChannel` (= `ChannelStock is null && FetchedAtUtc is not null`) eklendi: `ChannelStockCell` (Stok Listesi defter sekmesi, SKU detay), `ChannelStockListRow` (kanal sekmesi), `ReconciliationRow` (Senkron Merkezi), `IntakeChannelValue` (Kanal Stoğu → Defter). `HasDeviation`/`Deviation` zaten yalnızca değerli snapshot ile hesaplanıyordu; kanalda bulunamayan ilan sapma sayılmaz.
- Kanal sekmesi: "hiç çekilmemiş" sayısı yalnızca çekimi olmayanları sayar; yeni "son çekimde kanalda bulunamadı" sayısı (`MissingCount`).
- Kanal Stoğu → Defter: bulunamayan ilan değeri yok sayılır ve "eski çekim" anına katılmaz; mevcut `ILAN_DEGERI_YOK` / `KANALDA_DEGER_YOK` işaretleriyle tutarlıdır (artık eski değer `ESKI_CEKIM` ile önerilmez).

**Alternatifler:** Ayrı `KanaldaYok BIT` kolonu + KanalStok=0: sıfır değeri yanlış okunabilir ve sapma üretirdi. Ayrı tablo: her okuma sorgusunun değişmesi gerekirdi. NULL, mevcut "değer yok" anlamıyla örtüşüyor; okuma sorguları değişmedi.

**Kapsam dışı:** Kanala yazma yok; eşleştirme kuralları değişmedi. Senkron Merkezi "sapmalar" listesi değeri olmayan ilanları (önceden olduğu gibi) gösterir; artık "kanalda yok (son çekimde bulunamadı)" ile "çekilmedi" ayrılır.

**Doğrulama:** unit 372/372, entegrasyon 150/150, Playwright 17/17.

## ADR-041: Trendyol satış fiyatı KDV dahil kabul edildi (kullanıcı onayı)
**Tarih:** 2026-11-20
**Bağlam:**
- ADR-039 ile fiyat gönderimi `PriceUpdateRule.CanEnable = Supported && VatIncluded == true` koşuluna bağlandı. Trendyol resmi dokümanı `salePrice` için yalnızca "Ürün satış fiyatı TSF'dir" diyor; KDV dahil/hariç ifadesi yok (2026-11-20 yeniden okundu). Bu yüzden Trendyol FIYAT yeteneği açılamıyordu.
- Kullanıcının paylaştığı satıcı paneli ekran görüntüsünde "Trendyol Satış Fiyatı" ile "Müşterinin Gördüğü Fiyat" aynı değer.

**Karar:**
- Kullanıcı onayıyla (seçenek: "Panel kanıtıyla KDV dahil kabul et") `TrendyolAdapter.DescribePriceUpdate()` → `VatIncluded: true`. Not ve kaynak alanı kararın kullanıcı onayı olduğunu ve resmi dokümanda ifade bulunmadığını açıkça söyler.
- Beymen zaten KDV dahil (resmi dokümandan, ADR-028). Hepsiburada değişmedi: `CanEnable` false, FIYAT kapalı kalır.
- Yetenek açmak bu karardan ayrıdır: deneme modunda yazma kilidi (`Kanallar__YazmaKapali`) açıkken hiçbir fiyat kanala gitmez. Canlıya geçişte kilit kalkınca FIYAT=ACIK olan kanallara gider.

**Risk:** Trendyol ileride KDV hariç fiyat beklediğini belgelerse fiyatlar yanlış gider. Canlıda ilk gönderimden sonra satıcı panelinde birkaç ürünün "Müşterinin Gördüğü Fiyat"ı kontrol edilmeli (`docs/canliya-gecis-denetimi.md`).

**Doğrulama:** `PriceContractTests` ve `TrendyolAdapterContractTests` (Trendyol `CanEnable` true).

## ADR-042: İlan başına stok kuralı, kanal ürün görselleri ve tek yerden stok + fiyat düzenleme
**Tarih:** 2026-11-20
**Bağlam:** Kullanıcı (1) platform bazlı farklı stok göndermek, (2) yanlış eşleşmeyi görselle yakalamak için kanalların küçük resimlerini görmek, (3) pazaryeri panellerindeki gibi stok ve fiyatı tek yerden düzenlemek istedi. Eşleştirme ve tek defter ilkesi korunacak.

**Karar:**
- **Stok kuralı (kullanıcı seçimi: "İlan başına üst limit / sabit, defteri aşmaz"):** `ChannelListing.StokKuralTip` (`UST_LIMIT` | `SABIT` | NULL) + `StokKuralDeger`, `StokKuralDegistiren`, `StokKuralTarih` (migration `0017_ilan_stok_kurali_ve_gorsel.sql`; tip ve değer birlikte dolu/boş CHECK'i).
  - Saf hesap `StockAllocator.Allocate(..., ListingStockRule? rule)`: önce düşük stok eşiği; `UST_LIMIT` → MIN(pay ve kanal limitiyle hesaplanan, X); `SABIT` → MIN(X, satılabilir, kanal üst limiti) (kanal payının yerine geçer). Sonuç defterdeki satılabilir stoğu asla aşmaz; oversell koruması değişmez.
  - `SyncStore.ComputeTargetsAsync` kuralı uygular; böylece stok gönderimi, panel, mutabakat ve Kanal Stoğu → Defter aynı hedefi görür.
  - API: `GET/PUT /api/stock/listings/{id}/rule` (yazma Yönetici). Değişiklik aynı transaction'da `EslestirmeDegisti` outbox olayı yazar → mevcut OutboxIsle → STOK kuyruğu → StokGonder. Uç nokta kanala çağrı yapmaz.
  - Ayrı tablo yerine kolon: ilana özel fiyat (`FiyatOverride`) ile aynı yaklaşım; hedef sorgusuna join eklenmez.
- **Görseller:** `ChannelCatalogEntry/ChannelCatalogItem.ImageUrl`; yalnızca mutlak https ve ≤1000 karakter (`ListingChannelData.HttpsOrNull`).
  - Beymen: ürün listesi `images[].thumbnailPath` (yoksa `originalPath`), resmi örnek yanıttan.
  - Trendyol: `filterApprovedProducts` içeriğinin `images[].url`. Dokümandaki örnek göreli yol; gerçek yanıt mutlak https (`cdn.dsmcdn.com`; 2026-11-20 deneme modu katalog okuması, 200/200 ilanda görsel). Göreli adres yine de alınmaz.
  - Hepsiburada listing yanıtında görsel yok; gösterilmez.
  - Eşleşen ilanda `KanalVerisi.gorsel`, eşleşmeyen üründe `UnmatchedChannelItem.GorselUrl`. Görsel kanalın CDN'inden tarayıcıda yüklenir (referrer gönderilmez); sunucu görsel indirmez/saklamaz.
- **Panel:** Stok Listesi → kanal sekmesi her satırda kanal görseli, kanalda görünen fiyat, stok kuralı (Ortak havuz / Üst limit / Sabit) ve ilana özel fiyat; değişiklikler tek "Değişiklikleri kaydet" ile yazılır (fiyat mevcut `PUT /api/prices/listings/{id}/override`). Kanalın FIYAT yetenek durumu satırların üstünde yazar. "Eşleşmemiş kanal ürünlerini de göster" ile kanalın bağlanmamış ürünleri (görselli, salt okuma) aynı sekmede listelenir. SKU detayında bağlı ilanların görselleri yan yana; Stok Listesi defter sekmesinde ve Eşleştirme'de küçük resim.

**Kapsam dışı:** Kanala doğrudan yazan "hemen gönder" düğmesi eklenmedi; gönderim yetenek durumuna ve yazma kilidine tabi mevcut hattan gider (kullanıcı seçimi: "Canlı kurulumda, kilit kalkınca"). Eşleşmemiş ürün için stok/fiyat düzenlenmez (defter esas; önce SKU'ya bağlanmalı).

**Doğrulama:** unit 392/392 (yeni: ListingStockRuleTests 13, katalog görsel contract testleri, `gorsel` okuma), entegrasyon 151/151 (yeni: kural API → outbox → mock kanal, defteri aşmaz, geçersiz kural 400), Playwright 18/18 (yeni: kanal sekmesinde stok kuralı + fiyat kaydet/geri al).

## ADR-043: Kanaldaki ürün durumu gösterilir ve süzülür; ilanı pasif yapmaz

**Tarih:** 2026-11-21 · **Durum:** Kabul

**Bağlam:** Trendyol (`filterApprovedProducts`) ve Beymen (`GET /products`) katalog çekimi durum süzgeci uygulamaz; satış dışı ürünler de gelir. Deneme modu veritabanında Trendyol 584 ilanın 412'si "Satışta" (79 kara listede, 68 satışta değil, 24 arşiv, 1 kilitli), Beymen 194 ilanın 50'si "Satışta" (72 satışta değil, 36 stoğu tükendi, 25 seçilmedi, 11 revize bekliyor). Kanal durumu `KanalVerisi.kanalDurumu`'nda saklanıyordu ama kanal sekmesinde görünmüyordu; yalnızca Trendyol "Arşivlendi" ilanı PASIF yapıyordu.

**Karar:**
- `ChannelSaleState.Classify` (saf fonksiyon): ham metin → `SATISTA | STOK_YOK | SATIS_DISI | BILINMIYOR`. Yalnızca gerçek yanıtlarda görülen metinler tanınır (Türkçe karakter/büyük-küçük harf/boşluk farkı önemsiz); tanınmayan metin BILINMIYOR (tahmin yok).
- Kanal stok listesi satırında `ChannelStatus` (ham) + `SaleState`; listede kanalın tüm ilanları için `SaleStateCounts`. `GET /api/stock-overview/channels/{id}?saleState=…` ile süzülür; geçersiz değer 400.
- Panel: kanal sekmesinde "Kanaldaki durum" sütunu (rozet + kanalın metni) ve adetli "Kanaldaki durum" süzgeci.
- **İlan durumu (AKTIF/PASIF) ve stok gönderimi değişmez.** Satış dışı ilanı otomatik pasif yapmak, ürün yeniden satışa açıldığında stok/fiyat güncellemesini geciktirir; tam ortak havuzda satış dışı ilana gönderilen stok diğer kanalları azaltmaz.

**Doğrulama:** unit 410/410 (yeni: ChannelSaleStateTests), entegrasyon 152/152 (yeni: durum alanları, 4 süzgeç, adetler, geçersiz değer 400), Playwright ADR-035 senaryosuna durum sütunu + süzgeç adımları eklendi (18/18; ilk koşuda yeni adımdaki seçici düzeltildi).

## ADR-044: Eşleştirme listesinde kanaldaki ürün ↔ bağlı SKU yan yana

**Tarih:** 2026-11-21 · **Durum:** Kabul

**Bağlam:** Eşleştirme sayfasındaki "Eşleştirmeler" tablosu yalnızca kodları (SKU, kanal ürün kodu, barkod) gösteriyordu; hangi kanal ürününün hangi ürüne bağlandığını görmek için Stok Listesi kanal sekmesine gitmek gerekiyordu.

**Karar:**
- `GET /api/listings` satırı (`ListingDto`) kanalın bildirdiği ad, görsel, durum ile bağlı SKU'nun ürün adı, barkodu, renk/bedenini taşır; eşleşme dayanağı (`MatchBasis`) ve uyarı sayısı ADR-035'teki `ListingMatchCheck` ile hesaplanır (tek kural).
- Arama ürün adı ve kanaldaki ada da bakar. Kanaldaki ad `KanalVerisi` JSON'unda Türkçe karakterleri kaçışlı saklanabildiği için düz LIKE değil `JSON_VALUE(KanalVerisi,'$.urunAdi')` kullanılır.
- Panel: tablo "Kanaldaki ürün (görsel, ad, kodlar, varyant, kanal durumu, link) → Bağlı SKU (ürün adı, renk/beden, barkod) · Eşleşme" düzeninde. Uyarılı satırda "N uyarı, kontrol et" bağlantısı Stok Listesi kanal sekmesine götürür; onay ve ayrıntılı uyarılar orada kalır (tek yer).
- Eski ilanlarda (kanal verisinde ad yok) bu listede eşleşmeyenler kaydından yedek ad okunmaz; kanal sekmesi okur. Bilinçli: liste sayfalı EF sorgusu, ek join gerekmesin.

**Doğrulama:** unit 410/410, entegrasyon 152/152 (eşleştirme API testi: karşılaştırma alanları + kanaldaki ada göre arama), Playwright 19/19 (yeni: Eşleştirme listesi iki taraf + uyarıdan kanal sekmesine geçiş).

## ADR-045: Kendi bilgisayarına kurulum — Windows servisi, yerel ağ erişimi, kullanıcı yönetimi ve işlem geçmişi

**Bağlam:** Kullanıcı uygulamayı kendi bilgisayarına (geliştirme makinesinden ayrı bir cihaza) kendisi kuracak. İstenenler:
- Uygulama kendiliğinden çalışsın.
- Aynı yerel ağdaki diğer cihazlar panele erişebilsin.
- Yönetici kullanıcı ekleyip çıkarabilsin ve tüm kayıtları (kim ne yaptı) inceleyebilsin.

docker-compose canlı için uygun değildi (Developer SQL, sa hesabı, kalıcı olmayan anahtar ve loglar; canlıya geçiş denetimi madde 3 ve 9).

**Karar — çalışma biçimi:**
- **Windows servisi** (`Microsoft.Extensions.Hosting.WindowsServices`), exe değil.
  - Gecikmeli otomatik başlar, oturum açılmasa da çalışır, çökünce yeniden başlar (sc failure).
  - Panel aynı süreçten servis edilir; tek port.
- **Hesap:** Servis sanal hesapla çalışır (`NT SERVICE\PazaryeriHub`). Yönetici hakkı yoktur, parola saklanmaz.
- **SQL:** Varsayılan aynı bilgisayardaki SQL Server Express. Windows kimlik doğrulamasıyla bağlanır; servis hesabına yalnızca bu veritabanında `db_owner` verilir. SQL Server başka bir bilgisayardaysa SQL kullanıcısı kullanılır.
- **Klasörler:** Program `C:\Program Files\PazaryeriHub`. Ayarlar, şifreleme anahtarları ve loglar `C:\ProgramData\PazaryeriHub` altında; bu klasöre yalnızca SYSTEM, Yöneticiler ve servis erişir. Güncelleme program klasörünü değiştirir, veriler korunur.
- **Şifreleme anahtarları:** Kalıcı klasörde tutulur ve ayrıca **DPAPI (makine düzeyi)** ile korunur. Dosya kopyalansa da başka bilgisayarda açılmaz. Bu yüzden taşınan veritabanında kanal API bilgileri yeniden girilir.
- **Ayar dosyası:** `ayarlar.json` yalnızca kurulu çalışmada okunur (servis, `--kurulum`, `--kurulu`). Geliştirme ve deneme modu bu dosyayı okumaz; bu makineye kurulmuş bir kopya varsa deneme modu ona bağlanmaz. Kurulu çalışmada içerik kökü ve çalışma klasörü program klasörüdür (servisler System32'de başlar).
- **Kurulum adımı:** `PazaryeriHub.Api.exe --kurulum` tek seferliktir: veritabanını oluşturur, şemayı günceller, ilk Yöneticiyi ekler ve çıkar. Hata olursa çıkış kodu 1 döner. İlk Yöneticinin parolası yalnızca o sürecin ortam değişkeninde bulunur.
- **Ağ:** Kestrel `http://*:<port>` (varsayılan 5090) dinler. Güvenlik duvarı kuralı yalnızca **Özel ve Etki alanı** profillerinde açılır; Genel ağda kurulum uyarı verir.
  - Yerel ağda HTTP kullanılır, çerez yine HttpOnly ve SameSite=Strict.
  - HTTPS sertifikası yerel ağda her cihazda uyarı çıkaracağı için şimdilik yok; internete açılmaması kılavuzda yazılı.
- **Güvenli başlangıç:** Kurulumdan sonra kanala yazma KİLİTLİ, tüm kanallar Mock. `kanal-ayari.ps1` kanal modunu ve yazma kilidini değiştirip servisi yeniden başlatır (adım adım canlıya geçiş; KURULUM.md §5). softcotton gerçek moda alınamaz.
- **Paket:** `kurulum/paket-olustur.ps1` self-contained win-x64 yayınlar (hedefte .NET gerekmez), paneli, kur/kaldır/kanal-ayarı betiklerini ve KURULUM.md'yi tek zip'te toplar. Pakette parola, bağlantı bilgisi veya anahtar yoktur.
  - Betikler BOM'lu UTF-8'dir: Windows PowerShell 5.1 BOM'suz dosyadaki Türkçe karakterleri bozuk okuyor.

**Karar — kullanıcı yönetimi:**
- Kullanıcı **silme** eklendi: `DELETE /api/users/{id}`. Kayıtlardaki kullanıcı adı metin olduğu için geçmiş bozulmaz.
- **Oturum her istekte doğrulanır** (çerez `OnValidatePrincipal`):
  - Silinen veya pasife alınan kullanıcının açık oturumu hemen geçersiz olur.
  - Rol değişikliği beklemeden uygulanır.
  - Maliyet: istek başına birincil anahtarla tek okuma. Az kullanıcılı yerel kurulumda önemsiz.
- **Yönetici kendini koruyamaz hale getiremez:**
  - Yönetici kendini silemez, pasife alamaz, yönetici yetkisini kaldıramaz.
  - **Son aktif yönetici** kaldırılamaz (serializable transaction + UPDLOCK).

**Karar — işlem geçmişi:**
- Yeni tablo `dbo.AuditLog` (migration 0018). Kaydedilenler:
  - /api altındaki her yazma isteği (GET dışı): kullanıcı, rol, metot, yol, HTTP sonucu, IP, süre.
  - Başarılı ve başarısız girişler (denenen kullanıcı adıyla), çıkışlar.
- **Kaydedilmeyenler:** İstek gövdesi ve sorgu dizesi (parola, API anahtarı, müşteri verisi olabilir).
- Kayıt yazılamazsa asıl işlem bozulmaz; yalnızca log düşülür.
- `GET /api/audit` yalnızca Yönetici; kullanıcı, tip, tarih, yol ve "yalnızca hatalı" filtreleri var.
- Panelde "İşlem Geçmişi" menüsü yalnızca Yöneticiye görünür. Yol, anlaşılır işlem adına çevrilir (ör. "Kullanıcılar · silme").

**Bilinen eksikler:**
- HTTPS yok (yerel ağ HTTP).
- Otomatik veritabanı yedeği yok; SQL Express'te Agent olmadığı için kılavuzda elle veya Görev Zamanlayıcı öneriliyor.
- İşlem geçmişinin saklama süresi sınırsız (tablo küçük satırlı).
- `kur.ps1` ve `kaldir.ps1` geliştirme makinesinde **servis olarak kurulup denenmedi**: bu makinede servis ve güvenlik duvarı değiştirilmedi. Yayınlanan paket bu makinede geçici ayar dosyası ve geçici veritabanıyla konsol modunda denendi.

**Doğrulama:**
- Otomatik testler: unit 414/414, entegrasyon 156/156 (yeni 4 test). Playwright: önceki 19 senaryo tam çalıştırmada geçti; yeni senaryo (kullanıcı ekle/sil, açık oturumun kapanması, İşlem Geçmişi) düzeltmeden sonra ayrıca çalıştırıldı ve geçti.
- Paket denemesi (`--kurulum` + `--kurulu`), hepsi doğrulandı:
  - Kurulum adımı geçici veritabanı oluşturdu, şemayı kurdu, ilk Yöneticiyi ekledi; çıkış kodu 0.
  - `/health` localhost'ta ve makinenin ağ IP'sinde 200 döndü.
  - Panel açıldı, giriş 200, İşlem Geçmişi giriş kaydını gösterdi.
  - Anahtar dosyası DPAPI ile şifreli (düz anahtar yok); log yapılandırılan klasöre yazıldı.
  - Geçici veritabanı ve klasör sonra silindi.


## ADR-046: Eski bilgisayardan veri taşıma (yedekle → kur.ps1 -VeriYedegi)

**Tarih:** 2026-11-22
**Bağlam:** Kullanıcı deneme modunda girdiği tüm veriyi (ürünler, eşleştirmeler, stok defteri, siparişler, kullanıcılar) kendi kuracağı bilgisayara taşımak istiyor. Bu veri geliştirme bilgisayarında `PazaryeriHub_Test` veritabanında (SQL Server 2025 Express, sürüm 17), SQL kullanıcısıyla (`ms`) erişiliyor. Hedefte servis Windows kimliğiyle bağlanıyor (ADR-045). Kanal API bilgileri bu bilgisayarın Data Protection anahtarıyla şifreli.

**Karar:**
- Taşıma birimi **tam veritabanı yedeği** (.bak, COPY_ONLY + CHECKSUM, RESTORE VERIFYONLY ile doğrulanır). Tablo tablo dışa aktarma yerine seçildi: şema, indeksler, saklı yordamlar, veritabanı ayarları (READ_COMMITTED_SNAPSHOT), Hangfire tabloları ve DbUp sürüm tablosu birebir gelir; kimlik (IDENTITY) değerleri ve ilişkiler bozulmaz.
- `kurulum/veri-yedekle.ps1` (eski bilgisayar): yedeği sunucunun yedek klasörüne alır, `dagitim/`e kopyalar, kaynak sürümü yazar. Kaynağa yazmaz.
- `kurulum/veri-geri-yukle.ps1` (pakette), `kur.ps1 -VeriYedegi` ile yalnızca **ilk kurulumda**, `--kurulum` adımından (boş veritabanı oluşturmadan) önce çağrılır:
  - Aynı adlı veritabanı varsa durur; üzerine yazmaz.
  - Yedek sunucudan yeni sürümdense anlaşılır hata verir (SQL Server eski sürüme geri yüklemez).
  - Dosyaları hedef sunucunun varsayılan veri/log klasörlerine taşır.
  - Temizlik (yalnızca yeni kopyada): eşleşen login'i olmayan eski SQL kullanıcıları çıkarılır; `Channel.KimlikSifreli` NULL yapılır (burada çözülemez, panelden yeniden girilir); eski `HangFire.Server` kayıtları silinir; veritabanı sahibi `sa` yapılır.
- **Tohum kullanıcı, kurulum modunda:** taşınan veritabanında kullanıcı olduğu için eski kural ("üretimde yalnızca hiç kullanıcı yoksa") `kur.ps1`'de girilen Yöneticiyi atlıyordu; kullanıcı deneme modunun rastgele parolalı `demo-yonetici`siyle kilitli kalırdı. Artık `--kurulum` modunda eksik tohum kullanıcı **eklenir**, var olanın parolasına **dokunulmaz**. Normal üretim başlangıcı değişmedi.
- SQL kullanıcı/parola taşınmaz: hedefte Windows kimliği (`NT SERVICE\PazaryeriHub`) kullanılır, yetkiyi `kur.ps1` verir.

**Sonuç / bilinen sınırlar:**
- Hedefe en az kaynak sürüm (şu an SQL Server 2025 Express) kurulmalı; kılavuz güncellendi.
- Geri yükleme SQL Server hedefle aynı bilgisayardayken çalışır.
- Deneme veritabanındaki deneme kayıtları (ör. HB SIT siparişleri, `demo-yonetici`) de taşınır; panelden temizlenebilir.

**Doğrulama:** Entegrasyon testleri `SetupSeedUserTests` (2 test: normal üretim başlangıcı eklemez; kurulum modu ekler, var olanın parolasını değiştirmez). Bu bilgisayarda: `PazaryeriHub_Test` yedeklendi (39 MB) → `PazaryeriHub_AktarimDeneme` olarak geri yüklendi (184 ürün, 740 SKU, 6 sipariş, 3 kanalın API bilgisi temizlendi); ikinci geri yükleme reddedildi; yeni paketin `--kurulum`'u şemayı güncelledi, `yonetici`yi ekledi, giriş 200, sipariş listesi 200. Deneme veritabanı silindi. Eşleşmeyen SQL kullanıcısının çıkarılması burada denenemedi (aynı sunucuda `ms` login'i var).

## ADR-047: Unutulan yönetici parolası (kur.ps1 -YoneticiParolaSifirla)

**Tarih:** 2026-11-23
**Bağlam:** Kurulu sistemde yönetici parolası unutulursa (veya taşınan veritabanında `yonetici` hiç oluşmadıysa) panelden çıkış yolu yoktu: üretimde tohum kullanıcı yalnızca eksikse eklenir, var olanın parolasına dokunulmaz (ADR-046). Bu bilgisayardaki kurulumda ayar klasörü silinmeden yeniden çalıştırılan kur.ps1 "güncelleme" sayıldı ve `yonetici` hiç eklenmedi.

**Karar:**
- `kur.ps1 -YoneticiParolaSifirla [-YoneticiKullanici <ad>]`: yeni parolayı iki kez sorar (eşleşmezse hiçbir şey değişmez) ve kurulum adımına (`--kurulum`) `Kimlik:SeedPasswordReset=true` ile tohum kullanıcıyı verir.
- `UserService.SeedAsync`: sıfırlama bayrağı YALNIZCA kurulum modunda geçerlidir; ekle-veya-güncelle uygulanır (parola, Aktif=1, kilit ve hatalı giriş sayacı sıfır, rol = tohumdaki rol, yani Yönetici). Normal servis başlangıcında bayrak yok sayılır.
- Yetki modeli: sıfırlama yalnızca makinenin Windows yöneticisi tarafından yapılabilir (betik yönetici ister, parola ortam değişkeniyle yalnızca kurulum sürecine verilir ve hemen silinir). Panelde "parolamı unuttum" yok; yerel ağ uygulamasında e-posta doğrulaması gibi bir kanal yok.

**Doğrulama:** Entegrasyon: `SetupSeedUserTests` 4/4 (sıfırlama parolayı değiştirir, pasif/kilitli/Destek rolündeki hesabı açıp Yönetici yapar; bayrak normal başlangıçta yok sayılır). Yayınlanan paketle (taşınan veritabanı kopyasında): `yonetici` yokken sıfırlama oluşturdu (giriş 200); bayraksız kurulum parolayı değiştirmedi; yeniden sıfırlamada eski parola 401, yeni 200. `kur.ps1`'in kendisi yönetici yetkisi gerektirdiği için bu oturumda çalıştırılamadı.

## ADR-048: Stok gönderiminde kanal bazında onay

**Tarih:** 2026-11-23
**Bağlam:** Canlı kullanımda "Kanal Stoğu → Defter" aktarımı 14 SKU'nun defterini düşürdü ve bu değerler hemen Trendyol'a gönderildi; kullanıcı bu değişikliği istememişti. STOK yeteneği varsayılan ACIK'tı; panelde "Deneme (tek tek onay)" seçeneği stokta onay İSTEMİYORDU (yalnızca kanal komutlarında uygulanıyordu). Kullanıcı isteği: stok değiştirileceği zaman hangi ürünün hangi kanalda kaça değişeceği ayrıntılı gösterilsin, onay istensin; her pazaryerinin gönderimi kutucukla ayrı seçilsin.

**Karar:**
- STOK yeteneği `DENEME` artık gerçekten **"Onaylı"**: StokGonder yalnızca `SyncQueue.OnaylananDeger` dolu kayıtları talep eder ve **onaylanan değeri** gönderir. Gönderim anında hesaplanan değer onaylanandan farklıysa onay düşer, kayıt yeniden onaya girer (deneme sayılmaz).
- `KAPALI`: kayıt talep edilmez, deneme sayılmaz, HATA'ya düşmez (eskiden yazma kapısında hata sayılıp 5 denemede HATA oluyordu). Yazma kapısı engeli (global kilit) de deneme sayılmadan bekletilir (fiyat işiyle aynı).
- Varsayılan STOK = `DENEME`; migration 0019 mevcut `ACIK` kayıtları `DENEME` yapar (kullanıcı isteği). Kanal bazında yeniden `ACIK` yapılabilir.
- Onay ekranı (Senkron Merkezi): kanal kutucukları + sayılar, ürün resmi, SKU/barkod, kanalda şu an (son mutabakat), son gönderilen, gönderilecek, fark; satır seçimi; "Yalnızca azalanlar"; gönder/reddet öncesi kanal bazında özet (azalış/artış/ilk gönderim/0'a inen). Onay, ekranda görülen değerle yapılır; o arada değiştiyse kayıt atlanır.
- Reddedilen kayıt `IPTAL` (yeni durum); stok yeniden değişince yeni kayıt açılır.
- "Kanal Stoğu → Defter" onay penceresi artık kaç SKU'nun azalıp artacağını, 0'a inenleri ve örnekleri gösterir.
- Uç noktalar: `GET /api/sync/stock-approvals`, `POST .../approve` ve `.../reject` (Yönetici). Özet satırına `AwaitingApproval` eklendi.

**Sonuç / ödünleşim:** Onaylı kanalda bir satış sonrası diğer kanalların stoğu da onaya kadar eski kalır (aşırı satış riski). Bu, kullanıcının "her stok değişikliği onayla" isteğinin bilinçli sonucudur; kanal bazında "Açık" seçeneği duruyor.

**Doğrulama:** Entegrasyon: `SyncPipelineTests` içinde 5 yeni test (onaysız gönderilmez, onaylanan değer gider; onay sonrası değişince yeniden onay; red; KAPALI'da deneme sayılmaz; yalnızca Yönetici) + varsayılan yetenek testi güncellendi; entegrasyon 165/165, birim 416/416. Playwright: yeni senaryo (kanal kutucuğu, satır seçimi, özetli onay) geçti; "Hatalı senkron" senaryosu demo verideki yeni kayıt nedeniyle kanal süzgeciyle güncellendi. ADR-036 senaryosu (çıkış → Destek girişi) iki çalıştırmada başarısız oldu (önceki commit'te de); sonraki tam çalıştırmada (ADR-049) 22/22 geçti → kararsız (flaky), izlenecek.

## ADR-049: Ürün bazında kanal fiyatları (aynı SKU, kanal başına ayrı fiyat)

**Tarih:** 2026-11-23
**Bağlam:** Fiyat ekranı yalnızca kanal bazında önizleme gösteriyordu; aynı SKU'nun farklı pazaryerlerindeki fiyatlarını görmek ve kanal başına ayrı güncellemek için her kanalı ayrı ayrı açıp "Elle fiyat" istemiyle (prompt) tek tek girmek gerekiyordu. Kullanıcı: "aynı SKU ile eşleşmiş ürünün farklı platformlardaki fiyatlarını farklı farklı güncelleyebileyim", "küçük notlar ve detaylar".

**Karar:**
- Yeni model yok: kanal başına fiyat, mevcut **ilan elle fiyatı** (`ChannelListing.FiyatOverride`, ADR-028) ile tutulur; hesap/gönderim/onay/eşik/KDV kuralları aynen geçerli.
- `GET /api/prices/skus?search=` → SKU başına: resim, SKU fiyatı (satış/liste) ve her kanal ilanı için önizleme satırı (kanalda görünen, gidecek, son gönderilen, elle/taban, durum, sorun), kanalın FIYAT yeteneği ve ilan durumu. Yalnızca eşleşmesi olan SKU'lar; sayfa 25.
- `PUT /api/prices/listings/overrides` (Yönetici): birden çok ilan fiyatı tek transaction'da; bir satır geçersizse hiçbiri yazılmaz. Her değişiklik PriceHistory (ILAN_FIYATI) + FiyatDegisti outbox olayıyla (tekli uçla aynı).
- Panel: "Ürün bazında kanal fiyatları" tablosu (kanal sütunları, kutu içi düzenleme, değişen kutu sarı, geçersiz kırmızı, "Değişiklikleri kaydet (N)" + SKU/kanal "eski → yeni" özeti, fiyat gönderimi kapalı kanalların uyarısı); "Nasıl çalışır?" notları ve hücre ipuçları; kanal önizleme tablosuna ürün resmi.
- Önizleme satırı hesabı tek yardımcıya (`BuildRow`) alındı; kanal önizlemesi ve ürün görünümü aynı mantığı kullanır.

**Not:** Kurulu sistemde Trendyol ve Beymen FIYAT yeteneği "Açık" (deneme veritabanından taşındı); elle girilen fiyat bu kanallara eşik içindeyse onaysız gider. Kullanıcıya Onaylı/Kapalı önerildi.

**Doğrulama:** Entegrasyon (`PricePipelineTests`): iki kanalda ayrı fiyat, tek kanalın elle fiyatını kaldırma, geçmiş kayıtları; geçersiz satırda hiçbirinin yazılmaması; Depo 403. Entegrasyon 167/167. Playwright: yeni senaryo + fiyat/tüm ekranlar senaryoları geçti. Deneme modunda tarayıcıda görsel kontrol yapıldı.

## ADR-050: Fiyat & Stok Düzenle: eşleşen ürünün fiyatı ve stoğu tek satırda

**Tarih:** 2026-11-25 · **Durum:** Kabul

**Bağlam:** Kullanıcı, Trendyol satıcı panelindeki gibi her ürünü tek satırda (görsel, ad, kod, barkod, renk, durum, fiyat, stok) görüp eşleşen tüm ürünlerin fiyat ve stoğunu tek yerden düzenlemek istedi. Seçimleri: "Ürün başına tek satır" (kanallar yan yana) ve stok kutusu = "Defter stoğu" (kanala özel stok değil; tek defter ilkesi korunur).

**Karar:**
- ADR-049'daki ürün bazında tablo genişletildi; yeni menü sayfası **Fiyat & Stok Düzenle** (`/urun-duzenle`) aynı bileşeni tam sayfa gösterir, Fiyat sayfasındaki bölüm de aynıdır.
- `GET /api/prices/skus` satırı ek olarak: renk/beden, defter stoğu (Fiziksel, Rezerve, Güvenlik, Satılabilir; tüm depolar toplamı), okuma anı (`ReadAtUtc`, veritabanı saati); her kanal ilanı için kanalın bildirdiği durum (`kanalDurumu`), son mutabakattaki kanal stoğu, gidecek stok (`SyncStore.ComputeTargetsAsync`: pay, kanal limiti, ilan kuralı), son gönderilen stok ve STOK yetenek durumu.
- `POST /api/stock/available` (Yönetici): satırlar (SkuId, hedef satılabilir, görülen Fiziksel). Kanal Stoğu → Defter'in güvenli yazımı (`usp_KanalStokAl`) yeniden kullanılır, kaynak `ELLE_STOK`: Fiziksel = Rezerve + Güvenlik + hedef; okumadan sonra Fiziksel değiştiyse satır reddedilir, sonraki rezervasyonlar hedeften düşülür; her satır kendi transaction'ında (SAYIM hareketi + outbox). Uygulanan SKU'ların aktif ilanları kuyruğa alınır; gönderim kanalın STOK yeteneğine tabidir (Onaylı → Senkron Merkezi onayı). Ortak döngü `ChannelIntakeService.ApplyRowsAsync`'e alındı.
- Panel: "Stok (defter)" sütunu (satılabilir kutusu, yeni fiziksel önizlemesi), kanal hücresinde durum ve "kanalda · gidecek" stok (farklıysa turuncu); tek "Değişiklikleri kaydet (N)" fiyat + stok için STOK/FİYAT bölümlü "eski → yeni" özeti gösterir.

**Kapsam dışı:** Komisyon, buybox, "son 10 günün en düşük fiyatı" gibi pazaryeri panel alanları: bu verileri çeken doğrulanmış uç nokta kullanılmıyor (uydurma yok). Kanala özel stok bu ekranda yok; gerekirse ADR-042 ilan stok kuralı (Stok Listesi → kanal sekmesi).

**Doğrulama:** Entegrasyon (`SyncPipelineTests`): satır stok/kanal alanları; elle stok → defter (SAYIM, ELLE_STOK) → mock kanal, fiziksel değişen satır reddedilir, Depo 403. Birim 416/416, entegrasyon 169/169. Playwright: yeni senaryo; tam çalıştırmada 22/23 ("Sipariş listesi" senaryosundaki yarış: filtre sonrası eski satır tıklanıyordu; test doğru satırı bekleyecek şekilde düzeltildi, tekrarlı çalıştırmada geçti). Deneme modunda gerçek katalog verisiyle görsel kontrol.


## ADR-051: Fiyat & Stok Düzenle'de kanal bazlı stok; Fiyat sekmesi "Fiyat Kuralları" olarak sadeleşti

**Tarih:** 2026-11-25 · **Durum:** Kabul

**Bağlam:** Kullanıcı, ortak (defter) stoğun yanında aynı ekrandan kanal bazlı stok da değiştirmek istedi. Ayrıca Fiyat sekmesindeki ürün tablosu Fiyat & Stok Düzenle ile aynı olduğu için menünün son kullanıcı için sadeleşmesini istedi.

**Karar:**
- Kanal bazlı stok, ADR-042'deki ilan stok kuralının **SABIT** türüyle yapılır (yeni tablo/uç nokta yok): kanal hücresinde "kanal stoğu" kutusu; boş = ortak stok (kural kaldırılır), sayı = o ilana sabit adet. Gönderilen değer `MIN(adet, satılabilir, kanal üst limiti)`: defteri aşmaz, fazla satış koruması değişmez. Defter değişmez.
- `GET /api/prices/skus` kanal satırı `StockRuleType`/`StockRuleValue` taşır. UST_LIMIT kuralı varsa hücrede "en fazla N" yazar; kutuya sayı yazılırsa SABIT ile değişir (özette gösterilir).
- Kaydetme, mevcut `PUT /api/stock/listings/{id}/rule` ile ilan başına yapılır (her biri kendi transaction'ı + EslestirmeDegisti outbox). Özet "KANAL STOĞU" bölümü eski → yeni ve defteri aşan adette uyarı gösterir.
- Menü: "Fiyat & Stok Düzenle" Stok Listesi'nin altına; "Fiyat" → **Fiyat Kuralları** (onay bekleyen fiyatlar, kanal kuralları/önizleme, SKU ana fiyatı, ayarlar, geçmiş). Ürün bazında tablo Fiyat sayfasından kaldırıldı (bileşen yalnızca Fiyat & Stok Düzenle'de); sayfa başında yönlendirme notu. Ekrandaki notlar kısa madde listesine indirildi.

**Kapsam dışı:** Stok Listesi → kanal sekmesindeki kural/fiyat düzenleme yerinde bırakıldı (silinmedi); ileride sadeleştirme adayı.

**Doğrulama:** Entegrasyon: satırda kural alanları ve SABIT sonrası gidecek değer; birim 416/416, entegrasyon 169/169. Playwright: ADR-050 senaryosuna kanal stoğu (Trendyol 0 → diğer kanal ortak, defter değişmez → geri al); menü adları testlerde güncellendi. Tam çalıştırma makine yavaşken 19/23; menü adı kaynaklı "Tüm ekranlar" düzeltildi, Sipariş listesi ve ADR-035 tekrarda geçti; ADR-036 (Eşleştirme, bu değişiklikle ilgisiz) hâlâ kararsız. Deneme modunda görsel kontrol.


## ADR-052: Stok Listesi kanal sekmesi stok ve fiyatta salt okuma

**Tarih:** 2026-11-25 · **Durum:** Kabul

**Bağlam:** Stok/fiyat düzenleme hem Stok Listesi → kanal sekmesinde (ADR-042) hem Fiyat & Stok Düzenle'de (ADR-050/051) vardı. Kullanıcı kanal sekmesinin yalnızca görüntüleme olmasını istedi.

**Karar:**
- Kanal sekmesindeki "Stok ve fiyat" sütunu salt okuma özeti oldu (`ListingSummary`): stok ayarı (ortak stok / kanala özel N / en fazla N), gidecek ve son gönderilen stok, fiyat (elle rozeti, kanalda görünen, son gönderilen). Kural/fiyat alanları, kaydet çubuğu ve taslak mantığı kaldırıldı.
- Satırdaki "Düzenle →" bağlantısı `/urun-duzenle?ara=<SKU>` açar; Fiyat & Stok Düzenle `ara` parametresiyle aranmış başlar.
- "Doğru eşleşme, onayla" / "Onayı geri al" yerinde kaldı: stok/fiyat değil eşleşme kontrolüdür (ADR-036) ve başka ekranda yoktur.
- API değişmedi (`PUT /api/stock/listings/{id}/rule`, `PUT /api/prices/listings/{id}/override` Fiyat & Stok Düzenle ve diğer istemciler için duruyor).

**Doğrulama:** TypeScript derlemesi temiz. Playwright: ADR-042 senaryosu salt okuma + "Düzenle" yönlendirmesi olarak yeniden yazıldı ve geçti; ADR-035, ADR-050 geçti; "Tüm ekranlar" ilk koşuda Raporlar yavaş yüklendiği için zaman aşımına uğradı, tekrarda geçti. Arka uç değişmediği için birim/entegrasyon yeniden çalıştırılmadı.


## ADR-053: Şüpheli eşleşmeleri öne çıkarma: ebat karşılaştırması, "İncelenecek eşleşmeler" süzgeci, varyant değişince yeniden inceleme

**Tarih:** 2026-11-25 · **Durum:** Kabul

**Bağlam:** Eşleştirme ekranında kanal ürünü ile iç SKU yan yana duruyordu (ADR-044), ama farkı fark etmek operatöre kalıyordu. Ürün sahibinin istekleri:
- İncelenecek eşleşmeler için bir süzgeç.
- İç SKU 70×140 Antrasit, kanal varyantı 50×90 Antrasit ise satırda "Ebat farklı" yazsın.
- Alan boşsa "Ebat bilgisi eksik" yazsın.
- Önceden onaylanmış ilanın barkodu veya varyantı sonradan değişirse ilan yeniden incelemeye düşsün.

Önceki durum: beden metin olarak karşılaştırılıyordu, bu yüzden "70x140" ile "70 x 140 cm" farklı sayılıyordu. Alan boşsa uyarı çıkmıyordu. Kanal varyant metni onay imzasında yoktu.

**Karar:**
- **Ebat okuma: saf `SizeDimensions.Parse`.** "70x140", "70 × 140 cm", "140*70", "200x220x30" ve "1,5x2" biçimlerini okur.
  - Sayılar küçükten büyüğe sıralanır, yani "140x70" = "70x140".
  - Metinde birden fazla farklı ebat varsa (ör. set ürünü) ebat okunmaz, null döner.
- **Ebatın okunduğu yer, sırayla:**
  - Kanal tarafı: beden/ebat alanı, sonra kanal varyant metni ("Ebat: 50x90"), sonra kanal ürün adı.
  - SKU tarafı: beden alanı, sonra ürün adı.
- **Uyarılar (`ListingMatchCheck`):**
  - `EBAT_FARKLI` "Ebat farklı": iki tarafta da ebat var ve sayılar farklı.
  - `BEDEN_FARKLI`: ebat ölçüsü olmayan bedenler (S/M/L…) önceki gibi metin olarak karşılaştırılır.
  - `EBAT_EKSIK` "Ebat bilgisi eksik": bir tarafta beden/ebat var, diğerinde hiç yok.
  - Kanalın ürün bilgisi hiç okunmamışsa (ad, varyant ve beden yok; ör. elle girilmiş ilan) kanal tarafı "bilinmiyor" sayılır ve eksik denmez. Böylece kataloğu çekilmemiş ilanlar listeyi doldurmaz.
- **Onay imzası v2:** Kanal varyant metni de imzaya girer (`CatalogText.Normalize`; yalnızca biçim farkı imzayı bozmaz). Barkod zaten imzadaydı.
  - Onaydan sonra barkod veya varyant değişirse onay geçersiz olur ve ilan yeniden incelemeye düşer.
  - **Yan etki:** Bu sürümden önceki onayların imzası v1'dir. Uyarısı süren ve daha önce onaylanmış ilanlar bir kez "önceki onay geçersiz" olarak görünür, yeniden onaylanmaları gerekir. Bu bilinçli bir seçim: eski onay varyantı kapsamadığı için sessizce geçerli sayılmaz.
- **`GET /api/listings?onlyReview=true`:** Satır uyarıları, onay ve `NeedsReview` kanal sekmesiyle aynı okuyucudan gelir. Kanal adı yedeği ve onay imzası iki ekranda tutarlıdır.
  - Satırlara `warnings`, `approval` ve `needsReview` eklendi.
  - Süzgeç açıkken aramaya uyan ilanların tamamı okunur, bellekte süzülür ve sonra sayfalanır, çünkü uyarılar SQL'de hesaplanmıyor.
  - Süzgeç kapalıyken yalnızca sayfadaki ilanlar okunur.
- **Panel (Eşleştirme):**
  - "İncelenecek eşleşmeler" kutusu ve "N eşleşme incelenmeli" notu.
  - Satırda uyarı etiketleri: Ebat farklı, Ebat bilgisi eksik, Barkod farklı vb.
  - "✓ Doğru eşleşme" rozeti ve "Önceki onay geçersiz" notu.
  - "İncele / onayla →" bağlantısı Stok Listesi kanal sekmesini SKU aranmış ve "Yalnızca kontrol gerekenler" açık hâlde getirir (`?ara=`). Onay ve geri alma orada kalır (ADR-036).
- **Eşleştirme (`ChannelListing.SkuId`) değişmez;** uyarılar yalnızca bilgi verir.

**Doğrulama:**
- **Birim:** `ListingSizeCheckTests` 22 test: ebat okuma, farklı/aynı ebat, varyanttan ve addan okuma, eksik, bilinmeyen kanal, metin beden, varyantla imza. Ayrıca `ListingMatchCheckTests` güncellendi (70×140 ↔ 50×90 artık `EBAT_FARKLI`). Birim testleri 438/438.
- **Entegrasyon:** `ListingReviewFilterTests` 2 test: süzgeç farklı ve eksik ebatlı ilanları getirir; onaylanan çıkar; varyant veya barkod değişince geri döner. `ListingMatchApprovalTests` test verisine kanal bedeni eklendi, aksi hâlde "sorunsuz" ilan yeni kuralla "Ebat bilgisi eksik" alıyordu. Entegrasyon 171/171.
- **Playwright:** ADR-044 senaryosu süzgeç ve "İncele / onayla" akışıyla güncellendi; ADR-035 ve ADR-036 ile birlikte geçti.
- **Deneme veritabanı (gerçek katalog, salt okunur sayım):** Trendyol 584 ilan, Beymen 194 ilan. Yeni ebat kuralları ek uyarı üretmedi; uyarılı ilanlar Trendyol'da 2, Beymen'de 1 (önceki kurallar).

## ADR-054: Hepsiburada API bilgisi alanları netleştirildi (kullanıcı adı = Mağaza ID, User-Agent = entegratör adı)

**Tarih:** 2026-11-26 · **Durum:** Kabul

**Bağlam:**
- Canlı Hepsiburada bilgileri girildikten sonra sipariş çekme her dakika 401 aldı.
- Entegratör satıcı panelinde "Aktif" görünüyordu. Kullanıcı, kullanıcı adı alanına entegratör kullanıcı adını (`…_dev`) yazmıştı.
- Resmi doküman (developers.hepsiburada.com: Sipariş Entegrasyonu Önemli Bilgiler, Entegratöre Servis Anahtarı Ekleme) yalnızca "Basic Auth, kullanıcı adı ve şifre" ve servis anahtarının nereden alındığını söylüyor; kullanıcı adının karşılığını yazmıyor.
- SIT'te çalışmış giriş biçimi, değerler yazdırılmadan karşılaştırıldı:
  - kullanıcı adı = Merchant ID (GUID);
  - şifre = servis anahtarı;
  - User-Agent dolu ve kullanıcı adından farklı (entegratör adı).

**Karar:**
- Panel formu (Ayarlar → Kanallar → Hepsiburada):
  - Alanlar "Mağaza ID (Merchant ID)", "Kullanıcı adı" (Mağaza ID ile aynı), "Servis anahtarı (şifre)" ve "User-Agent (entegratör kullanıcı adı)".
  - Her alanın altında satıcı panelinde nereden alınacağını söyleyen kısa açıklama var.
  - User-Agent artık formda zorunlu. Sunucu tarafı değişmedi: boşsa varsayılan "PazaryeriHub" gider.
- Kullanıcı adı Mağaza ID'den farklı girilirse formda uyarı çıkar. Uyarı kaydı engellemez, çünkü doküman kuralı yazmıyor.
- 401 mesajı hangi alana ne yazılacağını ve ortam seçimini kontrol etmeyi söyler. "API bilgileri hatalı" öneki korundu, kanal durumu sınıflaması (`ChannelStatus`) değişmedi.
- `HepsiburadaCredentials` TODO'su güncellendi: biçim SIT'te çalışan girişe dayanıyor, canlıda ilk başarılı çağrıyla doğrulanmalı.

**Doğrulama:**
- Birim testi: `Yetkisiz_hatasi_hangi_alana_ne_yazilacagini_soyler` (önce kırmızı). Unit 439/439.
- Playwright (ADR-054 senaryosu): etiketler ve açıklama görünür; kullanıcı adı `firmaadi_dev` iken uyarı çıkar, Mağaza ID girilince kalkar. Form kaydedilmez. ADR-033 senaryosuyla birlikte geçti.

## ADR-055: Harf farkıyla uyan stok kodu yalnızca öneri olur (Hepsiburada eşleştirmesi)

**Tarih:** 2026-11-26 · **Durum:** Kabul

**Bağlam:**
- Canlı Hepsiburada'dan 314 ürün çekildi, hiçbiri eşleşmedi. Kurulu veritabanı yalnızca okundu.
- Hepsiburada listing sorgusu yalnızca `hepsiburadaSku`, `merchantSku`, `price` ve `isSalable` döndürüyor. Barkod, ad, renk ve beden yok (ADR-037). Eşleştirmede kullanılabilecek tek alan satıcı stok kodu.
- Satıcı stok kodlarının bir kısmı bizdeki kodla aynı, ama büyük harfle ve kimi zaman Türkçe İ ile yazılmış (`MİCROBORNOZ21` ↔ `Microbornoz21`). Kodlar ikili (BIN2) karşılaştırıldığı için bunlar tutmadı. Bu yolla 25 ürün tek SKU'ya uyuyor.
- Kalan 289 üründe Hepsiburada'daki kod bizdekinden gerçekten farklı (ör. `AXELBORNOZ6` ↔ `AxelBornoz006`). Bu ürünler kodla eşleştirilemez.
- Harf farkını otomatik bağlamada yok saymak güvenli değil. Model düzeyindeki bir stok kodu (`Chain75150`), harf farkıyla başka bir SKU koduna (`CHAIN75150`) uyabilir; mevcut `VariantGroupingTests` bunu yakalıyor.

**Karar:**
- Eşleştirme adımları ve ikili kod karşılaştırması değişmedi.
- Eşleşmeyen satırın satıcı stok kodu, bir SkuKodu'na veya SKU barkoduna yalnızca büyük/küçük harf ve I/İ/ı farkıyla uyuyorsa ve bu tek bir SKU ise **öneri** üretilir:
  - Skor 1.
  - Açıklama: "yalnızca harf farkıyla uyuyor; doğruysa onaylayın".
  - Bağlama kullanıcının onayıyla mevcut eşleşmeyenler akışından yapılır.
- Kod önerisi ad benzerliği önerisinden önce gelir.
- Belirsiz ve çakışma satırları etkilenmez; açıklamaları korunur.
- Anahtar `CatalogText.FoldCode`: kırp; i, ı, İ → I; gerisi kültürden bağımsız büyük harf. Tire, boşluk ve rakam korunur.

**Açık konu:**
- Hepsiburada resmi dokümanında "Mağaza Bazlı Ürün Bilgisi Listeleme" var: `GET mpop…/product/api/products/all-products-of-merchant/{merchantId}?barcode&merchantSku&hbSku&page&size`.
- Doküman bu servisin ürünleri "özellik değerleriyle birlikte" döndürdüğünü söylüyor, ama yanıt şeması dokümanda yok.
- Barkod, ad, renk ve beden için bu servis kullanılmalı. Alan adları uydurulmamalı: önce test ortamından (SIT) örnek yanıt alınmalı ve contract test yazılmalı.

**Doğrulama:**
- Birim testleri: `ChannelCatalogMatcherTests` 6 yeni test. Üç örnek: `MİCROBORNOZ21`, `MARINE85150LACIVERT`, `STRIPEBORNOZ0`.
- Mevcut testler korundu: Türkçe i ayrımı otomatik bağlamada korunuyor; model kodu harf farkıyla bağlanmıyor. Unit 445/445.
- Kurulu veride aynı katlama 314 bekleyen üründen 25'ine tek öneri veriyor, birden fazla adaylı ürün yok.

## ADR-056: Hepsiburada kataloğu ürün bilgisi servisiyle zenginleştirilir

**Tarih:** 2026-11-26 · **Durum:** Kabul

**Bağlam:**
- Hepsiburada listing sorgusu yalnızca HBSKU, satıcı stok kodu, fiyat ve satış durumu veriyor (ADR-037, ADR-055).
- Canlıda 314 ürünün 289'unda satıcı stok kodu bizdeki koddan farklı. Barkod, ad, renk ve beden olmadan eşleştirme ve öneri yapılamıyor.
- Resmi dokümanda "Mağaza Bazlı Ürün Bilgisi Listeleme" (`GET mpop…/product/api/products/all-products-of-merchant/{merchantId}?page&size`) var. Dokümana göre ürünleri özellik değerleriyle birlikte döndürüyor, ancak yanıt şeması dokümanda yok.
- Şema test ortamında (SIT) okundu (2026-11-26, yalnızca GET). Ayrıntı `docs/kanal-api-notlari.md` içinde.

**Karar:**
- `FetchCatalogAsync` ilk sayfada ürün bilgisi servisinin tüm sayfalarını okur (100'lük) ve sonucu hbSku'ya göre katalog çekimi boyunca tutar. Listing satırına şu alanlar eklenir:
  - barkod, ad, marka, kategori, ilk https görsel;
  - renk (`renk_variant_property`) ve beden (`beden_variant_property`); varyant metni "Renk: …; Beden: …".
- Aynı hbSku birden fazla satırdaysa listing'in merchantSku'suyla uyan satır seçilir, yoksa ilki.
- Model kodu verilmez (`variantGroupId` HB'nin grup kodu). ADR-031 değişmedi: her HBSKU ayrı varyant.
- Ürün bilgisi servisi hata verirse katalog eskisi gibi listing alanlarıyla gelir ve uyarı loglanır. Böylece doğrulanmamış canlı adres katalog çekimini durdurmaz.
- Canlı adres `https://mpop.hepsiburada.com/product`; dokümandaki "-sit çıkarılır" kuralıyla belirlendi. `// TODO: doğrulanmalı`.
- Eşleşmeyenler anahtarı "barkod, yoksa kanal ürün kodu" olduğu için barkod gelince ürünler ikinci kez açılırdı. Bunu önlemek için:
  - Anahtarı kanal ürün kodu olan eski kayıt aynı koddan bulunup barkod anahtarına taşınır.
  - Eşleşen öğenin kanal ürün kodu da "çözüldü" sayılır; eski kayıt BAGLANDI olur.

**Doğrulama:**
- Contract testleri:
  - SIT yanıtından kısaltılmış örnekle alan eşlemesi ve aynı hbSku seçimi.
  - Sayfalama ve tek seferlik okuma.
  - Servis hatasında devam.
  - Canlı/SIT adresi.
- Entegrasyon testi: barkod sonradan gelince eski kayıt taşınır, eşleşen kapanır.
- Unit 449/449, entegrasyon 172/172.
- SIT'te gerçek adaptörle uçtan uca çalıştırıldı (yalnızca okuma). 30 ilanın 30'u barkod, ad ve görselle geldi. SIT ürünlerinde varyant özelliği olmadığı için renk ve beden gelmedi.
- Canlıda doğrulanmadı.

## ADR-057: Hepsiburada'nın ürettiği barkod yerine satıcı stok kodu

**Tarih:** 2026-11-26 · **Durum:** Kabul

**Bağlam:**
- ADR-056 canlıda çalıştı. Ürün bilgisi servisi 200 döndü, böylece canlı adres `mpop.hepsiburada.com` doğrulandı.
- Sonuç: 314 ürünün 81'i barkodla eşleşti, 38'inden SKU oluşturuldu, 195'i eşleşmedi.
- Barkodsuz ürünlerde Hepsiburada kendi değerini üretiyor: `SoftCottonCreatedbyGökhan` + GUID parçası (ör. `…090cad47-469`, 37 karakter).
  - Veritabanında bu desene uyan 187 değer var: 141'i eşleşmeyen kayıtlarda, 23'ü SKU barkodunda, 23'ü ilanlarda.
  - Gerçek barkodların hiçbiri bu desene uymuyor.
- Bu değerler SKU barkoduna yazılmıştı (21'i yeni açılan SKU'larda, 2'si daha eski SKU'larda). Kullanıcı bunların yerine Hepsiburada stok kodunun kullanılmasını istedi.

**Karar:**
- `HepsiburadaAdapter.IsGeneratedBarcode`: değer en az 13 karakter, rakamla başlamıyor ve `[0-9a-f]{8}-[0-9a-f]{3,4}` ile bitiyorsa üretilmiş barkod sayılır.
- Üretilmiş barkod yerine katalog öğesinin barkodu satıcı stok kodu olur. Stok kodu önce listing'den, yoksa ürün bilgisinden alınır. Böylece:
  - SKU açılırken barkod = stok kodu olur;
  - mevcut ilanın kanal barkodu bir sonraki çekimde güncellenir (ilan kanal ürün koduyla tanınır).
- Eşleşmeyenler: kanal barkodu değişince anahtar da değişir. ADR-056'nın taşıma kuralı genişletildi:
  - Kanal ürün kodu tek bir kayıtta geçiyorsa, anahtarı ne olursa olsun o kayıt yeni anahtara taşınır.
  - Kod birden fazla kayıtta geçiyorsa taşınmaz.
- Veri düzeltmesi (kullanıcı onayıyla, 2026-11-26): kurulu veritabanında üretilmiş barkodu taşıyan 23 SKU'nun barkodu HB stok koduyla değiştirildi. Bu değer 23'ünde de SKU koduyla aynı.
  - Önce deneme çalıştırıldı: çakışma yok. Ardından tek transaction'da uygulandı.
  - Doğrudan SQL olduğu için işlem geçmişine (`AuditLog`, HTTP isteği kaydı) düşmedi.
- 195 eşleşmeyen ürün için yeni SKU açılmadı. Kullanıcı önerileri panelden tek tek inceleyecek. Öneriler ad benzerliğine dayandığı için renk ve beden tutmayabilir.

**Doğrulama:**
- Birim testleri: üretilmiş barkod tanıma (5 örnek), barkod yerine stok kodu (listing'de stok kodu yoksa ürün bilgisinden). Unit 455/455.
- Entegrasyon testi: barkod değişince eşleşmeyen kayıt ürün koduyla taşınır, ilan barkodu güncellenir. Entegrasyon 173/173.

## ADR-058: Trendyol ürün rengi canlı yanıt biçiminden okunur

**Tarih:** 2026-11-26 · **Durum:** Kabul

**Bağlam:**
- Depo Excel'i ile kanalları eşleştirme denemesinde Trendyol ilanlarının hiçbirinde renk yoktu (584 varyant).
- Canlı onaylı ürün yanıtı okundu (yalnızca GET, 2026-11-26). İçerik özellikleri düz biçimde geliyor: `{attributeId, attributeName, attributeValueId?, attributeValue}`.
- Dokümandaki örnekteki `attributeValues[]` listesi canlı yanıtta yok. Adaptör listeyi beklediği için renk hep boş kaldı.
- Her içerikte iki "Renk" var:
  - 47: satıcının yazdığı renk, ör. "Açık Pembe";
  - 348: platform rengi, ör. "Pembe".
- Ürün düzeyinde "Boyut/Ebat" (92) da var, ama değerleri "2'li" ya da "Standart" gibi; gerçek ebat değil.

**Karar:**
- `ContentAttribute` hem düz `attributeValue`'yu hem de `attributeValues[]` listesini okur (`FirstValue`).
- Renk önceliği: 47 (satıcı), sonra kimliksiz "Renk" (dokümandaki biçim), sonra diğerleri (348).
- Ürün düzeyindeki "Boyut/Ebat" beden olarak alınmaz. Alınsaydı şüpheli eşleşme kontrolünde (ADR-053) yersiz "beden farklı" uyarıları çıkardı.
- Sonuç: kanal kataloğunda Trendyol rengi dolar. Bu, eşleştirmede renk adımını ve ilan detayındaki renk karşılaştırmasını etkiler. Renk farkı olan mevcut eşleşmeler artık "şüpheli" listesinde görünebilir; bu beklenen bir sonuçtur.

**Doğrulama:**
- Contract test: canlı yanıttan kısaltılmış örnek; 47 önceliği, yalnız 348 olduğunda o, ürün düzeyi ebatın beden sayılmaması.
- Eski doküman biçimi testi korundu. Unit 456/456.
- Canlıda düzeltilmiş adaptörle tüm katalog okundu (yalnızca okuma): 584/584 varyantta renk var.

## ADR-059: Depo Excel'i → ortak SKU geçişi (komut satırı aracı)

**Tarih:** 2026-11-26 · **Durum:** Kabul (uygulama kullanıcı onayına bağlı)

**Bağlam:**
- Kullanıcının depo Excel'i ("SOFT STOK 1": Model, Varyant, Ürün Cinsi, Beden, Serbest Miktar) bütün kanalların stok kaynağı.
  - 872 satır, 127 model, kod ya da barkod yok.
  - Dosya yarım kaydedilmişti; sayfa verisi kurtarıldı.
- Kanallardaki ürünler bugün kanal kodlarından türemiş SKU'lara bağlı. Aynı mal kanallarda farklı SKU'larda.
- Kullanıcı kararları (2026-11-26):
  - Depo Excel'i stok için esas.
  - Her depo satırı bir ortak SKU olur.
  - Kesin eşleşen kanal ürünleri bu SKU'lara bağlanır; kalanlar onay dosyasıyla sonra taşınır.
  - Kanallardaki stok şimdilik değişmemeli. Trendyol ve Hepsiburada STOK yeteneği kapalı, Beymen mock; araç yeteneklere dokunmaz.
- Eşleştirme (model + renk + beden) panelin dışında, onay dosyası üzerinden yapıldı (`depo-eslestirme/Depo-Ortak-SKU-Onay.xlsx`, repoya girmez).
  - Trendyol rengi ADR-058 ile geldi.
  - 1.098 kanal öğesinin 468'i tek depo satırına oturdu.

**Karar:**
- Planlama saf fonksiyon (`DepotMigrationPlanner`):
  - Her depo satırı → SKU. Kod zaten varsa o SKU kullanılır ve stoğu değiştirilmez.
  - Atanan ilanlar taşınır; yerindeki ilan taşınmaz.
  - Bekleyen eşleşmeyenler bağlanır.
  - Bütün ilanları taşınan eski SKU 0'a sayılır; rezervi varsa sıfırlanmaz, uyarı verilir.
  - Negatif depo miktarı 0 sayılır (defter negatif tutmaz), uyarı verilir.
  - Hata durumları: tekrar eden kod, bilinmeyen ilan/satır, çift atama, alan sınırı. Herhangi bir hata varsa hiçbir şey uygulanmaz.
- Uygulama (`DepotMigrationService`) yalnızca mevcut servislerle yapılır; böylece transaction, hareket ve outbox kuralları korunur:
  - `CreateProductAsync`: model başına bir ürün; renk = varyantın Türkçe kısmı; barkod boş.
  - `AdjustToCountAsync`: SAYIM hareketi, kaynak `DEPO_GECIS`.
  - `UpsertListingAsync`: ilan taşıma.
  - `LinkUnmatchedAsync`: eşleşmeyen bağlama.
- Her adım kendi transaction'ında çalışır. Yeniden çalıştırmak güvenlidir.
- Fiyat kopyalanmaz: yeni SKU'da fiyat yok, bu yüzden kanala fiyat gitmez. Fiyatlar sonra girilir.
- Araç `tools/PazaryeriHub.Tools.DepoGecis`: `deneme` (varsayılan, yalnızca rapor) ve `uygula`; hedef `--test` ya da `--kurulu`.
  - Kurulu sistemde uygulamadan önce yedek alınır (`kurulum/veri-yedekle.ps1`).
- Sonuç: kanal stok yeteneği açıldığında kanallara depo Excel'ine göre hesaplanan stok gider. Açmadan önce kanal ve depo stokları karşılaştırılmalıdır.

**Doğrulama:**
- Birim: `DepotMigrationPlannerTests` 11.
- Entegrasyon: `DepotMigrationServiceTests`. Deneme yazmaz; uygulama SKU, sayım, outbox, taşıma, bağlama ve sıfırlama yapar; yeniden çalıştırma bir şey değiştirmez.
- Unit 467/467, entegrasyon 174/174.
- Kurulu sistemde deneme (2026-11-26):
  - 872 yeni SKU (stok 19.434);
  - 409 ilan taşınacak, 59 eşleşmeyen bağlanacak;
  - 235 eski SKU sıfırlanacak (15.470 adet);
  - uyarılar: 1 negatif depo satırı, 1 rezervli SKU; hata yok.
  - Yedek: `dagitim/PazaryeriHub-veri-depo-gecis-oncesi.bak`.

## ADR-060: Bütün liste ekranlarında "Excel'e aktar" (ortak bileşen + ortak uç)

**Bağlam:**
- Kullanıcı her sekmedeki listeyi Excel'e almak istiyor. Var olan dışa aktarmalar ekrana özeldi: Eşleştirme'deki şablon/dışa aktarma (içe aktarma biçiminde, filtresiz) ve Siparişler/Raporlar'daki satır bazlı sipariş Excel'i (`/api/orders/export`).
- Aktarma o an ekrandaki filtreye uymalı; sayfalanmış listelerde yalnızca görünen sayfa değil, filtreye uyan bütün kayıtlar gelmeli.
- Destek rolünde müşteri bilgisi maskelenir; dışa aktarma rol yetkilerine ve maskelemeye uymalı.

**Karar:**
- Tek sunucu ucu: `POST /api/export/xlsx` (`Policies.Read`). Gövde: dosya adı, sayfa adı, sütunlar (başlık + tür: text/number/money/date/datetime/bool) ve satırlar. `TableExportWriter` (ClosedXML) çalışma kitabını üretir. Uç veritabanından hiçbir şey okumaz.
- Veri her zaman ekranın kendi liste ucundan gelir (panel `fetchAllPages` ile filtreyi koruyarak bütün sayfaları çeker). Böylece rol yetkisi ve Destek maskelemesi liste ucunda ne ise Excel'de de odur; dışa aktarma için ayrı bir yetki/maskeleme yolu yoktur, atlanamaz.
- Tek panel bileşeni `ExportButton`, iki modlu:
  - Veri modu (sayfalanmış veya sınırlı listeler): `load` + `columns`. Stok Listesi (defter ve kanal sekmeleri), Stok Hareketleri, İade/İptal, iade talepleri, Eşleştirme, eşleşmeyen ürünler, gönderim kuyruğu, mutabakat sapmaları, stok onayları, kanal bildirimleri ve işlemleri, fiyat onayları ve geçmişi, Fiyat & Stok Düzenle, Soru-Cevap, İşlem Geçmişi.
  - Ekran modu (bütün satırları zaten ekranda olan tablolar): düğmenin kartındaki tablo okunur. Düğmeler, onay kutuları, görseller ve `aria-hidden` süsler atlanır; input/select değeri alınır. Tamamen sayısal (tr-TR biçimli) ya da ₺ sütunlar sayı olarak gider; barkod gibi uzun tam sayılar ve baştaki sıfırlı değerler metin kalır. Genel Bakış tabloları, Raporlar, Ayarlar (yetenek özeti, kullanıcılar, referans verisi), kanal yazma yetenekleri, fiyat önizleme, Kanal Stoğu → Defter önizleme/sonuç, katalog/içe aktarma raporları, SKU detay tabloları.
- Siparişler ekranındaki mevcut satır bazlı Excel (sunucu tarafı, filtreli, bütün kayıtlar, müşteri bilgisi yok) olduğu gibi kaldı.
- Güvenlik ve sınırlar: metin hücreleri her zaman metin yazılır (`=`, `+` ile başlayan değer formül olmaz); en fazla 100.000 satır ve 100 sütun, hücre 32.000 karakter; dosya ve sayfa adı güvenli hale getirilir (yol parçaları atılır). Tarihler UTC gelir, Türkiye saatine çevrilir.

**Gerekçe:** Her ekran için ayrı sunucu ucu yazmak liste sorgularını ikinci kez kopyalamak ve maskeleme kuralını her uçta tekrarlamak demekti. Ortak uç + liste uçları, yetki ve maskelemeyi tek yerde tutar.

**Doğrulama:**
- Birim: `TableExportWriterTests` (türler, formül enjeksiyonu, sınırlar, sayfa/dosya adı).
- Entegrasyon: `TableExportApiTests` (oturumsuz 401, dört rol de alır, geçersiz istek 400).
- Playwright (`e2e/export-variant.spec.ts`): Stok Hareketleri'nde gönderilen satır sayısı = filtrenin toplamı (görünen 50 değil) ve filtre uygulanınca yalnızca uyanlar; Genel Bakış tablosu ekran modunda; Destek rolü kanal sekmesini aktarır.

## ADR-061: Aynı ürünün varyantlarına toplu fiyat (Stok Listesi → kanal sekmesi)

**Bağlam:**
- Depo geçişinden (ADR-059) sonra yeni SKU'larda fiyat yok. Aynı ürünün (Product) beden/renk varyantları genelde aynı fiyatla satılıyor; tek tek girmek zahmetli.
- "Kanallardan çekilen stoklar" ekranı: Stok Listesi → kanal sekmesi (`ChannelStockTab`, ADR-035).
- Yeni bir kanala yazma yolu açılmamalı; fiyat onayı, FIYAT yeteneği (KAPALI/DENEME/ACIK) ve FIYAT kuyruğu aynen geçerli olmalı.

**Karar:**
- Kanal stok listesine ürün süzgeci (`productId`) ve satırlara `ProductId` eklendi: panel, aramada tek satır görünse de ürünün o kanaldaki bütün varyantlarını sunucudan alır.
- Yeni uç `PUT /api/prices/products/{productId}/channels/{channelId}/variant-price` (Yönetici): seçilen ilanların hepsi o ürüne ve o kanala ait olmalı, değilse hiçbiri yazılmaz (400). Yazım mevcut `SetListingOverridesAsync` ile yapılır: ilan fiyatı + PriceHistory (`ILAN_FIYATI`) + `FiyatDegisti` outbox aynı transaction'da. Kanala gönderimi her zamanki gibi OutboxIsle → FIYAT kuyruğu → FiyatGonder yapar:
  - KAPALI: fiyat kaydedilir, kuyruğa/kanala hiçbir şey gitmez.
  - DENEME: her ilan için onay açılır; onaylanmadan gitmez.
  - ACIK: gider; eşik aşan değişiklik yine onaya düşer.
- Fiyat ilan fiyatıdır (yalnızca o kanal); SKU'nun temel fiyatı ve diğer kanallar değişmez. "Elle fiyatı kaldır" ilan fiyatını kaldırır (SKU fiyatı + kanal kuralı).
- Panel: satırdaki "Varyantlara toplu fiyat" (yalnızca Yönetici) paneli açar; pasif olmayan varyantlar seçili gelir, seçim değiştirilebilir. Kaydetmeden önce ve sonra yetenek durumuna göre ne olacağı yazılır.

**Doğrulama:**
- Entegrasyon (`PricePipelineTests`): ürünle süzme; ACIK'ta üç varyant tek fiyatla mock kanala gider ve geçmişe yazılır; aynı fiyat tekrar değişiklik saymaz; KAPALI'da kuyruk/onay/kanal boş; DENEME'de her varyant onaya düşer, kanala gitmez; başka ürünün/kanalın ilanı, boş seçim, geçersiz fiyat 400 ve hiçbiri yazılmaz; Depo 403.
- Playwright: dört beden listelenir, biri çıkarılır, geçersiz fiyat engellenir, üç varyanta yazılır; diğer beden ve diğer kanal değişmez; temizlik.

## ADR-062: Sol menü sabit

**Karar:** `.sidebar` `position: sticky; top: 0; height: 100vh; overflow-y: auto`. Sayfa (içerik) kaydırılınca menü yerinde kalır; menü ekrandan uzunsa kendi içinde kayar. Sayfanın kaydırıcısı belgede kalır (ayrı bir kaydırma kabı yok): tarayıcı geri/ileri konumu, `scrollIntoView` ve sayfa içi arama bozulmaz. 900 px altında menü eskisi gibi üstte yatay ve sabitlenmez (dar ekranda içeriğin üstünü kapatmasın).

**Doğrulama:** Playwright: geniş ekranda sayfa sonuna kaydırılınca menü üstte ve "Ayarlar" bağlantısı görünür; 375 px'te menü `static`, yatay kaydırma yok.

## ADR-063: Eşleşmeyen kanal ürünü kanal fiyatı, linki ve varyantıyla saklanır

**Bağlam:** Katalog çekiminde eşleşmeyen ürün `UnmatchedChannelItem`'a yazılıyordu; kanalın bildirdiği satış/liste fiyatı, ürün linki ve varyant bilgisi bu kayıtta yoktu. Panelden (veya depo geçişinde, ADR-059) bağlanan ilan `KanalVerisi`'nde bu alanlar boş açılıyor, bir sonraki katalog çekimine kadar "kanalda fiyat yok" görünüyordu (canlı, 2026-11-27: bağlanan 50 Hepsiburada ilanının hepsi; çekimle eşleşen 178 ilanın hepsinde fiyat vardı).

**Karar:** `UnmatchedChannelItem`'a `KanalSatisFiyati`, `KanalListeFiyati` (DECIMAL(18,2), > 0), `UrunLinki`, `Varyant` kolonları eklenir (migration 0020). Her çekimde güncellenir; bağlanınca ilanın `KanalVerisi`'ne aynen yazılır (görsel de artık taşınır). Değerler salt okumadır: kanaldan okunur, kanala hiçbir şey göndermez; fiyat gönderim kuralları (ADR-028) değişmez.

**Mevcut kayıtlar:** Daha önce bağlanmış fiyatsız ilanlar bir sonraki katalog çekiminde dolar (çekim mevcut ilanların `KanalVerisi`'ni yeniden yazar); veri düzeltme betiği gerekmez.

**Doğrulama:** Entegrasyon testi: fiyat/link/varyantlı eşleşmeyen ürün panelden bağlanınca ilan bu değerlerle açılır (önce başarısızdı).

## ADR-064: Öneri ve İstekler (kullanıcı notları)

**Bağlam:** Uygulamayı kullananlar öneri/isteklerini panel içinden yazabilmeli; notun kimin yazdığı belli olmalı.

**Karar:**
- Sol menüde "Öneri ve İstekler" (`/oneriler`), tüm roller görür. Her rol not yazar ve tüm notları okur (notlar iş verisi/müşteri bilgisi içermez; KVKK kapsamında ek alan yok).
- Tablo `dbo.Oneri` (migration 0021): `Metin` (en fazla 2000), `KullaniciAdi`, `AdSoyad`, `Durum` (YENI / TAMAMLANDI / REDDEDILDI), `Olusma`, `DurumDegistiren`, `DurumTarihi`.
- Yazan **istemciden alınmaz**: oturumdaki kullanıcı Id'siyle `AppUser`'dan o anki kullanıcı adı ve ad soyad okunup yazılır (çerezdeki eski ad veya gövdede gönderilen ad kullanılmaz). Kullanıcıya FK yok: kullanıcı silinse de not yazanın adıyla kalır (AuditLog ile aynı yaklaşım).
- Durumu yalnızca Yönetici değiştirir (Tamamlandı / Reddet / Yeniye al); değiştiren ve zaman kaydedilir. Not silme ve düzenleme yok (kapsam dışı; gerekirse sonra).

**Doğrulama:**
- Entegrasyon (`FeedbackTests`): Destek'in notu gövdedeki sahte ada rağmen kendi adıyla kaydedilir, Muhasebe listede görür; boş ve 2000+ karakter 400; Destek durum değiştiremez (403); geçersiz durum 400, olmayan not 404; Yönetici TAMAMLANDI yapar, değiştiren yazılır, YENI süzgecinde görünmez.
- Playwright (`feedback.spec.ts`): Destek menüden not yazar, ad soyad ve kullanıcı adıyla listelenir, durum düğmesi görmez; Yönetici "Tamamlandı" yapar.

## ADR-065: Hepsiburada satış fiyatı KDV dahil kabul edildi (kullanıcı onayı)
**Tarih:** 2026-11-27
**Bağlam:**
- ADR-039: fiyat gönderimi `CanEnable = Supported && VatIncluded == true` koşuluna bağlı. Hepsiburada "Listing Fiyat Güncelleme" dokümanı yalnızca `price` alanını veriyor, KDV dahil/hariç ifadesi yok (2026-11-27 yeniden okundu). Bu yüzden Hepsiburada FIYAT yeteneği açılamıyordu.
- Kanıt (2026-11-27, salt okuma): Hepsiburada listing API'sinden okunan `price` ile hepsiburada.com'da müşterinin gördüğü fiyat iki üründe birebir aynı (Lord Havlu Mor 3920 = 3.920 TL; Stripe Bornoz Haki L 8680 = 8.680 TL). Sitede tüketiciye gösterilen fiyat KDV dahildir; gönderim aynı alanı yazar.

**Karar:**
- Kullanıcı onayıyla (seçenek: "Evet, KDV dahil kabul et") `HepsiburadaAdapter.DescribePriceUpdate()` → `VatIncluded: true`. Not ve kaynak alanı kararın kullanıcı onayı olduğunu ve resmi dokümanda ifade bulunmadığını söyler. Fiyat ekranı bu kanallarda "dahil (kullanıcı onayı)" yazar (önceden her dahil kanala "dokümanda doğrulandı" diyordu).
- Yetenek açmak ayrı adımdır (Ayarlar; önce DENEME önerilir). Yalnızca satış fiyatı gider (liste fiyatı alanı yok). Kategori eşiği dışındaki fiyat listingi kilitler (MinLock/MaxLock); ilk gönderim az sayıda ürünle yapılmalı.
- KDV doğrulanmamış kanal korumasının testi Hepsiburada yerine mock kanal kuralıyla sürer (`OrderNotificationContractTests`).

**Risk:** Hepsiburada ileride KDV hariç fiyat beklediğini belgelerse fiyatlar yanlış gider. İlk gönderimden sonra birkaç ürünün sitedeki fiyatı kontrol edilmeli.

**Doğrulama:** `PriceContractTests`, `HepsiburadaAdapterContractTests` (Hepsiburada `CanEnable` true; önce başarısızdı); tüm .NET testleri.

## ADR-066: Depo barkodu ortak SKU'ya yazılır; barkodla kesin eşleşen ilanlar taşınır
**Tarih:** 2026-11-27 · **Durum:** Kabul, kurulu sistemde uygulandı

**Bağlam:**
- Kullanıcı depo listesinin barkodlu sürümünü verdi ("MamulStok": Model, Varyant, Beden, Barkod). Ortak SKU'larda (ADR-059) barkod yoktu; 167 barkod kanaldan türemiş eski Beymen SKU'larında duruyordu.
- Beymen ilanları gerçek ürün barkodunu taşıyor; Trendyol ve Hepsiburada ilanlarında barkod alanı satıcının kendi kodu. Barkodla kesin eşleşme bu yüzden çoğunlukla Beymen'de çıkıyor.
- Mevcut model + renk + beden eşleşmeleri beden açısından denetlendi; yalnızca yazım farkı ("40/42" ↔ "40-42") var, gerçek hata yok.

**Karar:**
- `DepotRow.Barcode` (isteğe bağlı). Onay dosyasının "Depo satırları" sayfasında isteğe bağlı "Barkod" sütunu.
- `DepotMigrationPlanner` barkod planı kurar (`PlannedBarcode`):
  - Barkod ortak SKU'ya yazılır. Barkod tekil olduğu için başka (eski) bir SKU'da duruyorsa önce oradan kaldırılır.
  - Hiçbir şey değiştirilmeyen durumlar (uyarı verilir): aynı barkod birden fazla depo satırında; ortak SKU'nun zaten başka barkodu var; barkod başka bir depo satırının SKU'sunda.
  - 50 karakterden uzun barkod hatadır (hiçbir şey uygulanmaz).
- `DepotMigrationService`: kaldırma ve yazma aynı transaction'da. Yeniden çalıştırma bir şey değiştirmez.
- Atamalar: yalnızca kanal barkodu (barkod, stok kodu ya da ürün kodu) depo barkoduyla birebir aynı olan ve bugün başka SKU'ya bağlı ilanlar ile bekleyen eşleşmeyenler. Önceden elle düzeltilmiş ilanlar (set/tekli kontrolü) atanmaz.
- Barkodu ortak SKU'da olan yeni kanal ürünleri, katalog çekiminde `ChannelCatalogMatcher` sırasıyla (mevcut ilan, sonra barkod = `Sku.Barkod`) kendiliğinden doğru SKU'ya bağlanır.

**Sonuç (kurulu sistem, 2026-11-27; yedek `dagitim/PazaryeriHub-veri-barkod-oncesi.bak`):**
- 857 barkod yazıldı (167'si eski SKU'dan alındı).
- 101 ilan taşındı (96 Beymen, 5 Trendyol), 3 Beymen eşleşmeyeni bağlandı.
- Bütün ilanları taşınan 64 eski SKU 0'a sayıldı (736 adet; depo listesi esas). Kanal stok yetenekleri kapalı olduğu için kanallara stok gitmedi.
- Yeniden deneme: yapılacak bir şey yok.
- Depo listesinde düzeltilmesi gerekenler (kullanıcıya bildirildi):
  - İki barkod iki satırda tekrar ediyor: 8698642021365 (Sharp Lacivert S/M) ve 8698642021372 (Love Şalyaka set S/L). Bu satırlara barkod yazılmadı.
  - 3 satırın depo listesinde karşılığı yok.

**Doğrulama:**
- `DepotMigrationPlannerTests`: 4 yeni test, önce derlenmedi.
- `DepotMigrationServiceTests`: barkod eski SKU'dan ortak SKU'ya taşınır, ilan taşınır, yeniden çalıştırma değiştirmez.
