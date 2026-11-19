# PazaryeriHub

Trendyol, Hepsiburada, Beymen ve softcotton.com.tr için tek stok defterli stok ve sipariş yönetimi (.NET 10 + SQL Server + React panel).

- Spesifikasyon: [CLAUDE.md](CLAUDE.md)
- İlerleme raporu: [docs/ilerleme.md](docs/ilerleme.md)
- Mimari kararlar: [docs/decisions.md](docs/decisions.md)
- Kanal API notları: [docs/kanal-api-notlari.md](docs/kanal-api-notlari.md)

## Deneme modu (arayüzü görmek için)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\deneme-modu.ps1
```

- **Test** veritabanını (`PazaryeriHub_Test`) kullanır; ana veritabanına dokunmaz.
- Tüm kanallar mock çalışır, yani gerçek pazaryerlerine hiçbir çağrı yapılmaz. Arka plan job'ları kapalıdır.
- Bağlantı dizesi ilk çalıştırmada gizli girişle istenir ve test projesinin user-secrets'ına kaydedilir. Alternatif olarak `PAZARYERIHUB_TEST_DB` ortam değişkeni de kullanılabilir.
- Demo veri varsayılan olarak yüklenmez (canlı kanallardan çekilen verilerle karışmasın). `-DemoVeri` ile etiketli demo veri yüklenir: ürünler, siparişler, bir stok sapması ve hatalı bir senkron kaydı.
- Adres `http://localhost:5090`. Kullanıcı `demo-yonetici` (diğer roller için panelden kullanıcı ekleyin); parola her çalıştırmada yeniden üretilir ve yalnızca terminalde gösterilir.
- Parametreler: `-Port 5095` farklı port; `-DemoVeri` demo veri yükler; `-GercekKanal TRENDYOL,BEYMEN` bu kanalları gerçek API ile çalıştırır (yazma kilitli). Eski `-DemoVeriYok` hâlâ kabul edilir.
