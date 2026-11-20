# Canlıya geçiş denetimi (2026-11-20)

**Kapsam:** Trendyol ve Beymen'de canlı **okuma** testleri; panelin tüm ekranları gerçek veriyle; otomatik testlerin tamamı; kod ve yapılandırma denetimi.
**Yapılmayan:** Canlı kanallara **yazma** (stok, fiyat, sipariş durumu, iade kararı, soru cevabı). Gerçek mağazayı etkiler; CLAUDE.md gereği yalnızca mock ve contract testiyle doğrulanır. Hepsiburada, canlı API bilgisi gelene kadar test (SIT) ortamında (ADR-037).

## Sonuç özeti

| Alan | Sonuç |
|---|---|
| Otomatik testler | Unit 368/368, entegrasyon 149/149, Playwright 17/17 |
| Trendyol canlı okuma | ✅ Ürün kataloğu (584 satır, 584 eşleşti, 24 pasif) · stok mutabakatı (560 ilan, 485 kanalda bulundu, **75 bulunamadı**, **23 sapma**) · kargo firmaları (8) ve iade red gerekçeleri (15) · iade talepleri (1) · müşteri soruları · siparişler (çekim penceresinde yeni sipariş yok) |
| Beymen canlı okuma | ✅ Ürün kataloğu (200 satır, 194 eşleşti, 6 eşleşmedi) · stok mutabakatı (194/194 bulundu, **3 sapma**) · kargo firmaları (32) ve red gerekçeleri (12) · iade talepleri · sorular · siparişler (4 sipariş; biri "stok yetersiz") |
| Kanal çağrıları | Tümü GET ve HTTP 200; kanala hiçbir yazma isteği gitmedi |
| Panel ekranları (15 görünüm, gerçek veri) | ✅ Hepsi 1–2 sn'de açıldı; hata kutusu, sayfa hatası ya da başarısız API çağrısı yok |

## Bulgular (önem sırasıyla)

### 🔴 1. Yazma kilidi kalktığı anda 754 ilana birden stok gönderilir
- Trendyol ve Beymen'de **STOK yeteneği AÇIK** ve 778 ilanın hiçbirine şimdiye kadar stok gönderilmemiş. Ayrıca 3 işlenmemiş "kanal stoğunu yenile" olayı bekliyor.
- Bu veritabanıyla canlıya geçilirse ilk çalışmada **754 aktif ilanın stoğu defterdeki değerle değişir**:
  - sapmalı ilanların gerçek kanal stoğu değişir (Trendyol 23, Beymen 3);
  - kanalda bulunamayan 75 Trendyol ilanı hata verir.
- **Yapılacaklar (kilit kalkmadan önce):**
  1. Mutabakatı çalıştırın. Sapmaları Kanal Stoğu → Defter ile ya da sayımla kapatın; defter doğru olmalı.
  2. Kanalda bulunamayan 75 Trendyol ilanını inceleyin (Stok Listesi → Trendyol sekmesi): silinmiş ya da arşivlenmiş olanları PASİF yapın.
  3. STOK yeteneğini önce **DENEME** (Yönetici onayı) durumunda açın, ilk gönderimleri izleyin, sonra AÇIK yapın.

### 🔴 2. KDV'si doğrulanmamış fiyat gönderilebiliyordu — DÜZELTİLDİ (ADR-039)
- Hepsiburada **FİYAT yeteneği AÇIK** görünüyordu. KDV durumu resmi dokümanda doğrulanmadığı için bu yeteneğin açılamaması gerekiyordu (ADR-028).
- Neden: kural yalnızca yetenek değiştirilirken, o an kayıtlı adaptöre göre kontrol ediliyordu. Kanal mock'tayken açılmış, gerçek moda geçince AÇIK kalmıştı.
- **Düzeltme:** `GuardedChannelAdapter.PushPriceAsync` artık gönderim anında adaptörün kuralına bakıyor; izin yoksa istek kanala çıkmıyor, kayıtlar hata sayılmadan bekliyor. Birim testi eklendi.
- **Yapılacak:** Ayarlar → Kanallar → Hepsiburada → FİYAT yeteneğini KAPALI yapın (artık zararsız, ama yanıltıcı).

### 🔴 3. Sunucuda/konteynerde API bilgileri kaybolabilir
- Kanal API bilgileri `IDataProtector` ile şifreleniyor. Anahtarlar `DataProtection:KeysPath` verilmezse kullanıcı profilinde tutuluyor.
- `docker-compose.yml`'de bu ayar ve bunun için bir volume yok. Konteyner yeniden oluşturulunca anahtarlar silinir, **kayıtlı tüm kanal bilgileri çözülemez** ve kanallar "API bilgisi hatalı" durumuna düşer.
- **Yapılacak:**
  - `DataProtection__KeysPath` ayarını bir volume'a bağlayın, anahtar klasörünü yedekleyin.
  - Canlı sunucu ilk kurulduğunda API bilgilerini o sunucuda yeniden girin. Test makinesindeki şifreli değerler başka makinede çözülemez.

### 🟠 4. İlk sipariş çekimi yalnızca 24 saat geriye bakar
- `Siparis:InitialLookbackHours = 24`. Hiç çekim yapılmamış (yeni) bir veritabanında, 24 saatten eski açık siparişler (Yeni / Hazırlanıyor) içe alınmaz.
- Bu siparişlerin stoğu rezerve edilmez, ilk stok gönderimi bu adetleri satılabilir sayar: **fazla satış riski**.
- Bu deneme veritabanı kullanılırsa sorun yok, çünkü son başarılı çekim zamanı kayıtlı.
- **Yapılacak:** Yeni veritabanıyla geçilecekse ilk çalıştırmada `InitialLookbackHours` değerini açık siparişleri kapsayacak kadar büyük verin (ör. 336 = 14 gün; kanal sınırlarına dikkat), sonra 24'e döndürün.

### 🟠 5. Kanalda bulunamayan ilan eski kanal stoğunu gösteriyor
- Son mutabakatta bulunamayan 19 Trendyol ilanı, ekranda hâlâ 18 Kasım'daki kanal stoğunu gösteriyor (ör. `AxelH50100000` → "kanalda 100, 18.11"). Sapma hesabı da bu eski değerle yapılıyor.
- Çekim zamanı satırda yazıyor, ama kolayca gözden kaçıyor.
- **Öneri:** Bu çekimde bulunamayan ilan "kanalda yok (son çekimde bulunamadı)" diye gösterilsin, sapma hesabına girmesin. Ayrı iş olarak önerildi.

### 🟠 6. HTTPS arkasında oturum çerezi "Secure" olmayabilir
- Çerez `SecurePolicy = SameAsRequest`. Uygulama ileri vekil (nginx, IIS, yük dengeleyici) arkasında çalışacak ama `UseForwardedHeaders` yok.
- TLS vekilde sonlanırsa uygulama isteği HTTP görür ve çerez Secure olmadan gider.
- **Yapılacak:** Canlıda `ForwardedHeaders` ekleyin ya da çerezi `Always` yapın; vekilde HTTPS zorunlu olsun.

### 🟠 7. Migration'lar canlıda otomatik çalışmaz
- `Database:MigrateOnStartup = false` (varsayılan). Bu veritabanında 0015 (eşleşme onayı) dahil şema güncel.
- Yeni bir canlı veritabanında migration'lar ya elle çalıştırılmalı ya da ilk açılışta `Database__MigrateOnStartup=true` verilmeli. Aksi halde yeni ekranlar hata verir.

### 🟠 8. Canlıda hiç denenmemiş yazma uçları
- Adaptörlerde "doğrulanmalı" notu sayısı: Trendyol 12, Beymen 9, Hepsiburada 13. Yazmayla ilgili olanlar ilk canlı kullanımda kontrol edilmeli:
  - Trendyol: paket zaten "Picking" iken tekrar gönderimin yanıtı; alternatif teslimatın hesapta açık olması gerekip gerekmediği.
  - Trendyol: iade kararında birden fazla claim kaleminin sorgu biçimi.
  - Beymen: picking yanıtı ve tekrar çağrı; müşteriye gösterilen fiyatın platformPrice olması; soru cevabı uzunluk sınırı.
- **Yapılacak:** SİPARİŞ_DURUM, İADE_KARAR ve SORU_CEVAP yeteneklerini ilk hafta **DENEME** (Yönetici onayı) durumunda açın.
  - Şu an bu deneme veritabanında hepsi AÇIK.
  - Kanal İşlemleri ekranında ilk gönderimlerin sonucunu izleyin.

### 🟡 9. docker-compose canlı için uygun değil
- `MSSQL_PID: Developer`: Developer sürümü üretimde lisanslı değil; Express ya da Standard kullanın.
- API `sa` hesabıyla bağlanıyor; yalnızca bu veritabanına yetkili ayrı bir kullanıcı kullanın.
- Loglar konteyner içindeki `logs/` klasörüne yazılıyor; volume'a bağlayın, yoksa konteyner yenilenince kaybolur.

### 🟢 10. Küçükler
- `deneme-modu.ps1 -File … -GercekKanal A,B`: virgüllü liste reddediliyordu. **Düzeltildi**; liste artık betik içinde bölünüp doğrulanıyor.
- Panelde favicon yok (giriş sayfasında 404); yalnızca kozmetik.
- Hepsiburada: canlı API için Hepsiburada'nın iki yanıtı bekleniyor (envanter güncelleme 400, test siparişi ShippingMethodId). Ayrıntılar ADR-037'de.

## Canlıya geçiş kontrol listesi
1. [ ] Canlı sunucu: `DataProtection__KeysPath` (kalıcı ve yedekli), HTTPS ve ileri vekil başlıkları, SQL kullanıcısı (sa değil), lisanslı SQL sürümü, log volume'u.
2. [ ] Veritabanı: migration'lar uygulanmış mı (0001–0015)? Yeni veritabanıysa ilk sipariş çekimi için geniş geriye bakış (madde 4).
3. [ ] Kanal API bilgileri canlı sunucuda girilmiş ve ortamları **Canlı**. Hepsiburada: canlı bilgi geldikten sonra.
4. [ ] Mutabakat çalıştırıldı; sapmalar kapatıldı; kanalda bulunamayan ilanlar pasif (madde 1).
5. [ ] Yetenekler:
   - FİYAT KAPALI (KDV teyidi gelene kadar; Beymen hariç, o da DENEME ile).
   - STOK önce DENEME.
   - SİPARİŞ_DURUM, İADE_KARAR, SORU_CEVAP önce DENEME.
6. [ ] `Kanallar__YazmaKapali` kaldırıldı ve kanallar `Kanallar__{KOD}__Mod=Gercek`. Zamanlanmış işlerin cron'ları açık.
7. [ ] İlk gün: Senkron Merkezi ve Kanal İşlemleri'nde hata, Genel Bakış'ta kanal durumu ve "stok yetersiz" siparişler izlenir.
