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
- Etiketli demo veri yüklenir: ürünler, siparişler, bir stok sapması ve hatalı bir senkron kaydı.
- Adres `http://localhost:5090`. Kullanıcılar `demo-yonetici`, `demo-depo` ve `demo-destek`; parola her çalıştırmada yeniden üretilir ve yalnızca terminalde gösterilir.
- Parametreler: `-Port 5095` farklı port; `-DemoVeriYok` demo veri yüklemez.
