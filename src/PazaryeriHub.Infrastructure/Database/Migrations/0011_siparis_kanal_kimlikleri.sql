/*
    0011: Sipariş durum bildirimi için kanal kimlikleri (ADR-026). Spesifikasyonun tablo listesine EK.
      - [Order].KanalPaketNo: kanalın paket/sipariş kimliği (Trendyol shipmentPackageId, Beymen sipariş id GUID).
      - OrderLine.KanalSatirNo: kanalın satır kimliği (Trendyol lineId, Hepsiburada kalem id).
    Eski siparişlerde boştur; sonraki SiparisCek (örtüşmeli pencere) boş olanları doldurur. Dolmayan siparişler
    "kanala bildirilemez: kanal kimliği yok" olarak gösterilir.
*/

ALTER TABLE dbo.[Order] ADD KanalPaketNo NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NULL;
ALTER TABLE dbo.OrderLine ADD KanalSatirNo NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NULL;
