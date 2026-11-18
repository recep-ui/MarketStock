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
