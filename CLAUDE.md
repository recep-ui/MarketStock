# GÖREV: Çoklu Pazaryeri Stok ve Sipariş Yönetim Sistemi

Sen kıdemli bir .NET yazılım mimarı ve geliştiricisisin. Aşağıdaki spesifikasyona göre, aşama aşama ve test edilerek çalışan bir uygulama geliştireceksin.

## 1. Amaç

Trendyol, Hepsiburada, Beymen ve softcotton.com.tr kanallarında satış yapan bir işletme için:
- Tek bir stok defteri (ledger) tek doğruluk kaynağı olacak; pazaryerleri bu stoğun yansıtıldığı vitrinler.
- Pazaryerlerinden siparişler, iptaller ve iadeler çekilecek; kanallardaki stok değerleri mutabakat için çekilecek.
- Hesaplanan satılabilir stok ve fiyat tüm kanallara toplu gönderilecek.
- Tüm kanalların gelen/giden siparişleri tek panelde izlenecek.

## 2. Teknoloji (değiştirme, gerekçesiz sapma)

- Backend: .NET 8, ASP.NET Core Web API, **modüler monolit** (mikroservis yok)
- Veritabanı: **SQL Server 2019/2022** (T-SQL). Stok yazımları için Dapper + saklı yordamlar, genel CRUD için EF Core
- Kuyruk/zamanlayıcı: **Hangfire + Hangfire.SqlServer** (Redis/RabbitMQ yok)
- Olay aktarımı: **Outbox tablosu** (stok değişimiyle aynı transaction)
- Loglama: Serilog (yapılandırılmış), hassas veri maskeleme
- Panel: React + TypeScript (Vite), tablo için TanStack Table
- Test: xUnit, FluentAssertions, Testcontainers (MSSQL) ile entegrasyon testleri
- Konteyner: docker-compose (api, mssql, panel)

## 3. Mimari ilkeler

1. **Hub-and-spoke:** Çekirdek kanalı tanımaz. Her kanal `IChannelAdapter` arayüzünü uygular.
2. **Stok asla üzerine yazılmaz.** Her değişiklik `StockMovement` kaydıdır (append-only). `StockBalance` türetilmiş durumdur.
3. **Stok değişimi + hareket + outbox kaydı tek transaction'da** yazılır.
4. **İdempotentlik:** `UNIQUE(ChannelId, KanalSiparisNo)`; aynı siparişin iki kez işlenmesi stoğu iki kez etkilememeli.
5. **Tüm dış API çağrıları Hangfire job'ı** üzerinden çalışır: rate limit, üstel geri çekilme, 5 denemeden sonra HATA durumu.
6. **Debounce:** `SyncQueue` üzerinde filtreli unique index ile aynı listing için tek bekleyen kayıt.
7. **Oversell durumunda sipariş reddedilmez:** kaydedilir, "stok yetersiz" bayrağıyla panelde uyarı gösterilir.

## 4. Veritabanı

Veritabanı ayarları:
```sql
ALTER DATABASE PazaryeriHub SET READ_COMMITTED_SNAPSHOT ON;
ALTER DATABASE PazaryeriHub SET ALLOW_SNAPSHOT_ISOLATION ON;
```

Kurallar:
- Tarihler `DATETIME2(3)`, UTC. Para `DECIMAL(18,2)`. Esnek veri `NVARCHAR(MAX)` + `CHECK (ISJSON(...)=1)`.
- Ürün adı gibi metinler `Turkish_CI_AS`; **SKU, barkod, kanal ürün kodu, kanal sipariş no kolonları `Latin1_General_100_BIN2`** (Türkçe i/İ sorununu önlemek için).
- Migration'lar sürümlü SQL script olarak tutulur (DbUp veya EF migrations; birini seç ve gerekçelendir).

Tablolar (kolon adları Türkçe, bu isimleri koru):
- `Product(Id, Ad, Marka, Kategori, OlusturmaTarihi)`
- `Sku(Id, ProductId, SkuKodu UNIQUE, Barkod UNIQUE filtreli NULL hariç, Renk, Beden)`
- `Warehouse(Id, Ad)`
- `StockBalance(SkuId, WarehouseId, Fiziksel, Rezerve, GuvenlikStogu, Satilabilir = Fiziksel-Rezerve-GuvenlikStogu PERSISTED, RowVer ROWVERSION)`; PK(SkuId, WarehouseId); CHECK: Fiziksel>=0, Rezerve>=0, Rezerve<=Fiziksel
- `StockMovement(Id BIGINT, SkuId, WarehouseId, Tip [GIRIS|REZERVE|SEVK|REZERVE_IPTAL|IADE_GIRIS|SAYIM], Adet, KaynakTip, KaynakId, Tarih, Kullanici)`; indeks (SkuId, Tarih DESC)
- `Channel(Id, Kod [TRENDYOL|HEPSIBURADA|BEYMEN|SOFTCOTTON], Ad, PayYuzde DEFAULT 100, Aktif, Ayarlar JSON, KimlikSifreli VARBINARY)`
- `ChannelListing(Id, SkuId, ChannelId, KanalUrunKodu, KanalBarkod, SonGonderilenStok, SonSenkron, Durum, KanalVerisi JSON)`; UNIQUE(SkuId, ChannelId)
- `ChannelStockSnapshot(Id, ListingId, KanalStok, CekilmeZamani)`
- `[Order](Id, ChannelId, KanalSiparisNo, Durum, Toplam, SiparisTarihi, MusteriOzet JSON, KargoBilgisi JSON)`; UNIQUE(ChannelId, KanalSiparisNo)
- `OrderLine(Id, OrderId, SkuId, Adet, BirimFiyat, Komisyon)`
- `OrderEvent(Id, OrderId, EskiDurum, YeniDurum, Kaynak, Tarih)`
- `Outbox(Id, OlayTipi, Payload JSON, Olusma, IslendiAt)`; filtreli indeks IslendiAt IS NULL
- `SyncQueue(Id, ListingId, Tip [STOK|FIYAT], Durum [BEKLIYOR|GONDERILDI|HATA], Deneme, Hata, BatchId, Olusma)`; **UNIQUE (ListingId, Tip) WHERE Durum='BEKLIYOR'**

Saklı yordamlar (hepsi `SET XACT_ABORT ON`, tek transaction, hareket + outbox yazar):
- `usp_StokRezerve`: tek koşullu UPDATE (`WITH (UPDLOCK, ROWLOCK)`, `WHERE Fiziksel - Rezerve >= @Adet`); 0 satır etkilenirse `THROW 50001`. Uygulama katmanı bu hatayı yakalayıp siparişi "stok yetersiz" bayrağıyla kaydeder.
- `usp_StokSevk` (fiziksel↓, rezerve↓)
- `usp_RezerveIptal` (rezerve↓)
- `usp_IadeGiris` (fiziksel↑)
- `usp_StokGiris` / `usp_SayimDuzelt`

Outbox tüketimi: `SELECT TOP (100) ... WITH (ROWLOCK, UPDLOCK, READPAST)` + `UPDATE ... OUTPUT` ile; birden fazla worker çakışmamalı.

## 5. Stok hesaplama

```
Satılabilir = Fiziksel - Rezerve - GuvenlikStogu        (tüm depolar toplamı)
Kanala gidecek = MIN( FLOOR(Satılabilir * Channel.PayYuzde / 100), kanal üst limiti )
```
- `PayYuzde = 100` ve herkese aynı: "tam ortak havuz" modu.
- Negatif sonuç 0'a sabitlenir.
- Bu hesaplama saf bir fonksiyon (`StockAllocator`) olmalı ve birim testle kapsanmalı.

## 6. Kanal adaptörü sözleşmesi

```csharp
public interface IChannelAdapter
{
    string ChannelCode { get; }
    Task<IReadOnlyList<NormalizedOrder>> FetchOrdersAsync(DateTime sinceUtc, CancellationToken ct);
    Task<IReadOnlyList<ChannelStock>> FetchStockAsync(IEnumerable<string>? skus, CancellationToken ct);
    Task<BatchResult> PushStockAsync(IReadOnlyList<StockUpdate> updates, CancellationToken ct);
    Task<BatchResult> PushPriceAsync(IReadOnlyList<PriceUpdate> updates, CancellationToken ct);
    Task<BatchStatus> CheckBatchAsync(string batchId, CancellationToken ct);
    Task UpdateOrderStatusAsync(string channelOrderNo, OrderStatus status, CargoInfo? cargo, CancellationToken ct);
    Task<IReadOnlyList<NormalizedReturn>> FetchReturnsAsync(DateTime sinceUtc, CancellationToken ct);
}
```
Adaptör içinde kimlik doğrulama, hız limiti (Polly), sayfalama ve hata çevirisi çözülür. Pazaryerinin ham hata metni, kullanıcıya gösterilmeden önce sadeleştirilmiş açıklamaya çevrilir.

## 7. Akışlar (Hangfire job'ları)

1. `SiparisCek` (her kanal için 1-2 dk): son başarılı zaman damgasından, **örtüşmeli pencereyle** çek; idempotent kaydet; her satır için `usp_StokRezerve`.
2. `OutboxIsle` (10 sn): `StokDegisti` olaylarını oku, kanal paylarına göre yeni stoğu hesapla, `SyncQueue`'ya yaz.
3. `StokGonder` (kanal başına toplu): adaptörle gönder, `BatchId` kaydet.
4. `BatchSorgula`: sonucu kontrol et, `GONDERILDI`/`HATA` yap, üstel geri çekilme ile yeniden dene.
5. `Mutabakat` (gece + istenirse saatlik): `FetchStockAsync` → `ChannelStockSnapshot`; sapma raporu üret.
6. `IadeCek`: iade/iptalleri çek; depoya ulaşınca `usp_IadeGiris` (panelden onayla).

Sipariş durum makinesi: `Yeni → Onaylandi → Hazirlaniyor → Kargoda → TeslimEdildi`; `Yeni/Onaylandi/Hazirlaniyor → Iptal` (rezerve iade); `Kargoda/TeslimEdildi → Iade`. Geçersiz geçişler reddedilir, her geçiş `OrderEvent`'e yazılır.

## 8. Panel ekranları

1. Genel Bakış (bugünkü siparişler, kanal bazlı ciro, kritik stok, hatalı senkronlar)
2. Stok Listesi (fiziksel/rezerve/satılabilir + her kanalda görünen stok yan yana; sapma satırları kırmızı)
3. Stok Hareketleri (filtrelenebilir defter)
4. Siparişler (tüm kanallar tek listede, kanal rozeti, durum/tarih/ürün filtresi, detayda zaman çizelgesi)
5. İade/İptal (depoya girdi onayı)
6. Eşleştirme (SKU ↔ kanal ürün kodu, Excel ile toplu yükleme/dışa aktarma)
7. Senkron Merkezi (son gönderimler, sadeleştirilmiş hata, "yeniden dene")
8. Ayarlar (kanal payı, güvenlik stoğu, uyarı eşikleri, kullanıcı rolleri)

Rol/yetki: Yönetici, Depo, Muhasebe, Destek (müşteri bilgisi maskeli: SQL Dynamic Data Masking veya API katmanında maskeleme).

## 9. Güvenlik ve uyumluluk

- API anahtarları şifreli saklanır (`KimlikSifreli`); anahtar yönetimi için `IDataProtector`/Key Vault soyutlaması kullan. Gizli bilgiyi repoya, loga veya yanıta yazma.
- KVKK: yalnızca gerekli müşteri alanlarını sakla, erişimi role bağla.
- Her dış çağrıyı logla (kanal, uç nokta, süre, durum kodu; gövdede gizli veri yok).

## 10. Çalışma kuralları (çok önemli)

- **Pazaryeri API'lerini uydurma.** Trendyol, Hepsiburada, Beymen ve softcotton.com.tr için gerçek uç nokta, kimlik doğrulama ve alan adlarını yalnızca resmi dokümantasyondan veya benim vereceğim belgelerden kullan. Emin olmadığın yerde varsayım yapmak yerine adaptörü **mock/sahte** uygulamayla bırak, `// TODO: doğrulanmalı` işaretle ve bana listele.
- Beymen için API erişim yöntemi belirsiz; bu adaptörün sadece arayüz + mock + "entegrasyon yöntemi teyit edilmeli" notunu hazırla. softcotton.com.tr altyapısı (Ticimax/IdeaSoft/Shopify/WooCommerce vb.) belirsiz; bunu bana sor ve o zamana kadar mock adaptör yaz.
- Her aşamada önce **kısa bir plan** sun, sonra uygula. Aşama bitince: ne yapıldı, nasıl test edildi, bilinen eksikler ve sonraki adım.
- Her aşama için kabul kriterleri sağlanmadan sonraki aşamaya geçme. Testleri çalıştır, sonucu raporla; çalıştıramadığın bir şeyi "çalışıyor" diye raporlama.
- Kapsam dışına çıkma (ör. muhasebe, kargo etiketi üretimi, e-fatura). Gerekirse öneri olarak not düş.
- Önemli bir mimari karar gerektiğinde (ör. migration aracı seçimi) kısa gerekçeyle karar ver, `docs/decisions.md` dosyasına ADR olarak yaz.
- Kod yorumları ve dokümantasyon Türkçe; tip/metot isimleri İngilizce, veritabanı kolonları yukarıdaki Türkçe adlarla.
- **Skill eklentileriyle öncelik** (ör. Superpowers): bu dosyadaki kurallar eklenti skill'lerinden önce gelir. Çakışmada:
  - Doğrudan `main` üzerinde çalışılır; ayrı dal/worktree açılmaz. Her aşama bitince commit + push.
  - Mevcut kod silinmez veya "önce test" gerekçesiyle yeniden yazılmaz; yeni kodda önce test yazılır.
  - Yardımcı ajan (subagent) yalnızca büyük aşamalarda kullanılır; küçük düzeltmeler doğrudan yapılır.
  - Pazaryeri API kuralı (yukarıdaki ilk madde) ve "gerçek kanala yazma yok, yalnızca mock + contract test" kısıtı her zaman önce gelir.

## 11. Aşamalar ve kabul kriterleri

**Aşama 0: İskele.** Solution yapısı (`Api`, `Domain`, `Application`, `Infrastructure`, `Adapters.*`, `Tests`), docker-compose, Serilog, Hangfire, sağlık kontrolü uç noktası.
Kabul: `docker compose up` ile API + MSSQL ayağa kalkar, `/health` 200 döner.

**Aşama 1: Veritabanı ve stok motoru.** Tüm tablolar, indeksler, saklı yordamlar, `StockAllocator`.
Kabul: Testcontainers ile entegrasyon testleri: (a) 100 paralel rezervasyonda stok eksiye düşmez, (b) rezerve→iptal→sevk akışı bakiyeleri doğru bırakır, (c) her değişiklikte hareket + outbox kaydı oluşur, (d) allocator birim testleri (pay %, güvenlik stoğu, negatif→0).

**Aşama 2: Katalog + Excel.** Ürün/SKU CRUD, stok girişi, Excel içe/dışa aktarma (şablon indirme dahil), kanal ve eşleştirme API'leri.
Kabul: 1.000 satırlık Excel içe aktarılır, hatalı satırlar satır numarasıyla raporlanır.

**Aşama 3: Sipariş çekirdeği.** Normalize sipariş modeli, idempotent kayıt, durum makinesi, `SiparisCek` job'ı (mock adaptörle), oversell bayrağı.
Kabul: Aynı sipariş 5 kez gelse tek kayıt ve tek rezervasyon oluşur; geçersiz durum geçişi reddedilir.

**Aşama 4: Senkron hattı.** Outbox işleyici, `SyncQueue` debounce, `StokGonder`, `BatchSorgula`, yeniden deneme, mutabakat ve sapma raporu (mock adaptörle uçtan uca).
Kabul: Stok değişimi → ≤30 sn içinde mock kanalda güncel değer; hata durumunda 5 denemeden sonra HATA ve uyarı.

**Aşama 5: Panel.** Bölüm 8'deki ekranlar, rol/yetki, hata mesajı sadeleştirme.
Kabul: Tüm ekranlar mock verilerle çalışır; Playwright ile 3 temel senaryo (sipariş listesi filtre, stok sapması görme, hatalı senkron yeniden deneme).

**Aşama 6: Gerçek adaptörler (sırayla).** Trendyol → Hepsiburada → softcotton → Beymen. Her biri için yalnızca doğrulanmış dokümantasyonla; önce stok/fiyat gönderme, sonra sipariş çekme, sonra iade.
Kabul: Her adaptör sandbox/test hesabı varsa onunla, yoksa kaydedilmiş örnek yanıtlarla (contract test) doğrulanır.

## 12. İlk yanıtında yapman gerekenler

1. Bu spesifikasyonda gördüğün **çelişki veya eksikleri** listele (en fazla 8 madde).
2. Bana sorman gereken **kritik soruları** sor: fiziksel stok başlangıçta nerede tutulacak (Excel/ERP/bu uygulama), tek depo mu çoklu depo mu, softcotton.com.tr altyapısı nedir, Trendyol/Hepsiburada API erişim bilgileri ve dokümanları var mı.
3. Aşama 0 için planını sun ve yanıtlarımı bekle. Henüz kod yazma.