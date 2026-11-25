# PazaryeriHub kurulum kılavuzu

PazaryeriHub bir bilgisayara **Windows servisi** olarak kurulur. Ayrı bir exe'yi açmanız gerekmez:

- Bilgisayar açılınca kendiliğinden başlar, kimse oturum açmasa da çalışır.
- Çökerse kendini yeniden başlatır.

Panel bir web sayfasıdır. Bu bilgisayardan ve aynı ağdaki diğer cihazlardan (bilgisayar, tablet, telefon) tarayıcıyla açılır.

## 1. Gerekenler (kurulacak bilgisayar)

| | |
|---|---|
| İşletim sistemi | Windows 10 / 11 (64 bit) veya Windows Server 2019+ |
| Veritabanı | **SQL Server Express** (ücretsiz; veritabanı başına 10 GB sınırı) ya da Standard. Boş kurulumda 2019 ve sonrası yeter; **eski veriyi taşıyacaksanız eski bilgisayardaki sürüm ya da daha yenisi** (6. bölüm) |
| .NET | Gerekmez; paket kendi çalışma zamanını içerir |
| Ağ | Bilgisayarın ağ profili **Özel (Private)** olmalı; mümkünse sabit IP veya DHCP rezervasyonu |
| Yetki | Kurulum için Windows **yönetici** hesabı |

### SQL Server Express kurulumu
1. Microsoft'un sitesinden SQL Server Express'i indirin ve **Temel (Basic)** kurulumu seçin. Veri taşıyacaksanız eski bilgisayardaki sürümü seçin; `veri-yedekle.ps1` bu sürümü ekrana yazar (geliştirme bilgisayarında SQL Server 2025 Express, sürüm 17).
2. Örnek adı varsayılan olarak `SQLEXPRESS` olur. Kurulumu yapan Windows kullanıcısı SQL Server'da otomatik olarak yönetici olur.
3. Kurulumu PazaryeriHub'ı kuracak aynı Windows kullanıcısıyla yapın.

> Bu bilgisayar uyku moduna girerse panel erişilemez. Güç ayarlarından uykuyu kapatın.

## 2. Kurulum

1. `PazaryeriHub-<tarih>.zip` dosyasını bilgisayara kopyalayın ve bir klasöre açın (ör. `C:\Kurulum\PazaryeriHub`).
2. Başlat menüsünde **Windows PowerShell**'e sağ tıklayın → **Yönetici olarak çalıştır**.
3. Klasöre geçip kurulumu başlatın:
   ```powershell
   cd C:\Kurulum\PazaryeriHub
   powershell -ExecutionPolicy Bypass -File .\kur.ps1
   ```
4. Betik ilk Yönetici kullanıcısının (`yonetici`) parolasını sorar: en az 10 karakter. Bu parolayı unutmayın.
5. Sonunda panel adresleri yazılır. Örnek:
   ```
   Bu bilgisayardan : http://localhost:5090
   Ağdaki cihazlar  : http://DEPO-PC:5090
                      http://192.168.1.20:5090
   ```
   Masaüstüne **PazaryeriHub Panel** kısayolu da eklenir.

### Seçenekler
| Parametre | Varsayılan | Ne zaman |
|---|---|---|
| `-SqlSunucu` | `localhost\SQLEXPRESS` | SQL Server başka bir örnek adıyla veya başka bir bilgisayarda ise |
| `-SqlKullanici` | (boş = Windows kimlik doğrulaması) | SQL Server **başka bir bilgisayarda** ise SQL kullanıcısı verin (parola sorulur) |
| `-Veritabani` | `PazaryeriHub` | |
| `-Port` | `5090` | Port başka bir programda kullanılıyorsa |
| `-YoneticiKullanici` | `yonetici` | İlk yöneticinin kullanıcı adı |
| `-VeriYedegi` | (boş = boş veritabanı) | Eski bilgisayardan taşınan `.bak` dosyası; yalnızca ilk kurulumda (6. bölüm) |

Örnek (SQL Server ağdaki başka bir sunucuda):
```powershell
powershell -ExecutionPolicy Bypass -File .\kur.ps1 -SqlSunucu 192.168.1.5,1433 -SqlKullanici pazaryerihub
```

## 3. Diğer cihazlardan erişim

- Aynı ağdaki cihazdan tarayıcıyla `http://<bilgisayar-adı>:5090` veya `http://<IP>:5090` adresini açın.
- Açılmazsa:
  1. Kurulu bilgisayarın ağ profili **Özel** mi? Ayarlar → Ağ ve İnternet → bağlantı → Ağ profili türü. Güvenlik duvarı izni yalnızca Özel ve Etki alanı ağlarında açıktır.
  2. Bilgisayar adıyla açılmıyorsa IP adresini deneyin.
  3. Kurulu bilgisayarda `http://localhost:5090/health` "Healthy" diyor mu?
- Panel ağ içinde **HTTP** ile çalışır. Yalnızca güvendiğiniz yerel ağda kullanın, internete (modemde port yönlendirme) açmayın.

## 4. Kullanıcılar ve yetkiler

**Ayarlar → Kullanıcılar** (yalnızca Yönetici):
- **Yeni kullanıcı:** kullanıcı adı, ad soyad, rol ve en az 10 karakterlik parola.
- **Rol değiştirme, pasife alma, parola sıfırlama, silme.**
  - Pasife alınan veya silinen kullanıcının açık oturumu **hemen** kapanır.
  - Rol değişikliği anında geçerli olur.
  - Kendinizi silemez, pasife alamaz, yönetici yetkinizi kaldıramazsınız. Son aktif yönetici kaldırılamaz.

| Rol | Yetki |
|---|---|
| Yönetici | Her şey: ayarlar, kullanıcılar, kanal işlemleri, İşlem Geçmişi |
| Depo | Stok girişi/sayım, sipariş durumu, iade depoya giriş |
| Muhasebe | Yalnızca okuma |
| Destek | Okuma, soru cevaplama; müşteri bilgisi maskeli |

**İşlem Geçmişi** (sol menü, yalnızca Yönetici) şunları listeler:
- Kim, ne zaman, hangi cihazdan (IP), hangi değişikliği yaptı ve sonucu.
- Girişler, çıkışlar ve hatalı giriş denemeleri.

Kullanıcı, tarih, tip, işlem alanı ve "yalnızca hatalı" ile süzülebilir. Parola, API anahtarı ve müşteri bilgisi kaydedilmez.

Stok hareketleri, sipariş geçmişi, fiyat geçmişi ve kanal işlemleri de kendi ekranlarında işlemi yapan kullanıcıyla görünür.

## 5. Canlıya geçiş: adım adım

Kurulumdan sonra uygulama **güvenli modda** başlar:
- Kanallara hiçbir şey yazılmaz (yazma kilidi).
- Tüm kanallar Mock (sahte) bağlantıda.

Gerçek kanallara geçiş `kanal-ayari.ps1` ile yapılır. Betiği **Yönetici PowerShell**'de, paket klasöründen çalıştırın; servis kendiliğinden yeniden başlar.

**Adım 1 — API bilgileri:** Panel → Ayarlar → Kanallar: her kanalın API bilgisini girin.

Bilgiler bu bilgisayarın anahtarıyla şifrelenir. Başka bilgisayardan taşınan veritabanındaki kanal bilgileri çözülemez, yeniden girilmelidir.

**Adım 2 — Yalnızca okuma ile canlı:**
```powershell
powershell -ExecutionPolicy Bypass -File .\kanal-ayari.ps1 -GercekKanal TRENDYOL,BEYMEN -Yazma Kilitli
```
Siparişler gerçek kanallardan çekilir, stok ayrılır, gece mutabakatı çalışır; kanala hiçbir şey gitmez. Bir-iki gün izleyin.

**Adım 3 — Stok defterini doğrulayın** (en önemli adım):
1. Kanal Stoğu → Defter ekranında mutabakatı çalıştırın.
2. Sapmaları sayımla ya da "kanal stoğunu deftere al" ile kapatın.
3. Kanalda bulunamayan ilanları pasif yapın.

Stok gönderimi açıldığında tüm aktif ilanların stoğu defterdeki değere eşitlenir.

**Adım 4 — Yazma kilidini kaldırın:**
```powershell
powershell -ExecutionPolicy Bypass -File .\kanal-ayari.ps1 -GercekKanal TRENDYOL,BEYMEN -Yazma Acik
```
Kilit kalksa da hiçbir iş kendiliğinden kanala gitmez. Panel → Ayarlar → Kanallar → yetenekleri tek tek açın:
- **Stok:** varsayılan **Onaylı**. Hiçbir stok değişikliği siz onaylamadan pazaryerine gitmez.
  - **Senkron Merkezi → Onay bekleyen stok gönderimleri**: her ürün için resim, SKU, kanal, kanalda şu an görünen stok, gönderilecek stok ve fark gösterilir.
  - Göndermek istediğiniz kanalların kutucuklarını işaretleyin, satırları seçin, **Seçilenleri kanala gönder**. Göndermeden önce kanal bazında özet (kaç azalış, kaç artış, kaç ürün 0'a iniyor) gösterilir.
  - **Seçilenleri reddet**: o değer gönderilmez. Stok yeniden değişirse yeni değer tekrar onaya düşer.
  - Onaydan sonra stok yine değişirse onaylanan değer gitmez; kayıt tekrar onaya düşer.
  - Bir kanalda onaysız (anında) gönderim isterseniz o kanalın stok yeteneğini **Açık** yapın. **Kapalı**: o kanala hiç stok gitmez.
  - Uyarı: Onaylı kanalda bir satış olduğunda diğer kanallardaki stok da siz onaylayana kadar eski kalır. Onay listesini sık kontrol edin.
- **Sipariş durumu, iade kararı, soru cevabı:** ilk hafta **Deneme**.
- **Fiyat:** Trendyol ve Beymen'de **Deneme** ile. Kanal başına farklı fiyat: menüde **Fiyat & Stok Düzenle** (aynı SKU'nun her pazaryerindeki fiyatı, durumu ve stoğu tek satırda; kanal kutusuna elle fiyat, stok kutusuna satılabilir stok, kanal sütunundaki kanal stoğu kutusuna yalnızca o kanala gidecek adeti yazıp "Değişiklikleri kaydet"; stok defterde sayım olarak düzeltilir ve Onaylı kanallarda Senkron Merkezi'nde onay bekler). Hepsiburada'da KDV teyidi gelene kadar **kapalı** kalır (uygulama zaten engeller).

**Adım 5 — Hepsiburada:** Hepsiburada canlı onayı ve canlı API bilgileri gelince:
1. Ayarlar → Kanallar → Hepsiburada: ortamı **Canlı** yapın ve bilgileri girin.
2. Betiği Hepsiburada'yı ekleyerek çalıştırın: `-GercekKanal TRENDYOL,BEYMEN,HEPSIBURADA`.
3. Adım 2–4'ü Hepsiburada için tekrarlayın.

**Acil durdurma** (kanala yazmayı hemen keser):
```powershell
powershell -ExecutionPolicy Bypass -File .\kanal-ayari.ps1 -Yazma Kilitli
```

## 6. Mevcut verileri taşıma (isteğe bağlı)

Eski bilgisayardaki tüm veri bu kuruluma taşınabilir: ürünler, SKU'lar, eşleştirmeler, stok defteri ve hareketleri, siparişler, fiyat geçmişi, panel kullanıcıları ve ayarlar. Tablolar, indeksler ve saklı yordamlar yedeğin içindedir; ayrıca bir şey kurulmaz.

**Eski bilgisayarda** (geliştirme/deneme bilgisayarı), proje klasöründe normal PowerShell:
```powershell
powershell -ExecutionPolicy Bypass -File .\kurulum\veri-yedekle.ps1
```
- Varsayılan olarak deneme modunun veritabanı `PazaryeriHub_Test` yedeklenir. Başkası için `-Veritabani <ad>` verin.
- Yedek `dagitim\PazaryeriHub-veri-<tarih>.bak` olarak çıkar ve doğrulanır. Kaynak veritabanına hiçbir şey yazılmaz.
- Ekrana kaynak SQL Server sürümü yazılır. Yeni bilgisayara **en az bu sürümü** kurun; yeni sürümün yedeği eski sürüme geri yüklenemez.
- Deneme modunu yedekten önce kapatmak gerekmez; o anki veri yedeklenir. Yedekten sonra deneme modunda yaptığınız değişiklikler taşınmaz.

**Yeni bilgisayarda:** `.bak` dosyasını paket klasörüne kopyalayın ve kurulumu bununla başlatın (Yönetici PowerShell):
```powershell
powershell -ExecutionPolicy Bypass -File .\kur.ps1 -VeriYedegi .\PazaryeriHub-veri-<tarih>.bak
```
Kurulum veritabanını boş oluşturmak yerine yedekten geri yükler, sonra şemayı günceller. Ardından:
- **Kullanıcılar:** eski kullanıcılar ve parolaları taşınır. Betiğin sorduğu `yonetici` kullanıcısı eksikse eklenir. Aynı adla bir kullanıcı zaten varsa parolasına dokunulmaz.
- **SQL kullanıcıları ve parolaları:** taşınmaz, gerekmez. Servis SQL Server'a kendi Windows hesabıyla (`NT SERVICE\PazaryeriHub`) bağlanır ve kurulum bu hesaba yetkiyi verir. Eski bilgisayarın SQL kullanıcıları (ör. `ms`) yeni kopyadan çıkarılır.
- **Kanal API bilgileri** temizlenir: eski bilgisayarın anahtarıyla şifrelidir, burada çözülemez. Panelden yeniden girin (5. bölüm, Adım 1).
- Kanallar yine Mock ve yazma kilitli başlar; canlıya geçiş 5. bölümdeki gibi.

Güvenlik: `-VeriYedegi` yalnızca ilk kurulumda çalışır. Yeni bilgisayarda aynı adlı veritabanı varsa kurulum durur, üzerine yazmaz. `.bak` dosyası müşteri bilgisi içerir; taşıdıktan sonra USB'den/paylaşımdan silin.

Elle yapmak isterseniz (SSMS): eski bilgisayarda Tasks → Back Up (Copy-only), yeni bilgisayarda `PazaryeriHub` adıyla Restore, sonra `kur.ps1`. Bu yolda kanal API bilgileri ve eski SQL kullanıcıları elle temizlenmelidir.

## 7. Güncelleme

Yeni paketi açın, aynı komutla `kur.ps1`'i çalıştırın:
- Servis durur, program dosyaları yenilenir, veritabanı şeması güncellenir, servis yeniden başlar.
- Ayarlar, şifreleme anahtarları, loglar ve veriler korunur.
- Parola sorulmaz.

## 8. Yedekleme (önerilir)

Yedeklenmesi gerekenler:
- **Veritabanı:** SQL Server Express'te zamanlanmış yedek yoktur. SSMS ile elle ya da Windows Görev Zamanlayıcı ile `sqlcmd ... BACKUP DATABASE` komutu kullanın.
- **`C:\ProgramData\PazaryeriHub\anahtarlar`:** Kanal API bilgilerinin şifreleme anahtarları. Bu bilgisayara bağlıdır. Bilgisayar değişirse API bilgileri yeniden girilir.

## 9. Kaldırma

```powershell
powershell -ExecutionPolicy Bypass -File .\kaldir.ps1
```
Servis, program dosyaları, güvenlik duvarı kuralı ve kısayol kaldırılır. Veritabanı ve `C:\ProgramData\PazaryeriHub` korunur.

`-AyarlariSil` ile ayarlar, anahtarlar ve loglar da silinir.

## 10. Sorun giderme

| Sorun | Bakılacak yer |
|---|---|
| Servis başlamıyor | `C:\ProgramData\PazaryeriHub\loglar\` en son dosya; Olay Görüntüleyicisi → Windows Günlükleri → Uygulama |
| Hatayı ekranda görmek | Servisi durdurun (`Stop-Service PazaryeriHub`), Yönetici PowerShell'de `& "C:\Program Files\PazaryeriHub\PazaryeriHub.Api.exe" --kurulu` |
| Veritabanına bağlanamıyor | SQL Server servisi çalışıyor mu (`Get-Service MSSQL*`); `C:\ProgramData\PazaryeriHub\ayarlar.json` içindeki sunucu adı |
| Diğer cihazlar açamıyor | 3. bölüm |
| Yönetici parolası unutuldu | Başka bir Yönetici Ayarlar → Kullanıcılar → Parola ile sıfırlar. Giren yönetici yoksa aşağıdaki "Unutulan yönetici parolası" |
| `yonetici` ile giriş olmuyor, kullanıcı yok | Aşağıdaki "Unutulan yönetici parolası": kullanıcı yoksa oluşturulur |

### Unutulan yönetici parolası

Paket klasöründe, **Yönetici PowerShell**'de:
```powershell
powershell -ExecutionPolicy Bypass -File .\kur.ps1 -YoneticiParolaSifirla
```
- `yonetici` için yeni parola iki kez sorulur. Başka bir kullanıcı için `-YoneticiKullanici <ad>` ekleyin.
- Kullanıcı yoksa oluşturulur. Varsa parolası değişir, hesap etkinleşir, kilidi kalkar, rolü Yönetici olur.
- Veriler ve ayarlar değişmez. Program yeniden kopyalandığı için servis birkaç saniye durup yeniden başlar.
- Bu işlem yalnızca bilgisayarın Windows yöneticisi tarafından yapılabilir; panelden yapılamaz.

Ayarlar `C:\ProgramData\PazaryeriHub\ayarlar.json` dosyasındadır. Bu klasörü yalnızca Yöneticiler ve servis okuyabilir. Elle değişiklikten sonra servisi yeniden başlatın: `Restart-Service PazaryeriHub`.
