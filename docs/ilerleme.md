# İlerleme Denetimi: Devir Raporu

**Tarih:** 2026-11-16
**Denetleyen:** Claude Code (Antigravity'den devralma)
**Yöntem:** Tüm kaynak dosyalar okundu, derleme ve testler bu makinede çalıştırıldı. Önceki raporlar dikkate alınmadı.
**Not:** En güncel durum en üstteki bölümdedir; §1 ve sonrası ilk devralma denetiminin (değiştirilmemiş) kaydıdır.

---

## Hepsiburada SIT: katalog testi kabul, envanter güncelleme çalıştı (2026-11-21)

- HB destek: test ortamında "Eşleşen" statüsüne çekilemiyor; ürünlerin "İncelenecek"e düşmesi ve onay/red çağrılarının hatasız kayıtları yeterli → **katalog testi tamamlandı**.
- `inventory-uploads` 400 çözüldü. HB'nin verdiği gövde (`price` metin, `dispatchTime`, `maximumPurchasableQuantity`) charset ekli başlıkla yine 400 döndü; `Content-Type: application/json` (eksiz) ile `200 {"id"}` → `Done`, hata yok (rapor `hb-sit-rapor/20261121-172909-envanter`, git dışı).
- SIT istemcisi: envanter gövdesi HB örneğine göre, gövdeli isteklerde eksiz Content-Type. Araca yalnızca envanter yazan `envanter` adımı eklendi (listingin kendi stok/fiyat/kargoya veriliş değerleriyle).
- Contract test: gövde alan sırası/tipleri ve Content-Type. Unit 410/410.
- Uygulama adaptörü değişmedi (stok/fiyat `stock-uploads` / `price-uploads` ile, SIT'te zaten 200).

## Eşleştirme listesinde kanal ürünü ↔ SKU yan yana (2026-11-21, ADR-044)

- Eşleştirme → "Eşleştirmeler" tablosu: kanaldaki görsel/ad/kodlar/varyant/durum → bağlı SKU'nun ürün adı/renk-beden/barkodu, eşleşme dayanağı rozeti; uyarılı satırdan kanal sekmesine bağlantı.
- Arama ürün adı ve kanaldaki adı da kapsar.
- Testler: unit 410/410, entegrasyon 152/152, Playwright 19/19.

## Kanal sekmesinde kanaldaki durum sütunu ve süzgeci (2026-11-21, ADR-043)

- Soru: Trendyol/Beymen'den çekilen ürünler yalnızca satışta olanlar mı? Hayır: katalog çekimi süzgeçsiz, satış dışı ürünler de geliyor (deneme DB: Trendyol 412/584, Beymen 50/194 satışta).
- Stok Listesi → kanal sekmesi: "Kanaldaki durum" sütunu (Satışta / Stoğu tükendi / Satış dışı / Bilinmiyor + kanalın metni) ve adetli süzgeç.
- İlan durumu ve stok gönderimi değişmedi (otomatik pasif yapılmaz).
- Testler: unit 410/410, entegrasyon 152/152, Playwright 18/18.
- Bilinen: Beymen API ile çekilen "Beymen'e Seçilmedi" ilanlar PASIF yapılmaz (Excel içe aktarmada yapılır); tutarsızlık bilerek bırakıldı, istenirse hizalanır.

## Hepsiburada SIT: katalog ürün gönderimi tamam (2026-11-21)

- Hepsiburada destek: "kategorileri listeleyip ardından özellikleri listeleyerek ürün gönderimi yapmanızı bekliyoruz".
- Araçla sırayla çalıştırıldı (yalnızca SIT): `katalog-kesif` (kategori ağacı → Bornoz 60005084) → `katalog-kesif --kategori 60005084` (özellikler + enum değerleri) → `urun-gonder`.
- Royal Kimono: trackingId `821a7185-…`, `importStatus=SUCCESS`, ürün **İncelenecek**. Kod değişmedi; dünkü 3003 "File not found" aynı istekle bugün oluşmadı.
- Eşleşme testi (dokümandaki 3 test barkodu): trackingId `c579a029-…`, üçü de SUCCESS / İncelenecek. Onay (…0114) ve red (…0155) uçları 200; ürünler "Eşleşen" olmadığı için durum değişmedi. "Eşleşen" durumuna geçiş HB tarafında; HB'ye soruldu.
- Doküman yeni portala taşınmış (developers.hepsiburada.com/tr/companies/hepsiburada); uç ve biçim değişmemiş.
- Raporlar: `hb-sit-rapor/20261121-*` (git dışı).

---

## Tek yerden stok + fiyat, ilan stok kuralı, kanal görselleri, Trendyol/Beymen fiyat yeteneği (2026-11-20): TAMAM

- **Fiyat (ADR-041):** Trendyol satış fiyatı kullanıcı onayıyla KDV dahil kabul edildi; Trendyol ve Beymen FIYAT yeteneği açılabilir. Deneme veritabanında Trendyol ve Beymen FIYAT = ACIK, Hepsiburada FIYAT = KAPALI yapıldı (önceden yanlışlıkla ACIK'tı). Yazma kilidi açık olduğu için deneme modunda kanala fiyat gitmez; canlı kurulumda kilit kalkınca gider.
- **İlan stok kuralı (ADR-042):** Ortak havuz / Üst limit / Sabit; defteri asla aşmaz. Migration 0017. `GET/PUT /api/stock/listings/{id}/rule` → outbox → mevcut stok gönderim hattı.
- **Görseller:** Trendyol (`images[].url`, gerçek yanıt mutlak https: deneme katalog okumasında 200/200 ilanda görsel) ve Beymen (`thumbnailPath`, 187/194). Hepsiburada listing yanıtında görsel yok.
- **Panel:** Kanal sekmesinde görsel, kanalda görünen fiyat, stok kuralı ve ilan fiyatı aynı satırda; tek "Değişiklikleri kaydet". Eşleşmemiş kanal ürünleri aynı sekmede görselli listelenebilir. SKU detayında kanalların görselleri yan yana; Stok Listesi ve Eşleştirme'de küçük resim.
- **Bilinen eksik:** Deneme veritabanındaki SKU'ların temel satış fiyatı boş ("SKU fiyatı yok"); fiyat yalnızca ilana özel fiyat girilen ilanlar için hesaplanır. Fiyat ekranından veya ilan fiyatıyla doldurulmalı.
- **Testler:** unit 392/392, entegrasyon 151/151, Playwright 18/18. Deneme modunda gerçek verilerle ekran kontrolü (kırık görsel 0).

---

## Kanalda bulunamayan ilan eski kanal stoğunu göstermiyor (2026-11-20): TAMAM

- Denetim madde 5: son mutabakatta kanalda bulunamayan ilan eski kanal stoğunu güncel gibi gösteriyor, sapma eski değerle hesaplanıyordu.
- Mutabakat artık bulunamayan ilan için `KanalStok = NULL` snapshot yazar (migration 0016, ADR-040).
- Stok Listesi (defter ve kanal sekmeleri), SKU detay, Senkron Merkezi ve Kanal Stoğu → Defter "kanalda yok" gösterir; bu ilanlar sapma sayılmaz. Kanal sekmesinde "son çekimde kanalda bulunamadı" sayısı.
- Eski snapshot satırları değişmez. Deneme veritabanındaki 19 ilan bir sonraki Trendyol mutabakatından sonra "kanalda yok" görünür (migration uygulanmış olmalı).
- **Testler:** unit 372/372 (yeni 4 + migration listesi), entegrasyon 150/150 (yeni: bulunamayan ilan sapma saymaz, "kanalda yok" döner, kanalda yeniden görünürse değer geri gelir), Playwright 17/17.

---

## Hepsiburada SIT: sipariş akışı tamam, tam ürün gönderme bekliyor (2026-11-20)

- **Destek yanıtı:**
  - Sipariş örneğindeki `CargoCompanyId: 21`, `DeliveryOptionId: 1` ve `OrderDate` ile test siparişi oluştu (201).
  - Sipariş listelendi ve uygulamanın adaptörüyle paketlendi: 201 `{packageNumber, barcode}`.
- **Tam ürün gönderme akışı:**
  - Araçta yeni adımlar: `katalog-kesif` (kategori, özellik, değer; yalnızca okuma), `urun-gonder --dosya`, `eslesme --onay/--red`.
  - Kategori, özellik ve değer uçları çalışıyor.
  - Royal Kimono (Bornoz 60005084) gönderimi "File not found" (3003) aldı; çalışan curl örneği Hepsiburada'dan istendi.
- **Hepsiburada'ya yanıt taslağı:** `hb-sit-rapor/hepsiburada-yanit-taslagi.md` (stok, fiyat, envanter istekleri, sipariş sonuçları, katalog sorusu).
- **Testler:** HepsiburadaSitClient contract testleri 18/18.

---

## Canlıya geçiş denetimi (2026-11-20): TAMAM — rapor `docs/canliya-gecis-denetimi.md`

- **Otomatik testler:** unit 368/368, entegrasyon 149/149, Playwright 17/17.
- **Trendyol ve Beymen canlı okuma:** katalog, stok mutabakatı, referanslar, iade, soru ve sipariş çekme başarılı; tüm çağrılar GET/200, kanala yazma yok. Panelin 15 görünümü gerçek veriyle hatasız açıldı.
- **Düzeltilen:**
  - Fiyat yeteneği mock'tayken açılmışsa KDV'si doğrulanmamış fiyat canlıya gidebiliyordu; gönderim anında da engelleniyor (ADR-039).
  - `deneme-modu.ps1 -File … -GercekKanal A,B` virgüllü liste kabul ediyor.
- **Canlıdan önce yapılacaklar** (rapordaki kontrol listesi):
  - İlk stok gönderimi 754 ilanı birden değiştirir; önce sapmaları kapatın, STOK'u DENEME ile açın.
  - DataProtection anahtar klasörü kalıcı olmalı.
  - İlk sipariş çekimi 24 saat geriye bakıyor.
  - Çerez ve ileri vekil başlıkları.
  - Migration'lar.
  - Denenmemiş yazma uçları için yetenekler DENEME ile açılmalı.

---

## Kanal Stoğu / Eşleştirme: neden boş olduğunu söyleyen uyarılar (2026-11-20): TAMAM

- Hepsiburada test ortamındayken çekilen ürünler eşleşmiyor ve Kanal Stoğu → Defter boş kalıyordu. Ekran ise kullanıcıyı işe yaramayan mutabakata yönlendiriyordu.
- **Yeni uyarılar:**
  - Kanal mock modda ya da test ortamındaysa uyarı.
  - Kanalda eşleşmiş ilan yoksa "N ürün eşleşme bekliyor → Eşleştirme".
  - Katalog raporunda hiç eşleşme yoksa nedenleri (stok kodu uyuşmuyor, barkod yok, barkodsuzdan SKU oluşmaz).
- Sunucu ve eşleştirme kuralları değişmedi (ADR-038).
- **Testler:** Playwright 17/17 (yeni 1, sunucu yanıtları yakalanarak belirli). Unit ve entegrasyon testleri etkilenmedi; yalnızca panel değişti.

---

## Hepsiburada test ortamı (SIT) doğrulama aracı (2026-11-19): KISMEN (Hepsiburada'dan iki yanıt bekleniyor)

- Hepsiburada canlı API için test ortamında katalog, listeleme ve sipariş adımlarını istiyor. Tek seferlik araç: `dotnet run --project tools/PazaryeriHub.Tools.HbSit -- <kontrol|katalog|listeleme|siparis|hepsi>`. Yalnızca `*-sit.hepsiburada.com`'a gider, panel bilgisini okur, veritabanına yazmaz (ADR-037).
- **Sonuçlar:**
  - ✅ Katalog: trackingId alındı.
  - ✅ Stok ve fiyat gönderimi + sorgulama (Done), activate/deactivate, listing sorgulama.
  - ⚠️ Toplu kilit kaldırma: uç çalışıyor.
  - ❌ Envanter güncelleme: 400 "deserializing input data".
  - ❌ Test siparişi: "ShippingMethodId cannot be empty"; dokümanda bu alan yok. Sipariş listeleme ve paketleme bu yüzden çalıştırılamadı.
- **Yan bulgu:** Hepsiburada listing yanıtında `productName` yok; kanal sekmesinde Hepsiburada ürün adı bu yüzden boş.
- **Testler:** Unit 367/367 (yeni 14). Entegrasyon çalıştırılmadı: deneme modu açıktı ve API dosyalarını kilitliyordu; uygulama davranışı değişmedi (adaptörde yalnızca yorum, yeni istemciyi API kullanmıyor).

---

## "Doğru eşleşme" onayı (2026-11-19): TAMAM

- Kanal sekmesinde uyarılı ilan Yönetici tarafından "Doğru eşleşme, onayla" ile kapatılabilir; "Kontrol et" sayısı ve filtresinden çıkar. Uyarılar ve onaylayan/zaman satırda görünür kalır; "Onayı geri al" ile geri döner.
- Onay, eşleşme bilgisinin imzasına bağlı: bağlı SKU, barkod/kod/ad/renk/beden veya uyarılar değişirse onay kendiliğinden geçersiz olur ve uyarı geri gelir. Eşleştirme değişmez; onay geçmişi silinmez (migration 0015, ADR-036).
- **Testler:** Unit 353/353 (yeni 6), Integration 149/149 (yeni 3), Playwright 16/16 (yeni 1); iki tam çalıştırmada 16/16.

---

## Stok Listesi: kanal bazlı görünüm ve yanlış eşleştirme uyarıları (2026-11-19): TAMAM

- **Eşleştirme korundu:** kurallar ve kanallar arası ortak havuz değişmedi; yeni görünüm yalnızca okur ve uyarır.
- **Stok Listesi sekmeleri:** "Defter (tüm kanallar)" (önceki görünüm) + her pazaryeri ayrı (`?kanal=KOD`). Kanal sekmesinde her ilan için kanalın bildirdiği (barkod, kod, stok kodu, ad, renk/beden, çekilen stok ve zamanı) ile bağlı SKU ve gitmesi gereken stok yan yana.
- **"Kontrol et" uyarıları:** barkod farklı, stok kodu farklı, renk farklı, beden farklı, ad benzemiyor; eşleşme dayanağı (barkod / stok kodu / kodla doğrulanamadı). Ayrıntı ve kurallar ADR-035.
- **Kanal adı/markası** artık ilan verisine yazılıyor (katalog çekimi, kanal Excel'i, elle bağlama). Mevcut ilanlarda ad bir sonraki katalog çekiminde gelir; o zamana kadar bağlanmış eşleşmeyen kaydından okunur, yoksa "kanal adı bilinmiyor" yazar (demo ilanları bu durumda).
- **Testler:** Unit 347/347 (yeni 14), Integration 146/146 (yeni 3), Playwright 15/15 (yeni 1), art arda üç tam çalıştırmada 15/15. Senaryo demo sapmasını okuduğu için sapmayı kapatan senaryolardan önceye konuldu. Bir ara çalıştırmada 1. senaryo bilinen filtre-yenileme yarışıyla bir kez düştü (değişiklikle ilgisiz).

---

## Genel arama, SKU detay, raporlar ve sipariş Excel'i (2026-11-19): TAMAM

- **Genel arama:** üst çubukta "Hızlı bul" (`/` veya Ctrl+K). SKU kodu (büyük/küçük harf duyarsız önek), barkod, ürün adı, kanal barkodu/kodu ve sipariş no ile bulur; Enter ile SKU detayına ya da sipariş detayına gider.
- **SKU detay (`/stok/:id`):** bakiye, depolar, kanal ilanları (stok, sapma, link), fiyat, 7/30 gün net satış, son hareketler ve siparişler. Stok Listesi, kritik stok, raporlar, sipariş satırları ve aramadan açılır.
- **Raporlar ekranı:** dönem (Bugün / 7 gün / 30 gün / Bu ay / Geçen ay / özel) ve kanal seçimi; net ciro, net sipariş, adet, ortalama sepet, iptal ve iade oranları; günlük net ciro grafiği (üzerine gelince kanal kırılımı, tablo görünümü); kanal karşılaştırması; en çok satanlar; stoğu olup satmayanlar.
- **Excel:** Siparişler ekranında uygulanmış filtrelerle ve Raporlar'da seçili dönemle "Excel'e aktar" (satır bazlı, müşteri bilgisi yok).
- Ayrıntılar ve tanımlar (net = İptal ve İade hariç): ADR-034.
- **Testler:**
  - Unit 333/333 (yeni 20: dönem çözümü, UTC aralığı, gün doldurma, oranlar, arama terimi).
  - Integration 143/143 (yeni 7: arama 2, SKU detay 2, rapor 2, Excel 1).
  - Playwright 14 senaryo (yeni 2: genel arama → SKU detay → sipariş; Raporlar → Excel indirme → SKU detay; ekran turuna Raporlar eklendi).
    - Disk dolu iken (C: boş alanı çalıştırma sırasında 110 MB'a iniyordu) iade senaryosu temizlikte `ENOSPC` ile düşüyordu.
    - Kullanıcı yer açtıktan sonra (22,5 GB boş): ilk tam çalıştırmada ekran turu bir kez 10 sn beklemede zaman aşımına uğradı (tek başına geçti; ayrıntı yakalanamadı). Sonraki iki tam çalıştırma **14/14**.
- **Ortam notu:** Playwright artık kurulu Chrome ile çalışıyor (önceki denetimde Chrome yoktu; Edge 157 Playwright 1.48'in `--headless=old` bayrağını desteklemiyor).

---

## Kanal durum kartları ve anlaşılır hata gösterimi (2026-11-19): TAMAM

- **Ayarlar:** Kanallar / Stok kuralları / Kullanıcılar sekmeleri. Her kanal için durum kartı var: Gerçek/Mock, ortam, API bilgisi, yazma durumu, son çekimler. Seçilen kanalın ayarları tek yerde; yetenekler tüm kanallar için tek tabloda.
- **Genel Bakış:** Üstte kanal durumu var. Kanal işleri Türkçe adlarla gösteriliyor, hatalılar önde.
- **Hatalar:** Ham metin yerine "ne oldu + ne yapmalı" gösteriliyor; ham metin "Teknik ayrıntı"da (`GET /api/channels/status`, `errorInfo`; ADR-033).
- **Doğrulama:** Unit 313/313, Integration 136/136, Playwright 12/12 (1. senaryo bilinen soğuk başlangıç kararsızlığıyla bir kez düştü, tekrarında geçti). Deneme modunda Trendyol ve Beymen "Gerçek API · Canlı · Kilitli (deneme modu)" olarak görünüyor.

---

## Otomatik testler ayrı veritabanında (2026-11-18): TAMAM

- Entegrasyon testleri ve Playwright artık `PazaryeriHub_OtoTest` kullanıyor (yoksa oluşturuluyor). Deneme modu kullanıcının verisiyle `PazaryeriHub_Test`'te (ADR-032).
- **Doğrulama:**
  - Integration 135/135 ve Playwright 11/11.
  - Kullanıcının `PazaryeriHub_Test` veritabanı her iki çalıştırmadan sonra birebir aynı (13 tablo, satır sayısı + CHECKSUM).

---

## Panelden sipariş çekme + son çekim durumu (2026-11-18): TAMAM

- **Siparişler → "Kanaldan sipariş çek"** (Yönetici; `POST /api/orders/fetch/{kanal}`): SiparisCek kuyruğa alınır. Yalnızca okuma; kanaldaki iptaller de bu çekimle gelir. Deneme modunda zamanlanmış çekim kapalı olduğu için tek yol bu.
- **Siparişler, Kanal iade talepleri, Soru-Cevap:** Ortak "kanaldan çek" bileşeni. Son çekim zamanı, başarılı/hata durumu ve kanalın pasif olduğu görünür (`GET /api/channels/{id}/job-states`). Çekim bitince liste kendiliğinden yenilenir.
- **Test hijyeni:**
  - Test fikstürü ChannelJobState'i de saklayıp geri yazıyor.
  - İki test, kullanıcının Beymen'i aktif yapmış olmasına göre ön koşulunu kendisi kuruyor.
  - Katalog testi rastgele ürün adı kullanıyor.
- **Önemli bulgu:** Entegrasyon testleri, deneme modunun kullandığı `PazaryeriHub_Test`'teki kullanıcı olaylarını da işledi. Kullanıcının "deftere al" stokları test uygulamasındaki mock Trendyol/Beymen'e "gönderilmiş" sayıldı. Gerçek kanallara istek gitmedi.
  - Kullanıcı ilanlarının gönderim kaydı sıfırlandı; stoklar yeniden "gönderilecek" durumda (KanalStokYenile).
  - Test verisi seçici olarak silindi; kullanıcının 740 SKU'su, 778 ilanı ve 500 hareketi korundu.
  - Kalıcı çözüm: otomatik testler için ayrı veritabanı (karar bekliyor).
- **Testler:**
  - Unit 289/289.
  - Integration: tam çalıştırmada 135 testten 2'si kullanıcı yapılandırması nedeniyle düştü (düzeltildi). Etkilenen 23 test yeniden çalıştırıldı ve geçti. Kullanıcı verisini etkilememek için tam set yeniden çalıştırılmadı.
  - Playwright çalıştırılmadı (aynı veritabanına demo veri yükler).

---

## Varyant ayrımı ve kanal fiyatı tüm kanallarda (2026-11-18): TAMAM (gerçek hesapla doğrulanmadı)

- **Beymen:** Katalog `GET /products` ucuna geçti. Model, renk, beden, ad, marka, kategori, liste ve satış fiyatı okunuyor; her productId ayrı SKU adayı.
- **Hepsiburada:** Listing fiyatı ve ürün adı okunuyor; her HBSKU ayrı.
- **Ürün linki:** Yalnızca Trendyol'da; diğer iki kanalın dokümanında adres alanı yok.
- **Testler:** Unit 289/289 (Beymen ve Hepsiburada katalog contract testleri güncellendi), Integration 134/134.

---

## Katalog varyant ayrımı + ürün linki + kanal fiyatı (2026-11-18): TAMAM (gerçek Trendyol ile yeniden çekim bekleniyor)

- **Sorun:** Trendyol'dan ürün oluşturmada aynı modelin farklı renk ve ebatları (ör. CHAIN75150'nin 3 rengi, BAMBU85150 + Bambu50100) tek SKU'da birleşmişti.
- **Düzeltme (ADR-031):**
  - İçerik kimliği ve varyant özellikleriyle ayrım.
  - Varyant bilinmiyorsa her barkod ayrı SKU; farklı stok kodu asla birleşmez.
  - Eşleştirici renk/beden bilinmeyen satırı model koduyla bağlamaz.
- **Ek:**
  - Trendyol ürün linki: Stok Listesi ve Eşleştirme'de.
  - Kanalda görünen fiyat: Fiyat önizlemesinde.
  - "24 saatten eski" uyarısının yanlış kullanımı düzeltildi.
- **Veri:** Hatalı oluşan 113 ürün, 209 SKU, 584 ilan test veritabanından silindi; deftere stok yazılmamıştı. Trendyol API bilgisi ve aktiflik korundu.
- **Testler:**
  - Unit 288/288 (yeni: gruplama, eşleştirici, uyarı, kanal verisi, Trendyol contract).
  - Integration 134/134 (yeni: mock katalogdan oluşturma → 3 ayrı SKU, çoklanan ilan aynı SKU, link ve fiyat yazılır).
  - Playwright 11/11. 3 tam çalıştırmanın 2'sinde ilk senaryo (sipariş listesi, bu değişiklikle ilgisiz) soğuk başlangıçta zaman aşımına uğradı; tek başına ve 3. tam çalıştırmada geçti.

---

## Test hijyeni: kullanıcının kanal yapılandırması korunur (2026-11-18): TAMAM

- **Sorun:** `ChannelCredentialsTests`, test veritabanındaki Trendyol API bilgisini her çalışmada sahte değerle değiştirip siliyordu. Deneme modunda girilen gerçek anahtar bu yüzden kayboldu. Ayrıca bazı sipariş testleri Trendyol'u aktif yapıp pasife çekiyordu (önceki durumu geri koymadan).
- **Risk:** Gerçek anahtar varken "API bilgisi yok" testi gerçek Trendyol'a sipariş isteği atabilirdi.
- **Düzeltme:**
  - `TestDatabase` fikstürü testlerden önce kanal aktifliği, pay, ayarlar, şifreli API bilgileri, yazma yetenekleri ve AppSetting değerlerini kaydeder; testler bitince geri yazar.
  - İki kimlik testi değeri ayrıca kendi içinde saklayıp geri koyar.
  - "API bilgisi yok" testi gerçek anahtarı geçici olarak kaldırır; böylece dışarı istek çıkmaz.
- **Doğrulama:** Trendyol'a geçici sahte şifreli değer, Aktif=1 ve SORU_CEVAP=DENEME verildi. 133/133 test sonrası üçü de aynen kaldı. Sonra geçici değer kaldırıldı.
- **Not:** Entegrasyon testleri `PazaryeriHub_Test`'e yine test ürünü, sipariş vb. ekler (testlerin doğası). Temiz başlangıç gerekirse test sonrası temizlik ayrıca yapılır.

---

## Sipariş çekme aralığı (2026-11-18): TAMAM

- `Siparis:ChannelFetchCron`: Trendyol ve Hepsiburada 1 dk, Beymen ve softcotton 2 dk. Hız limitleri resmi dokümandan doğrulandı (ADR-030).
- Genel `Siparis:FetchCron` boşsa hiçbir kanalda çekme job'ı yok; deneme modu ve testler değişmedi.
- **Testler:**
  - Unit 280/280. Yeni 4 test: varsayılanlar, genel kapama, yapılandırmadan bağlama.
  - Integration: yeni kayıt testi. Hangfire'da siparis-cek-trendyol ve siparis-cek-hepsiburada `* * * * *`, siparis-cek-beymen `*/2 * * * *`.

---

## Entegrasyon genişletme M5: Soru-Cevap (2026-11-18): TAMAM (gerçek hesapla doğrulanmadı)

- **SoruCek job'ı + tablolar** (`CustomerQuestion`, `QuestionAnswerLog`; migration 0014): Trendyol ve Beymen soruları tekil kaydedilir.
  - Müşteri adı, kimliği ve iletişim bilgisi saklanmaz.
  - Hepsiburada mock: liste yanıt şeması dokümanda yok.
- **Cevap:**
  - Yönetici ve Destek gönderebilir (yeni "SoruCevap" yetkisi); Muhasebe ve Depo 403.
  - "Gönderilecek cevap" onay kutusu zorunlu.
  - Kanal uzunluk kuralı uygulanır (Trendyol 10–2000).
  - Soru başına tek etkin cevap.
  - Cevap `SORU_CEVAP` kanal işlemi olarak gider (varsayılan KAPALI; DENEME'de Yönetici onayı).
  - Her cevap kullanıcı adı ve zamanla kaydedilir.
- **Panel:** "Soru-Cevap" ekranı.
- **Testler:**
  - Unit 276/276. Yeni 5 contract testi:
    - Trendyol: 2 haftalık pencere, müşteri bilgisi taşınmaz, cevap gövdesi ve uzunluk.
    - Beymen: durum kodları, Türkiye saati, cevap gövdesi.
    - Hepsiburada: desteklenmiyor.
  - Integration 132/132. Yeni 4 test:
    - 5 kez gelen soru tek kayıt; KVKK kolon kontrolü.
    - Destek gönderir, Muhasebe 403; onay kutusu ve uzunluk; tek cevap, kanala bir kez; denetim.
    - DENEME.
    - Doğrulanmamış kanal atlanır.
  - Playwright 11/11:
    - Yeni senaryo: Destek onay kutusuyla cevap gönderir.
    - Ekran turuna Fiyat ve Soru-Cevap eklendi.
    - Edge ile çalıştırıldı.
- **Doğrulanmayanlar:** ADR-029.

---

## Entegrasyon genişletme M4: Fiyat (2026-11-18): TAMAM (gerçek hesapla doğrulanmadı)

- **Fiyat verisi** (migration 0013):
  - SKU liste/satış fiyatı, kanal kuralı (çarpan, ek, yuvarlama, taban), ilan başına elle fiyat.
  - Onay tablosu, fiyat geçmişi, değişim eşiği (%30).
- **Hesap ve karar:** saf `PriceAllocator` ve `PriceSyncPolicy`.
- **Hat:** `FiyatDegisti` → OutboxIsle → `SyncQueue(FIYAT)` → FiyatGonder → BatchSorgula. Stok hattı aynı kaldı.
- **Varsayılan KAPALI:**
  - Açmadan önce önizleme: "N ilanın fiyatı değişecek".
  - Eşik aşan veya DENEME'deki değişiklik Yönetici onayı bekler.
  - Taban delinmez.
- **KDV:** yalnızca Beymen dokümanı "VAT included" diyor → yalnızca Beymen'de açılabilir. Trendyol ve Hepsiburada adaptörleri hazır ama KDV teyidi gelene kadar açılamaz.
- **Panel:** "Fiyat" ekranı. **Excel:** fiyat şablonu ve içe/dışa aktarma.
- **Testler:**
  - Unit 271/271:
    - Yeni: PriceAllocator ve karar politikası testleri.
    - Yeni: Excel fiyat ayrıştırma testleri.
    - Yeni: fiyat contract testleri (3 kanal).
    - Güncellendi: eski "fiyat desteklenmiyor" testleri.
  - Integration 128/128. Yeni 8 test:
    - Kapalıyken gitmez.
    - Önizleme → aç → mock'ta bir kez görünür, geçmiş yazılır.
    - Eşik → onay/red; Depo 403.
    - DENEME.
    - Taban ve elle fiyat.
    - KDV doğrulanmamış kanal açılamaz.
    - Varyant kuralı.
    - Excel 4 satır (satır numaralı hatalar).
  - Playwright 10/10. Yeni senaryo: onay → önizleme → aç. Edge ile çalıştırıldı.
- **Doğrulanmayanlar:** ADR-028.

---

## Entegrasyon genişletme M3: Kanal iade talepleri (2026-11-18): TAMAM (gerçek hesapla doğrulanmadı)

- **IadeCek job'ı + tablolar** (`ReturnRequest`, `ReturnRequestLine`, `ReturnDecision`; migration 0012): Trendyol ve Beymen talepleri tekil kaydedilir.
- **Hepsiburada:** talep listesi yanıt şeması dokümanda olmadığı için mock bırakıldı. Gerçek bir yanıt örneği gerekiyor.
- **Depo:** "Depoya ulaştı" ile satır bazında stok girişi. Sevk şartı var, sipariş adedini aşamaz, eski yolla çift giriş olmaz.
- **Yönetici kararları:** onay, red (gerekçe listesinden; Trendyol 1651/451/2101, Beymen PackageIsEmpty) ve teslim alındı (Beymen). Kararlar kanal işlemleri kuyruğundan, `IADE_KARAR` yeteneğiyle gider; her karar kullanıcı adıyla denetim izine yazılır.
- **Testler:**
  - Unit 222/222 (yeni 5 contract).
  - Integration 120/120 (yeni 2: 5 kez gelen talep tek kayıt; depoya giriş bir kez; eski yol engeli; eşleşmeyen sipariş; Depo karar veremez; kapalı yetenek; gerekçe doğrulaması; tekil karar; kanala gönderim; denetim izi).
  - Playwright 9/9 (yeni: depoya giriş + Yönetici onayı). Edge ile çalıştırıldı.
- **Doğrulanmayanlar:** ADR-027.

---

## Entegrasyon genişletme M2: Kanala yazma altyapısı + sipariş durumu bildirimi (2026-11-18): TAMAM (gerçek hesapla doğrulanmadı)

- **Yazma güvenliği:** global yazma kilidi (deneme modu açar), kanal/yetenek bazlı KAPALI/DENEME/ACIK (`ChannelCapability`), gerçek adaptörleri saran `GuardedChannelAdapter`.
- **Kuyruk:** `ChannelCommand` + KanalKomutGonder job'ı (5 denemede HATA, engel hata sayılmaz).
- **Bildirim:** Trendyol (Picking, kendi kargoda takip linki), Beymen (picking, kendi kargoda Shipped), Hepsiburada (paketleme). Bildirim modu "Onayla ve gönder" (varsayılan) veya "Otomatik".
- **Trendyol sipariş çekme Order V2'ye taşındı:** eski uç 15.10.2026'dan beri kullanımdan kalkmış.
- **Panel:** Ayarlar → Kanala yazma yetenekleri; Siparişler → Kanala bildirim; Senkron Merkezi → Kanal işlemleri.
- **Testler:**
  - Unit 217/217 (yeni 8 contract/birim: Trendyol V2 eşlemesi, Picking ve alternatif teslimat, Beymen, Hepsiburada, kapı açık/kapalı).
  - Integration 118/118 (yeni 5: yetenek API'si ve yetki; kapının 4 koşulu; DENEME'de yalnızca onaylanan tek kayıt ve tekillik; kanaldan gelen geçişte bildirim yok; kendi kargoda takip no ve M1 kargo kodu doğrulaması; otomatik mod; 5 denemede HATA; engelin deneme sayılmaması; yeniden dene).
  - Playwright 8/8 (yeni: yetenek DENEME → Hazırlanıyor → Kanala bildir → Yönetici onayı). Edge ile çalıştırıldı.
  - Contract örnekleri dokümandan alınmıştır, gerçek yanıt değildir.
- **Doğrulanmayanlar:** ADR-026.

---

## Entegrasyon genişletme M1: Satıcı Bilgileri (2026-11-18): TAMAM (gerçek hesapla doğrulanmadı)

- Kanal referans verisi (kargo firmaları, iade red gerekçeleri) için `ChannelReference` tablosu (migration 0009), `FetchReferenceAsync` adaptör metodu, `ReferansCek` job'ı, okuma ve yenileme API'si ve Ayarlar'da "Kanal referans verisi" kartı.
- **Kaynaklar:**
  - Trendyol: iki liste de API'den.
  - Beymen: dokümandaki sabit tablolar.
  - Hepsiburada: doğrulanamadı, desteklenmiyor (ADR-025).
- **Testler:**
  - Unit 209/209 (yeni 6 contract: Trendyol 3, Beymen 1, Hepsiburada 2). Örnek yanıtlar dokümandan alınmıştır, gerçek yanıt değildir.
  - Integration 112/113 (yeni 2: tekillik, pasifleştirme, boş liste, desteklenmeyen tip, hata kaydı, yetki ve 409). Başarısız tek test `Genel_bakis_bugunun_siparislerini_ve_hatali_senkronu_sayar`: Türkiye saatiyle 00:11'de çalıştı ve demo siparişlerinin bir kısmı dünün tarihine düştü (gece yarısı sınırı; M1 ile ilgisiz).
  - Playwright 7/7 (Edge ile; Chrome makineden kaldırılmış).
- **Doğrulanmayanlar:** ADR-025.

---

## Mutabakat uzlaştırma: sapmayı kanala yaz + kanal stoğunu deftere al (2026-11-17): TAMAM

- **Sapmayı kanala yaz:** Senkron Merkezi'nde satır ve toplu gönderim mevcut hattan yapılıyor (`EslestirmeDegisti` ile zorla kuyruğa alma); Trendyol/Beymen için 15 dk koruması var. "Kanalı tamamen yenile" artık panelde.
- **Kanal stoğunu deftere al:** yeni "Kanal Stoğu → Defter" ekranı.
  - Kaynak kanal seçilir, önizlenir, hedef düzenlenir; uygulamayı Yönetici onaylar.
  - Hareket SAYIM, kaynak KANAL_ALIM, kullanıcı adıyla yazılır.
  - Ardından tüm kanallara gönderilir.
- **Plandan sapma (düzeltme):** planda Fiziksel = kanal + Rezerve + Güvenlik yazmıştım. Uygulama sırasında bunun çekimden veya önizlemeden sonraki siparişleri satılabilir stokta fazla gösterdiğini (oversell riski) fark ettim. Formüle sonraki net rezervasyon düşümü eklendi (ADR-024).
- **Testler:**
  - Unit 203/203: uzlaştırma kuralları.
  - Integration 110/110:
    - zorla gönderim ve gerekçeli atlama, 15 dk koruması, Depo 403;
    - önizleme yazmaz; uygulama formülü ve denetim izi;
    - tüm ilanlara yayılım, fark yokta hareket yok;
    - önizlemeden sonraki siparişin düşülmesi;
    - çakışan harekette satırın reddedilmesi ve yarım hata.
  - Playwright 7/7: sapma gönderme ve deftere alma senaryoları eklendi.
- Doğrulanmayanlar ADR-024'te.

---

## Çoklu ilan + genel düşük stok eşiği (2026-11-17): TAMAM

- **Migration 0008:** `UNIQUE(SkuId, ChannelId)` kaldırıldı; kanal + barkod ve kanal + kanal ürün kodu tekil oldu. Ön kontrol çakışmada migration'ı durduruyor. Migration öncesi kontrol sorgusunda çakışma çıkmadı (test veritabanı 993 ilan, ana veritabanı 0 ilan).
- **Davranış:**
  - Bir SKU bir kanalda birden fazla ilana bağlanabilir; her ilana tam (ortak havuz) stok gönderilir.
  - Trendyol "Çoklanan" satırları katalog içe almada tek SKU'nun ilanları olur.
  - İkinci ilanın barkoduyla gelen sipariş artık rezerve ediyor; önceki düzende yok sayılan ilanın siparişi rezerve edilmiyordu.
- **Genel düşük stok eşiği:** `DusukStokEsigi` (varsayılan 0). Ayarlar ekranından değiştirilir; değişince tüm ilanlar yeniden hesaplanır ve yalnızca değişen değerler gönderilir.
- **Panel:** Stok Listesi'nde çoklu ilan hücresi, Ayarlar'da eşik alanı.
- **Testler:**
  - Unit 194/194: eşik, eşleştirici ve Excel eşleştirme kuralları.
  - Integration 106/106:
    - çoklu ilan senkronu ve ilan bazlı tekillik;
    - kısmi hata, sipariş çözümleme, mutabakat;
    - eşik ve outbox olayı; tekil index'ler;
    - 0008 ön kontrolünün geçici veritabanında çakışmada durması.
  - Playwright 5/5: stok listesinde "(2 ilan)" kontrolü eklendi.
- Ayrıntılar ve oversell penceresi ADR-023'te. Set ürün çarpanı ayrı adım.

---

## Kanal kataloğu içe alma B: kanal API'si (2026-11-17): TAMAM (gerçek hesapla doğrulanmadı)

- `IChannelAdapter.FetchCatalogAsync`, `KatalogCek` job'ı (panelden tetiklenir; önizleme → uygula) ve migration 0007 (`ChannelJobState.SonSonuc`).
- **Adaptörler:**
  - Trendyol: filterApprovedProducts (model, renk, beden, stok kodu, arşiv).
  - Hepsiburada: HBSKU ve merchantSku.
  - Beymen: id ve barkod.
  - Mock: sayfalı katalog. Ayrıntılar ADR-022'de.
- **Panel:** Eşleştirme ekranında "Kanaldan ürün çek (API)" bölümü (durum 2 sn'de bir yenilenir). Deneme modunda Beymen mock kataloğu demo veriyle gelir.
- **Testler:**
  - Unit 185/185: yeni Trendyol, Hepsiburada ve Beymen katalog contract testleri ve öğe dönüştürücü.
  - Integration 98/98: sayfalı çekme, önizleme/uygulama, durum kaydı, 409, kanal hatası.
  - Playwright 5/5.
  - Deneme modunda tarayıcıyla uçtan uca denendi: Beymen mock kataloğundan 4 eşleşme, 2 eşleşmeyen.
- **Bilinen eksikler:** Gerçek kanal hesaplarıyla denenmedi. Hepsiburada ve Beymen API'leri model/renk/beden vermediği için bu kanallarda eşleştirme yalnızca mevcut eşleştirme, barkod ve stok kodu adımlarıyla yapılır.

---

## Kanal kataloğu içe alma A: Excel (2026-11-17): TAMAM

- **Migration 0006:** `Product.ModelKodu` ve `UnmatchedChannelItem` tablosu.
- **Kanal ürün Excel'i (Trendyol, Beymen):** biçim tanıma, kodların metin okunması, bilimsel gösterim reddi, 4 adımlı eşleştirme, ad benzerliği önerisi, çakışma koruması, isteğe bağlı ürün/SKU oluşturma, önizleme → onay. Ayrıntılar ADR-020 ve ADR-021'de.
- **Senkron güvenliği:** stoğu hiç girilmemiş SKU kanala gönderilmez.
- **Panel:** Eşleştirme ekranına "Kanal ürün dosyasından içe al" ve "Eşleşmeyen kanal ürünleri" (Bağla / Yok say) bölümleri eklendi.
- **Testler:**
  - Unit 179/179: yeni eşleştirici ve dosya okuyucu testleri.
  - Integration 97/97: yeni katalog içe alma uçtan uca testi, yetki testi, stoğu olmayan SKU testi.
  - Playwright 5/5.
- **Gerçek dosyalarla kuru çalıştırma** (test veritabanı, yalnızca önizleme, boş katalog):

  | Kanal | Satır | Hatalı satır | Yeni SKU | Yeni ürün | Belirsiz | Pasif |
  |---|---|---|---|---|---|---|
  | Trendyol | 584 | 0 | 440 | 97 | 144 | 24 |
  | Beymen | 288 | 0 | 284 | 120 | 4 | 27 |

  Belirsiz satırlar aynı model + renk + beden'in tekrarı; Trendyol tarafında bunlar "Çoklanan Ürün Satışı".
- **Bilinen eksikler:**
  - Bir SKU bir kanalda tek eşleştirmeye sahip olabilir (`UNIQUE(SkuId, ChannelId)`). Bu yüzden Trendyol'daki çoklanan ilanlardan yalnızca biri bağlanabilir; diğeri "Yok say" ile kapatılmalı.
  - Hepsiburada ve softcotton dosya biçimi için örnek dosya yok.

---

## Aşama 6c: Beymen adaptörü (2026-11-16): KOD + CONTRACT TESTLERİ TAMAM, GERÇEK HESAPLA DOĞRULANMADI

- Gerçek `BeymenAdapter`: toplu stok + sonuç, kanal stoğu, siparişler (yeni + son 7 gün durum takibi + iptaller), iadeler (claims). Panelde API bilgisi formu.
- **Testler:** Unit 161/161 (yeni 17 Beymen contract testi), Integration 93/93, Playwright 5/5.
- Doğrulanmamış noktalar ADR-019'da.
- **Aşama 6 genel durum:** Trendyol, Hepsiburada, Beymen gerçek adaptörleri yazıldı ve contract testleriyle doğrulandı; üçü de varsayılan olarak Mock modda. softcotton.com.tr altyapısı bilinmediği için mock. Kabul kriteri ("sandbox varsa onunla, yoksa kaydedilmiş örnek yanıtlarla") **örnek yanıtlar resmi doküman şemasından hazırlandığı için kısmen** sağlandı; gerçek hesapla yapılacak ilk denemede yanıtlar kaydedilip testlere eklenmeli.

---

## Aşama 6b: Hepsiburada adaptörü (2026-11-16): KOD + CONTRACT TESTLERİ TAMAM, GERÇEK HESAPLA DOĞRULANMADI

- Gerçek `HepsiburadaAdapter`: stok gönderme + yükleme sonucu, kanal stoğu (listing sorgusu), siparişler (ödeme bekleyen, açık, kargoda, teslim, iptal). İade (talep) entegrasyonu henüz yok.
- Kayıtlı siparişe satırsız durum güncellemesi desteği (Hepsiburada kargo/teslim listeleri için).
- Panel: API bilgisi formu Trendyol ve Hepsiburada için ortak.
- **Testler:** Unit 144/144 (yeni 12 Hepsiburada contract testi), Integration 93/93 (yeni: satırsız durum güncellemesi; HB mod seçimi), Playwright 5/5.
- Doğrulanmamış noktalar ADR-018'de; SIT hesabı ile doğrulanmalı.

---

## Aşama 6a: Trendyol adaptörü (2026-11-16): KOD + CONTRACT TESTLERİ TAMAM, GERÇEK HESAPLA DOĞRULANMADI

- Gerçek `TrendyolAdapter` (v2.0): stok gönderme + batch sonucu, sipariş paketleri (sayfalı, durum eşlemeli), iadeler (claims), kanal stoğu (mutabakat). Fiyat ve durum bildirimi kapsam dışı.
- Kimlik bilgileri IDataProtector ile şifreli (`PUT/DELETE /api/channels/{id}/credentials`, panelde Ayarlar → Kanallar → "API bilgisi").
- HTTP hattı: çağrı loglama, hız sınırı (40/10 sn), yeniden deneme (Polly), zaman aşımı; hata çevirisi.
- Mod: `Kanallar:TRENDYOL:Mod` = Mock (varsayılan) | Gercek.
- **Testler:** Unit 132/132 (yeni 27 Trendyol contract testi), Integration 92/92 (yeni 2: şifreli saklama, mod seçimi), Playwright 5/5.
- **Kabul durumu:** sandbox/test hesabı yok → contract testleri **resmi doküman şemasına göre hazırlanmış örnek yanıtlarla** yapıldı (gerçek kayıtlı yanıt değil). Gerçek hesap bilgileri geldiğinde: (1) stage veya canlıda tek SKU ile stok gönderimi + batch sonucu, (2) sipariş çekme, (3) gerçek yanıtları kaydedip contract testlerine eklemek gerekiyor. Doğrulanmamış noktalar ADR-017'de.

---

## Aşama 5: Panel (2026-11-16): TAMAM
- 8 ekran + giriş; rol bazlı yetki (API policy) ve Destek maskelemesi; sadeleştirilmiş hata mesajları.
- Testler: Unit 105/105, Integration 90/90 (yeni 18: oturum, kilit, rol yetkileri, maskeleme, stok listesi/sapma, genel bakış, hareketler, iade girişi, ayarlar/kullanıcılar).
- **Playwright 5/5** (3 ardışık çalıştırma): (1) sipariş filtresi + zaman çizelgesi, (2) stok sapması kırmızı satır, (3) hatalı senkron "Yeniden dene"; ek: tüm ekranlar açılıyor, Destek maskeli görüyor.
- Panel tarayıcıda elle de kontrol edildi (Genel Bakış, Stok Listesi).
- Eksik: gerçek ortam için ilk Yönetici user-secrets ile oluşturulmalı; Excel içe aktarma ve kullanıcı ekranları Playwright ile ayrıca test edilmedi (API testleri var).

---

## 000000. Aşama 4: Senkron hattı (2026-11-16): TAMAM (mock adaptörle)

### Yapılanlar
| Parça | Yer |
|---|---|
| OutboxIsle, StokGonder, BatchSorgula, Mutabakat job'ları | `Infrastructure/Sync/SyncJobs.cs` |
| Hedef stok, debounce kuyruğa alma, başarı/başarısızlık işleme | `Infrastructure/Sync/SyncStore.cs` |
| Yeniden deneme politikası (saf) ve ayarlar | `Application/Sync/SyncContracts.cs` |
| Senkron Merkezi sorguları, yeniden dene, kanal yenile, sapma raporu | `Infrastructure/Sync/SyncService.cs`, `Api/Endpoints/SyncEndpoints.cs` |
| Kanal/eşleştirme değişikliğinde outbox olayı (aynı transaction) | `ChannelService`, `ExcelTransferService`, `Persistence/OutboxWriter.cs` |
| Mock adaptörde hata enjeksiyonu (kalıcı gönderim hatası, kalem bazlı batch hatası, kanal stoğu kayması) | `Adapters.Mock` |
| Migration 0004 (SyncQueue ekleri, indeksler) | `Database/Migrations/0004_senkron_hatti.sql` |
| Geçici `HeartbeatJob` kaldırıldı | |

Uç noktalar: `GET /api/sync/summary`, `GET /api/sync/queue?channelId=&status=`, `POST /api/sync/queue/{id}/retry`,
`POST /api/sync/channels/{id}/refresh`, `GET /api/sync/reconciliation?channelId=&onlyDeviations=`, `POST /api/sync/reconciliation/{kanal}/run`.

### Testler (gerçek çıktı)
- `dotnet build`: 0 hata, 0 uyarı.
- Unit **99/99** (yeni: yeniden deneme politikası).
- Integration **72/72** (yeni 10 senkron testi). Tam set iki kez arka arkaya yeşil.
- Uygulama gerçek `PazaryeriHub`'da açıldı: 14 tekrarlayan job kayıtlı, eski heartbeat silindi, OutboxIsle/StokGonder/BatchSorgula 10 sn'de bir başarıyla çalışıyor; senkron uçları 200.

### Kabul kriterleri
| Kriter | Test | Durum |
|---|---|---|
| Stok değişimi → ≤30 sn içinde mock kanalda güncel değer | `Uctan_uca_stok_degisimi_30_saniye_icinde…`: **gerçek Hangfire zamanlaması** (10 sn cron), ayrı uygulama örneği | ✅ 3 çalıştırmada **9,5 / 9,5 / 9,2 sn** |
| Hata durumunda 5 denemeden sonra HATA ve uyarı | `Bes_basarisiz_denemeden_sonra_HATA_olur…` | ✅ deneme 1–4: BEKLIYOR, bekleme 30/60/120/240 sn (süresi dolmadan gönderilmez); 5.: HATA; `UYARI: senkron kaydı … HATA durumuna alındı` logu (Warning) yazıldı; özet ve HATA listesinde göründü; "yeniden dene" sonrası gönderildi |
| Ek: debounce (3 değişiklik → 1 kayıt), pay %50 + üst limit + güvenlik stoğu, gönderim sırasında değişiklik kaybolmaz, kısmi batch hatası, pasif kanal/eşleştirme, kanal payı değişince yeniden gönderim, takılı talep kurtarma | 7 test | ✅ |
| Mutabakat ve sapma raporu | `Mutabakat_kanal_stogunu_kaydeder…` | ✅ 3 eşleştirme: 1 sapma (beklenen 10, kanal 7), 1 kanalda yok, 1 uyumlu; snapshot yazıldı; istek üzerine çalıştırma 202 (Hangfire) |

### Bilinen eksikler / açık konular
- **Fiyat senkronu yapılmadı:** şemada fiyat kaynağı yok (ürün/SKU/eşleştirmede fiyat kolonu tanımlı değil). Fiyat nereden gelecek? (soru)
- Sipariş durumunun kanala bildirilmesi ve IadeCek henüz yok.
- Kanal hata metinlerinin sadeleştirilmesi şimdilik genel (HTTP kodu + mesaj); kanal özel çeviriler gerçek adaptörlerle (Aşama 6).
- Yetkilendirme yok (Aşama 5).

---

## 00000. Aşama 3: Sipariş çekirdeği (2026-11-16): TAMAM (mock adaptörle)

### Yapılanlar
| Parça | Yer |
|---|---|
| `IChannelAdapter` sözleşmesi + normalize tipler (spesifikasyon §6) | `Application/Channels/IChannelAdapter.cs` |
| Bellek içi mock adaptör (4 kanal) | `Adapters.Mock/MockChannelAdapter.cs` (gerçek API çağrısı YOK) |
| Durum makinesi (saf) + stok hedefi | `Domain/Orders/OrderStateMachine.cs` |
| İdempotent kayıt, durum geçişi, stok uzlaştırma, oversell bayrağı | `Infrastructure/Orders/OrderService.cs` (ADR-012) |
| `SiparisCek` job'ı (örtüşmeli pencere, kanal başına, eşzamanlısız) | `Infrastructure/BackgroundJobs/OrderFetchJob.cs` |
| Migration 0003: satır başına stok hareketi benzersizliği, `ChannelJobState`, sipariş indeksleri | `Database/Migrations/0003_siparis_cekirdegi.sql` |
| API: `GET /api/orders` (kanal/durum/tarih/SKU/stok yetersiz/arama filtreli), `GET /api/orders/{id}` (satırlar + zaman çizelgesi), `POST /api/orders/{id}/status` | `Api/Endpoints/OrderEndpoints.cs` |
| Geliştirme/test uçları: `POST /api/dev/mock/{kanal}/orders`, `POST /api/dev/jobs/order-fetch/{kanal}` (yalnızca Development/Testing) | aynı dosya |

### Testler (gerçek çıktı)
- `dotnet build`: 0 hata, 0 uyarı.
- Unit **91/91** (yeni: durum makinesinin 7×7 tüm geçiş matrisi, yol bulma, son durumlar, stok hedefi).
- Integration **62/62** (yeni 13 sipariş testi). Sipariş testleri ayrıca 5 kez tekrar koşuldu: 5/5 yeşil.
- Uygulama gerçek `PazaryeriHub`'da açıldı: 0003 uygulandı (`SchemaVersions`), `/health` Healthy, `/api/orders` 200, Hangfire'da `siparis-cek-{trendyol,hepsiburada,beymen,softcotton}` kayıtlı. Ana veritabanına test siparişi yazılmadı.

### Kabul kriterleri
| Kriter | Test | Durum |
|---|---|---|
| Aynı sipariş 5 kez gelse tek kayıt ve tek rezervasyon | `Ayni_siparis_5_kez_arka_arkaya…`, `…ayni_anda…` (5 paralel), `SiparisCek_job_ortusen_pencerelerle…` | ✅ 1 sipariş, satır başına 1 REZERVE hareketi, 1 outbox, Rezerve = adet |
| Geçersiz durum geçişi reddedilir | `Gecersiz_durum_gecisi_reddedilir…` (API 409; Yeni→TeslimEdildi, Kargoda→İptal, Kargoda→Yeni), `Kanaldan_gelen_geri_donus_reddedilir…` | ✅ sipariş ve zaman çizelgesi değişmedi |
| Oversell bayrağı | `Stok_yetersizse_siparis_reddedilmez…` | ✅ sipariş kaydedildi, `StokYetersiz=1`, rezerve yok; stok gelince rezerve edildi ve bayrak kalktı |
| Ek: iptal rezervi iade eder, kargoda sevk eder, ileri atlama ara adımları yazar, eşleşmeyen ürün, kanal barkodu eşleştirmesi, geçersiz veri, pasif kanal, adaptör hatasında pencere ilerlemez | 8 test | ✅ |

Not: paralel testte 5 işlemenin gerçekten aynı anda çakıştığı ölçülmedi; doğruluk test zamanlamasına değil veritabanı kısıtlarına (`UNIQUE` + filtreli unique indeks) dayanıyor.

### Bilinen eksikler
- Sipariş durumunun kanala bildirilmesi (`UpdateOrderStatusAsync` job'ı) ve `IadeCek` yapılmadı (sonraki aşamalar).
- Rezervasyon deposu sabit ("Ana Depo"); çoklu depo kararı bekleniyor.
- Yetkilendirme ve Destek rolü için müşteri maskelemesi Aşama 5'te. Müşteri özetinde yalnızca ad soyad, il, ilçe tutuluyor (KVKK).
- Kanallar seed'de pasif; SiparisCek pasif kanalda hemen çıkar. Gerçek adaptörler Aşama 6'da.

---

## 0000. Aşama 2: Katalog + Excel (2026-11-16): TAMAM

### Yapılanlar
| Parça | Yer |
|---|---|
| Ürün/SKU/depo CRUD (EF Core, mevcut tablolara eşli) | `Infrastructure/Catalog/CatalogService.cs`, `Persistence/PazaryeriHubDbContext.cs` |
| Stok girişi, sayım, güvenlik stoğu, bakiye | `IStockLedger` + yeni `usp_GuvenlikStoguAyarla` (outbox yazar, hareket yazmaz; ADR-011) |
| Kanal ayarları + SKU↔kanal eşleştirme | `Infrastructure/Channels/ChannelService.cs` |
| Excel şablon / içe / dışa aktarma (ürün, stok, eşleştirme) | `Application/Excel/*` (saf doğrulama), `Infrastructure/Excel/*` (ClosedXML) |
| Türkçe ProblemDetails hata yanıtları (SQL metni sızmaz) | `Api/Errors/ApiExceptionHandler.cs` |

### Uç noktalar (kimlik doğrulama yok, Aşama 5'te eklenecek)
- `GET/POST /api/products`, `GET/PUT/DELETE /api/products/{id}`, `POST /api/products/{id}/skus`
- `GET /api/skus?search=`, `PUT/DELETE /api/skus/{id}`; `GET/POST /api/warehouses`
- `GET /api/stock/{skuId}`, `POST /api/stock/receipts`, `POST /api/stock/counts`, `PUT /api/stock/safety-stock`
- `GET /api/channels`, `PUT /api/channels/{id}`; `GET/PUT /api/listings`, `DELETE /api/listings/{id}`
- `GET /api/excel/{products|stock|listings}/template`, `GET .../export`, `POST .../import?validateOnly=`

### Testler (gerçek çıktı)
- `dotnet build`: 0 hata, 0 uyarı.
- Unit **58/58** (yeni: Excel okuma, başlık normalizasyonu, sayısal barkod, satır doğrulama kuralları).
- Integration **49/49** (yeni 14: Excel 8, katalog/stok/kanal/eşleştirme API 6). İki kez arka arkaya koşuldu, ikisi de yeşil.
- Uygulama yerelde gerçek `PazaryeriHub`'a karşı çalıştırıldı: `/health`, `/api/channels`, `/api/warehouses`, `/api/products`, şablon ve dışa aktarma 200 döndü; bilinmeyen tür 404 (problem+json).

### Kabul kriteri
| Kriter | Durum |
|---|---|
| 1.000 satırlık Excel içe aktarılır, hatalı satırlar satır numarasıyla raporlanır | ✅ 1.000 satır, bilinen 9 hatalı satır (boş SKU, boş ad, uzun SKU, dosya içi SKU tekrarı ×2, dosya içi barkod tekrarı ×2, DB'de başka SKU'ya ait barkod, uzun renk) → tam olarak 10, 50, 100, 200, 300, 400, 500, 600, 700 raporlandı; 991 SKU / 200 ürün yazıldı. Süre ~2 sn. Tekrar yükleme: 0 yeni, 991 değişmedi. validateOnly hiçbir şey yazmadı. |
| Şablon indirme dahil | ✅ 3 tür için şablon + "Aciklama" sayfası; başlıklar testle doğrulandı |
| Dışa aktarma | ✅ dışa aktarılan dosya değiştirilmeden geri yüklenince 0 değişiklik |

### Bilinen eksikler / kararlar
- Yetkilendirme yok (Aşama 5). API şu an yalnızca yerelde kullanılmalı.
- Kanal kimlik bilgisi girişi (`KimlikSifreli`, `IDataProtector`) yok; Aşama 6'da.
- Ürün gruplama *Ürün Adı + Marka* ile (model kodu kolonu yok; ADR-009). Gerekirse `Product`'a model kodu eklenebilir.
- Stok Excel'inde `GIRIS` tekrar yüklenirse tekrar ekler (şablonda uyarı); ilk yükleme için `SAYIM` önerilir.
- "Kanal üst limiti" şimdilik `Channel.Ayarlar` JSON'unda serbest alan; Aşama 4'te kesinleşecek.

---

## 000. Yerel çalıştırma doğrulaması (2026-11-16): Aşama 0 ve 1 TAMAM

Karar: Docker kullanılmıyor; uygulama ve testler yerelde çalışıyor (ADR-008).

### Aşama 0 (yerel çalıştırma ile)
API `dotnet run` ile gerçek `PazaryeriHub` veritabanına karşı (Development, `.claude/launch.json` → `api`) başlatıldı:

| Uç nokta | Sonuç |
|---|---|
| `GET /` | 200 |
| `GET /health` | **200 `Healthy`**: sqlserver (RCSI + snapshot açık, SQL 17) ve hangfire (aktif sunucu) |
| `GET /health/live` | 200 |
| `GET /hangfire` | 200; panelde `asama0-heartbeat` tekrarlayan işi kayıtlı ve çalışıyor |

Hangfire zamanlama ölçümü (95 sn, 10 çalışma): ortalama **10,01 sn**, min 9,15, max 10,38. Bu pencerede Error/Fatal log yok.
→ `OutboxIsle` için hedeflenen 10 sn aralık Hangfire ile karşılanabilir.

### Testler (Testcontainers kaldırıldı, yerel `PazaryeriHub_Test` ile)
- `dotnet build`: 0 hata, 0 uyarı.
- Unit **43/43**, Integration **35/35**, atlanan 0.

### Aşama 0 kabul kriterleri (ADR-008'e göre)
| Kriter | Durum |
|---|---|
| API + SQL Server ayağa kalkar | ✅ yerelde (`dotnet run` + yerel SQL Server 2025 Express) |
| `/health` 200 | ✅ hem elle hem `HealthWithDatabaseTests` ile |
| `docker compose up` | ➖ kapsam dışı bırakıldı (ADR-008); compose/Dockerfile doğrulanmadı |

### Bilinen notlar
- Panelde "Sunucular 2" göründü: önceki çalıştırma zorla kapatıldığı için eski Hangfire sunucu kaydı; Hangfire zaman aşımıyla temizler. İşlevsel etkisi yok.
- Hangfire paneli yalnızca Development'ta açık ve yetkisiz; yetkilendirme Aşama 5'te.

---

## 00. Aşama 1: Veritabanı ve stok motoru (2026-11-16): TAMAM

### Yapılanlar
| Parça | Dosya |
|---|---|
| Şema (13 tablo, BIN2 collation'lar, CHECK/ISJSON, filtreli unique'ler, indeksler) | `src/PazaryeriHub.Infrastructure/Database/Migrations/0001_temel_sema.sql` |
| Başlangıç verisi ("Ana Depo", 4 kanal `Aktif=0`, `PayYuzde=100`) | `.../Migrations/0002_baslangic_verisi.sql` |
| Saklı yordamlar (6 + 2 iç yardımcı) | `.../Database/Procedures/*.sql` (her açılışta `CREATE OR ALTER`) |
| DbUp migrator | `src/PazaryeriHub.Infrastructure/Database/DatabaseMigrator.cs` (`Database:MigrateOnStartup`) |
| Stok defteri sözleşmesi + hata tipleri | `src/PazaryeriHub.Application/Stock/` |
| Dapper uygulaması (50001 → `InsufficientStockException`) | `src/PazaryeriHub.Infrastructure/Stock/SqlStockLedger.cs` |
| `StockAllocator` (saf fonksiyon) | `src/PazaryeriHub.Domain/Stock/StockAllocator.cs` |
| Test veritabanı fixture'ı (env → user-secrets → yoksa atla) | `tests/PazaryeriHub.Tests.Integration/Infrastructure/TestDatabase.cs` |

Yordam ortak deseni: `SET XACT_ABORT ON` → tek `BEGIN TRAN` → koşullu `UPDATE ... OUTPUT INTO` (UPDLOCK) →
0 satırsa `ROLLBACK` + `THROW 500xx` → `usp_StokHareketYaz` (hareket + `StokDegisti` outbox, transaction dışında çağrılırsa 50020) → `COMMIT`.
Hata kodları: 50001 stok yetersiz, 50002 sevk için rezerve yok, 50003 iptal için rezerve yok, 50004 sayım < rezerve, 50010 geçersiz adet.

### Ortam
- `ms` login'ine sysadmin verildi (ürün sahibi). `PazaryeriHub` ve `PazaryeriHub_Test` oluşturuldu:
  `Turkish_CI_AS`, RCSI açık, snapshot açık, **AUTO_CLOSE kapalı** (Express varsayılan olarak açıyordu; bootstrapper ve provision script'i artık kapatıyor).
- Test bağlantısı test projesinin user-secrets'ında (`ConnectionStrings:PazaryeriHubTest`).

### Test sonuçları (gerçek çıktı, `PazaryeriHub_Test` üzerinde)
- `dotnet build`: 0 hata, 0 uyarı.
- **Unit: 43/43 geçti.**
- **Integration: 35/35 geçti, 0 atlanan.** Stok testleri (15 test) ayrıca 3 kez tekrar koşuldu: her seferinde 15/15.
- `db/provision/001_create_database.sql` sqlcmd ile çalıştırıldı: çıkış kodu 0 (idempotent).

### Uygulama ile uçtan uca (gerçek `PazaryeriHub` veritabanı, Development)
- Açılışta DbUp çalıştı: journal'da `0001_temel_sema.sql`, `0002_baslangic_verisi.sql`; 13 tablo + `SchemaVersions`, 8 yordam, 11 Hangfire tablosu.
- `GET /health` → **200**, `Healthy` (sqlserver: RCSI/snapshot true, SQL 17; hangfire: 1 aktif sunucu). Logda Error/Fatal yok.

### Kabul kriterleri
| Kriter | Test | Durum |
|---|---|---|
| (a) 100 paralel rezervasyonda stok eksiye düşmez | `StockConcurrencyTests` (2 test) | ✅ Geçti: 50 stok / 100 istek → 50 başarılı, 50 `InsufficientStock`, Rezerve=50, 50 hareket + 50 outbox |
| (b) rezerve→iptal→sevk bakiyeleri doğru | `StockFlowTests` (10 test) | ✅ Geçti: hatalı işlemler bakiye/hareket/outbox'ı değiştirmiyor |
| (c) her değişiklikte hareket + outbox | `LedgerAuditTests` (3 test) | ✅ Geçti: 6 işlem tipinin her biri tam 1 hareket + 1 outbox; payload bakiye ile tutarlı |
| (d) allocator birim testleri | `StockAllocatorTests` | ✅ Geçti |
| Ek: collation, i/İ ayrımı, unique/filtreli indeks, ISJSON, CHECK, migration tekrarı | `SchemaTests` (17 test) | ✅ Geçti |
| "Testcontainers ile" koşulu | — | ➖ ADR-008 ile yerel SQL Server test veritabanına çevrildi; Testcontainers kaldırıldı |

Not: (a) testinde koşullu UPDATE olmasaydı bile `CK_StockBalance_RezerveFiziksel` CHECK'i eksiye düşmeyi engellerdi (hata 547 ile).
Koşullu UPDATE, bu durumu iş hatası (50001) olarak ve satır kilidiyle düzgün çözüyor; CHECK ikinci güvenlik ağı.

### Spesifikasyona yapılan ekler / yorumlar (gözden geçirilmeli)
- `[Order].StokYetersiz BIT`: ilke 7'deki "stok yetersiz bayrağı" için kolon listesine **ek**.
- `OrderLine.SkuId NULL` olabilir: eşleşmeyen kanal ürünü olan sipariş de kaydedilebilsin diye.
- Durum/tip kod kolonları (`Durum`, `Tip`, `OlayTipi`, `Kod`) da BIN2 (Türkçe `I/ı` sorununa karşı).
- `ChannelListing.Durum` için `AKTIF|PASIF` değerleri seçildi (spesifikasyonda tanımsız).
- `Channel.PayYuzde DECIMAL(5,2)` (33,33 gibi paylar için).
- "Kanal üst limiti" için kolon yok; şimdilik `Channel.Ayarlar` JSON'unda tutulması öneriliyor (Aşama 4'te kesinleşecek). Allocator parametre olarak alıyor.
- `StockMovement.Adet`: SAYIM'da işaretli fark (negatif/0 olabilir), diğerlerinde > 0.
- Güvenlik stoğu rezervasyonu **engellemez** (spesifikasyon koşulu `Fiziksel - Rezerve >= Adet`). Testle belgelendi.
- Yordamlar dış bir transaction içinde çağrılırsa hata durumunda dış transaction da geri alınır (XACT_ABORT). Aşama 3'te sipariş kaydı ile rezervasyon ayrı transaction'larda yapılmalı.

### Kalan eksikler
- `docker-compose.yml` / `Dockerfile` doğrulanmadı (ADR-008: Docker kullanılmıyor).
- Güvenlik stoğu (`GuvenlikStogu`) için uygulama düzeyi API yok; Aşama 2 (stok/ayarlar) ile gelecek.
- Outbox tüketimi (READPAST) Aşama 4'te.

---

## 0. Güncelleme (2026-11-16, devralma sonrası ilk düzeltmeler)

| Konu | Durum |
|---|---|
| Derleme (NU1903) | ✅ Düzeltildi: `Newtonsoft.Json 13.0.4` geçişli olarak sabitlendi (ADR-006). `dotnet build`: 0 hata, 0 uyarı. |
| Testler | ✅ `dotnet test` bastırma olmadan: Unit 18/18, Integration 2 geçti, 1 atlandı (gerçek DB testi). |
| .NET sürümü | ✅ .NET 10 kararı ürün sahibince onaylandı (ADR-002). |
| SQL sunucusu | Hedef: SQL Server 2025 (17.0) **Express**, collation `Turkish_CI_AS` (ADR-003). |
| Veritabanı oluşturma | ✅ (güncel durum için yukarıdaki Aşama 1 bölümüne bakın) Önceki durum: ❌ **Engelli.** `ms` login'inin `CREATE DATABASE` yetkisi yok (sysadmin=0, dbcreator=0). Uygulama denedi → **SQL hata 262**; `/health` → **503**. Çözüm: bir sysadmin `db/provision/001_create_database.sql` script'ini çalıştırmalı. |
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
