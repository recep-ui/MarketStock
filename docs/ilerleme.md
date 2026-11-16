# İlerleme Denetimi: Devir Raporu

**Tarih:** 2026-11-16
**Denetleyen:** Claude Code (Antigravity'den devralma)
**Yöntem:** Tüm kaynak dosyalar okundu, derleme ve testler bu makinede çalıştırıldı. Önceki raporlar dikkate alınmadı.
**Not:** Bu denetimde kod değiştirilmedi. Yeni eklenen tek dosya bu rapor.

---

## 0. Güncelleme (2026-11-16, devralma sonrası ilk düzeltmeler)

| Konu | Durum |
|---|---|
| Derleme (NU1903) | ✅ Düzeltildi: `Newtonsoft.Json 13.0.4` geçişli olarak sabitlendi (ADR-006). `dotnet build`: 0 hata, 0 uyarı. |
| Testler | ✅ `dotnet test` bastırma olmadan: Unit 18/18, Integration 2 geçti, 1 atlandı (gerçek DB testi). |
| .NET sürümü | ✅ .NET 10 kararı ürün sahibince onaylandı (ADR-002). |
| SQL sunucusu | Hedef: SQL Server 2025 (17.0) **Express**, collation `Turkish_CI_AS` (ADR-003). |
| Veritabanı oluşturma | ❌ **Engelli.** `ms` login'inin `CREATE DATABASE` yetkisi yok (sysadmin=0, dbcreator=0). Uygulama denedi → **SQL hata 262**; `/health` → **503**. Çözüm: bir sysadmin `db/provision/001_create_database.sql` script'ini çalıştırmalı. |
| Gizli bilgi | ✅ Test dosyasındaki gerçek SQL parolası sahte değerle değiştirildi. Bağlantı dizesi yalnızca user-secrets'ta. `*.xlsx` `.gitignore`'a eklendi (repo public). |
| ADR | ✅ `docs/decisions.md` oluşturuldu (ADR-001…007). |
| Migration aracı | DbUp seçildi (ADR-004); henüz uygulanmadı (Aşama 1). |
| Docker | ❌ Bu makinede hâlâ kurulu değil. `docker compose up` ve Testcontainers testleri çalıştırılamıyor. |

**Aşama 0 kabul kriterleri hâlâ kanıtlanmadı:** `/health` 200 için veritabanının oluşturulması, `docker compose up` için Docker kurulumu gerekli.

---

## 1. Repo yapısı ve git geçmişi

- **Git deposu yok.** `git status` → `fatal: not a git repository`. Geçmiş, commit veya dal bilgisi yok; ne zaman, hangi sırayla yapıldığı izlenemiyor.
- `docs/` klasörü yoktu (bu rapor ilk dosyası). `Directory.Packages.props` içinde atıf yapılan `docs/decisions.md ADR-005` **mevcut değil**.
- Kökte iki Excel dosyası var: `Trendyol Ürünler.xlsx`, `Beymen Ürünler .xlsx` (içerikleri bu denetimde açılmadı; Aşama 2 ve 6 için girdi olabilir).

```
PazaryeriHub.slnx
├─ src/
│  ├─ PazaryeriHub.Domain           (yalnızca AssemblyMarker)
│  ├─ PazaryeriHub.Application      (yalnızca AssemblyMarker; IChannelAdapter YOK)
│  ├─ PazaryeriHub.Infrastructure   (Hangfire, sağlık kontrolleri, DB bootstrap, log maskeleme)
│  ├─ PazaryeriHub.Api              (Program.cs, /health, /health/live, Dockerfile)
│  └─ PazaryeriHub.Adapters.{Trendyol,Hepsiburada,Beymen,Softcotton}  (yalnızca AssemblyMarker)
└─ tests/
   ├─ PazaryeriHub.Tests.Unit        (18 test: mimari bağımlılık kuralları + log maskeleme)
   └─ PazaryeriHub.Tests.Integration (3 test: /health)
```

Toplam ~20 kaynak dosya. Alan modeli, SQL şeması, saklı yordam, adaptör sözleşmesi, panel **hiç yok**.

---

## 2. Aşama durumları

| Aşama | Durum | Gerekçe (dosyaya dayalı) |
|---|---|---|
| 0: İskele | **Kısmi** | Solution yapısı, Serilog, Hangfire, `/health` var. Ancak **derleme kırık** (bkz. §3), Docker ile doğrulanmadı, Testcontainers yok, panel servisi compose'da yok, `docs/decisions.md` yok, spesifikasyondan teknoloji sapmaları var (§5). |
| 1: Veritabanı + stok motoru | **Yok** | Hiç tablo, migration, saklı yordam, `StockAllocator` yok. DbUp paketi bile referanslı değil. |
| 2: Katalog + Excel | **Yok** | Controller/endpoint, EF Core, Excel kütüphanesi yok. |
| 3: Sipariş çekirdeği | **Yok** | `IChannelAdapter`, `NormalizedOrder`, durum makinesi, mock adaptör yok. |
| 4: Senkron hattı | **Yok** | Outbox, SyncQueue, ilgili job'lar yok. Yalnızca geçici `HeartbeatJob` var. |
| 5: Panel | **Yok** | React/Vite projesi yok. |
| 6: Gerçek adaptörler | **Yok** | Adaptör projeleri boş işaretçi sınıflardan ibaret. |

---

## 3. Çalıştırma sonuçları (gerçek çıktı)

### 3.1 `docker compose up`
**Çalıştırılamadı.** Bu makinede Docker kurulu değil:
```
docker: command not found
```
(PowerShell'de `Get-Command docker` boş, `C:\Program Files\Docker` yok, Docker servisi yok.)

Ayrıca: Docker imaj derlemesi de muhtemelen başarısız olur, çünkü Dockerfile `dotnet restore` çalıştırıyor ve aşağıdaki NU1903 hatası `Directory.Build.props` üzerinden imaj içinde de geçerli (doğrulanmadı, akıl yürütme).

### 3.2 `dotnet build PazaryeriHub.slnx`: BAŞARISIZ
```
error NU1903: Hata Olarak Uyarı: 'Newtonsoft.Json' 11.0.1 paketinde önem derecesi yüksek olan
bilinen bir https://github.com/advisories/GHSA-5crp-9r3c-p9vr güvenlik açığı var
  (Api, Infrastructure, Tests.Unit, Tests.Integration)
Oluşturma BAŞARISIZ OLDU.  0 Uyarı, 4 Hata
```
Neden: `Hangfire.Core` → `Newtonsoft.Json >= 11.0.1` geçişli bağımlılığı. `TreatWarningsAsErrors=true` olduğu için güvenlik uyarısı hataya dönüşüyor. **Mevcut haliyle `dotnet test` de çalışmaz.**

### 3.3 `dotnet test` (yalnızca tanı amaçlı: `-p:WarningsNotAsErrors=NU1903`, dosya değiştirilmedi)
```
Tests.Unit        : Başarılı 18, Başarısız 0, Atlanan 0, Toplam 18
Tests.Integration : Başarılı 2,  Başarısız 0, Atlanan 1, Toplam 3
  [SKIP] HealthEndpointTests.Gercek_veritabaniyla_health_200_doner
         (PAZARYERIHUB_TEST_DB ortam değişkeni tanımlı değil)
```
- Geçen entegrasyon testleri yalnızca **"DB'ye ulaşılamıyorsa 503"** ve **"/health/live 200"** senaryoları.
- **"/health 200" senaryosu hiç çalışmadı.** Makinede `MSSQL$SQLEXPRESS` servisi çalışıyor, ancak test veritabanı yaratmak makinede değişiklik olacağı için bu denetimde kullanılmadı.

### 3.4 Hiç yazılmamış testler
- Testcontainers (MSSQL) altyapısı: paket bile yok.
- Aşama 1-5 kabul testlerinin tamamı (paralel rezervasyon, rezerve→iptal→sevk, hareket+outbox, allocator, 1.000 satır Excel, 5× aynı sipariş, durum geçişi, ≤30 sn senkron, 5 deneme→HATA, Playwright 3 senaryo).

---

## 4. Kabul kriterleri: kanıt durumu

| Aşama | Kriter | Durum |
|---|---|---|
| 0 | `docker compose up` ile API + MSSQL ayağa kalkar | ❌ Kanıtlanmadı (Docker yok; derleme kırık) |
| 0 | `/health` 200 döner | ❌ Kanıtlanmadı (ilgili test SKIP) |
| 1 | (a) 100 paralel rezervasyonda stok eksiye düşmez | ❌ Yok |
| 1 | (b) rezerve→iptal→sevk bakiyeleri doğru | ❌ Yok |
| 1 | (c) her değişiklikte hareket + outbox | ❌ Yok |
| 1 | (d) allocator birim testleri | ❌ Yok |
| 2 | 1.000 satır Excel, hatalı satırlar satır no ile | ❌ Yok |
| 3 | Aynı sipariş 5× → tek kayıt, tek rezervasyon | ❌ Yok |
| 3 | Geçersiz durum geçişi reddedilir | ❌ Yok |
| 4 | Stok değişimi → ≤30 sn mock kanalda | ❌ Yok |
| 4 | 5 denemeden sonra HATA + uyarı | ❌ Yok |
| 5 | Ekranlar + Playwright 3 senaryo | ❌ Yok |
| 6 | Contract/sandbox testleri | ❌ Yok |

Kanıtlanmış olanlar (spec kabul kriteri değil, ama mevcut): katman bağımlılık kuralları (hub-and-spoke) ve log maskeleme birim testleri, DB yokken `/health` 503 ve gizli bilgi sızdırmama.

---

## 5. Spesifikasyondan sapmalar

### 5.1 Teknoloji (Bölüm 2: "gerekçesiz sapma yok")
| Spesifikasyon | Mevcut | Not |
|---|---|---|
| .NET 8 | `net10.0` (`Directory.Build.props`, `global.json` 10.0.100, Dockerfile `sdk:10.0`) | Gerekçe/ADR yok. |
| SQL Server 2019/2022 | `mcr.microsoft.com/mssql/server:2025-latest` | Gerekçe/ADR yok; `-latest` etiketi sabitlenmemiş. |
| Dapper + EF Core | İkisi de yok (henüz gerekmedi) | |
| Testcontainers (MSSQL) | Yok; gerçek DB testi ortam değişkenine bağlı ve atlanıyor | |
| docker-compose: api, mssql, **panel** | Yalnızca api + mssql | Panel Aşama 5'e ertelenmiş; kabul edilebilir ama not edilmeli. |
| Migration aracı seçimi + ADR | Yorumlarda "DbUp" ve `db/provision` script'leri geçiyor; **ikisi de yok**, ADR yok | |
| Polly | Yok (adaptörler gelince gerekecek) | |

### 5.2 Veritabanı (Bölüm 4)
Şema hiç yazılmadığı için **kolon adları, collation'lar (`Latin1_General_100_BIN2`), saklı yordamlar (tek transaction + hareket + outbox, `THROW 50001`), filtreli unique index (`SyncQueue ... WHERE Durum='BEKLIYOR'`), `UNIQUE(ChannelId, KanalSiparisNo)` idempotentliği** kontrol edilecek nesne yok. Hepsi **eksik**.

Mevcut DB ile ilgili tek şey `DatabaseBootstrapper`: DB'yi `COLLATE Turkish_CI_AS` ile yaratıyor, RCSI ve `ALLOW_SNAPSHOT_ISOLATION` açıyor. Bu spesifikasyona uygun. `SqlServerHealthCheck` bu ayarlar kapalıysa `Degraded` döndürüyor; bu iyi bir davranış.

### 5.3 Diğer gözlemler
- Hangfire dashboard yalnızca Development ortamında ve yetkisiz. Aşama 5'te yetkilendirme gerekli (kodda not var).
- `HeartbeatJob` geçici; Aşama 4'te kaldırılmalı.
- `appsettings.json` içindeki Serilog `Using` listesinde `Serilog.Sinks.Console` ve `Serilog.Formatting.Compact` doğrudan referanslı değil; `Serilog.AspNetCore` üzerinden geçişli geliyor. Çalışıyor ama kırılgan.
- `.env` yok (`.env.example` var); `.gitignore` `.env`'i dışlıyor, bu doğru.

---

## 6. Uydurulmuş (doğrulanmamış) pazaryeri API çağrısı

**Yok.** Dört adaptör projesi yalnızca boş `*AdapterAssemblyMarker` sınıfı ve `// TODO: doğrulanmalı` notu içeriyor. Kaynak kodda hiçbir pazaryeri URL'si, uç noktası veya alan adı bulunmuyor (grep ile tarandı).

---

## 7. Kaldığımız yer

**Aşama 0, kısmi.** Aşama 0 kabul kriterleri kanıtlanmadan Aşama 1'e geçilmemeli.

## 8. Düzeltilmesi gerekenler (öncelik sırasıyla)

1. **Derlemeyi düzelt:** `Newtonsoft.Json` için güvenli sürümü (≥13.0.1) `Directory.Packages.props`'ta merkezi olarak sabitle (transitive pinning). Uyarıyı susturmak yerine açığı kapatmak tercih edilmeli.
2. **Git deposu başlat** ve mevcut durumu ilk commit olarak kaydet (onayınla).
3. **Teknoloji sapmalarına karar ver:** .NET 8'e mi dönülecek, yoksa .NET 10 + SQL Server 2025 mi korunacak? Hangisi seçilirse `docs/decisions.md`'ye ADR olarak yazılmalı. SQL imajı sabit bir etikete bağlanmalı (ör. `2022-CU…`).
4. **`docs/decisions.md` oluştur:** atıf yapılan ADR-005 (FluentAssertions 7.x lisans) dahil, mevcut kararları geriye dönük kaydet; migration aracı (DbUp) kararını ekle.
5. **Testcontainers.MsSql ekle** ve `Gercek_veritabaniyla_health_200_doner` testini ortam değişkeni yerine konteynerle çalışır hale getir (Docker gerektirir).
6. **Docker kurulumu:** Bu makinede Docker Desktop yok. Aşama 0 kabulü ve Aşama 1'deki tüm Testcontainers testleri için gerekli. Kurulum senin tarafından yapılmalı.
7. compose'a panel servisi için yer tutucu bırakıp bırakmamaya karar ver (Aşama 5'e ertelenebilir).

## 9. Devam planı

1. **Aşama 0'ı kapat:** 1–5. maddeler → `dotnet build` temiz, `dotnet test` (Testcontainers dahil) yeşil, `docker compose up --build` + `curl /health` → 200 kanıtı.
2. **Aşama 1:** DbUp ile sürümlü SQL script'leri (tüm tablolar, BIN2 collation'lar, indeksler, filtreli unique'ler), 6 saklı yordam (`XACT_ABORT`, tek transaction, hareket + outbox), `StockAllocator` + kabul testleri (a)–(d).
3. Sonrası CLAUDE.md'deki sıraya göre.

## 10. Açık sorular (CLAUDE.md §12'den, hâlâ yanıtsız)
- Fiziksel stok başlangıçta nereden gelecek (Excel/ERP/bu uygulama)?
- Tek depo mu, çoklu depo mu?
- softcotton.com.tr altyapısı nedir (Ticimax/IdeaSoft/Shopify/WooCommerce…)?
- Trendyol/Hepsiburada API erişim bilgileri ve dokümanları var mı? Beymen entegrasyon yöntemi?
- Kökteki `Trendyol Ürünler.xlsx` / `Beymen Ürünler .xlsx` dosyaları neyi temsil ediyor (kanal ürün dışa aktarımı mı, eşleştirme kaynağı mı)?
