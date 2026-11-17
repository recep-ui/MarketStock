# Kanal API Notları (Aşama 6 hazırlık)

**Tarih:** 2026-11-16 · **Kaynak:** yalnızca resmi geliştirici dokümanları (üçüncü taraf bloglar kullanılmadı).
**Kapsam kararları:** tek depo; fiyat gönderilmez (fiyatlar satıcı panellerinden girilir, ADR-015). Gerekenler: **stok gönderme + sonuç sorgulama, sipariş çekme, iade/iptal çekme, mutabakat için kanal stoğu okuma.**

İşaretler: ✅ resmi dokümanda okundu · ⚠️ dokümanda yok / belirsiz → `// TODO: doğrulanmalı`

---

## 1. Trendyol — https://developers.trendyol.com (v2.0)

| Konu | Bilgi | Kaynak |
|---|---|---|
| Kimlik | ✅ Basic Auth: kullanıcı = API Key, parola = API Secret (satıcı paneli → Hesap Bilgilerim → Entegrasyon Bilgileri; yalnızca ana kullanıcı görür). Prod/stage bilgileri farklı. | `/v2.0/docs/authorization` |
| User-Agent | ✅ **Zorunlu**, yoksa 403. Biçim: `"{sellerId} - SelfIntegration"` (kendi yazılımımız) | aynı |
| Ortam | ✅ Prod `https://apigw.trendyol.com/integration` · Stage `https://stageapigw.trendyol.com/integration` (**IP beyaz liste** gerekir, yoksa 503) | `/v2.0/docs/3-prod-stage-environments-1` |
| Stok gönder | ✅ `POST /inventory/sellers/{sellerId}/products/price-and-inventory`, gövde `{"items":[{"barcode","quantity"}]}` — yalnızca stok gönderilebilir (fiyat alanları opsiyonel). En fazla **1000 kalem**, ürün başına en fazla 20.000 stok. Yanıt `{"batchRequestId"}`. **Aynı istek 15 dk tekrar gönderilemez** → yalnızca değişen değerler gönderilmeli (debounce + "son gönderilenden farklıysa" kuralımız buna uygun). | `/v2.0/reference/updatepriceandinventory` |
| Batch sonucu | ✅ `GET /product/sellers/{sellerId}/products/batch-requests/{batchRequestId}` → `status` (COMPLETED / IN_PROGRESS), `items[].status` (SUCCESS / FAILED), `items[].failureReasons[]`, `items[].requestItem`. Sonuç tamamlanmadan sonra 4 saat görülebilir. | `/v2.0/reference/getbatchrequestresult` |
| Siparişler | ✅ `GET /order/sellers/{sellerId}/orders?startDate&endDate` (epoch **ms**), `page` (0'dan), `size` (≤200), `orderByField=PackageLastModifiedDate`, `status`. Yanıt `{page,size,totalPages,totalElements,content[]}`. Paket: `id`, `orderNumber`, `status`, `customerFirstName/LastName`, `shipmentAddress.city/district`, `cargoProviderName`, `cargoTrackingNumber`, `totalPrice`, `packageHistory[]`, `lines[]` (`barcode`, `merchantSku`, `quantity`, `price`, `amount`). | `/v2.0/reference/getshipmentpackages` |
| Paket durumları | ✅ Created, Picking, Invoiced, Shipped, Delivered, Cancelled, UnDelivered, Returned (+ sorguda Awaiting, AtCollectionPoint, UnSupplied). Anlamları dokümanda tanımlı değil ⚠️ | aynı |
| Durum bildirme | ✅ `PUT /order/sellers/{sellerId}/shipment-packages/{packageId}` `{status: Picking|Invoiced, lines[], params}` | `/v2.0/reference/updatepackagestatus` |
| İadeler | ✅ `GET /order/sellers/{sellerId}/claims` (`startDate/endDate`, `page`, `size≤200`, `claimItemStatus`) → `orderNumber`, `items[].orderLine.barcode`, `claimItems[].claimItemStatus.name` | `/v2.0/reference/getclaims` |
| Kanal stoğu | ✅ `GET /product/sellers/{sellerId}/products/approved/inventory-and-price?barcodes=` (≤50) → `variants[].barcode`, `quantity` | `/v2.0/reference/filterapprovedproductsinventoryandprice` |
| Katalog | ✅ `GET /product/sellers/{sellerId}/products/approved?page&size(≤100)&nextPageToken` (page×size ≤ 10.000) → `content[]{productMainId, title, brand{name}, category{name}, attributes[], variants[]{barcode, stockCode, attributes[]{attributeName, attributeValue}, onSale, locked, archived, blacklisted}}`. ⚠️ Renk/beden öznitelik adları kategoriye bağlı → doğrulanmalı | `/v2.0/reference/filterapprovedproducts` (2026-11-17) |
| Limitler | ✅ Aynı uca 10 sn'de en fazla 50 istek (51. → 429). Sipariş listeleme 30–100 istek/dk (mağaza seviyesine göre). | authorization, 1-service-limitations |
| ⚠️ Belirsiz | Sipariş sorgusunda en fazla tarih aralığı; durum anlamları; satırda komisyon alanı yok; claims tarih birimi açıkça yazmıyor (diğerleri ms). | |

**Durum eşleme önerisi:** Created/Awaiting → Yeni; Picking/Invoiced → Hazırlanıyor; Shipped/AtCollectionPoint → Kargoda; Delivered → TeslimEdildi; Cancelled/UnSupplied → İptal; Returned/UnDelivered → İade *(UnDelivered için doğrulanmalı)*.

## 2. Hepsiburada — https://developers.hepsiburada.com (portal istemci tarafında çiziliyor; uygulama içi tarayıcıyla okundu)

| Konu | Bilgi |
|---|---|
| Kimlik | ✅ HTTP Basic Auth ("kullanıcı adı ve şifre"); **User-Agent başlığı zorunlu**. Doküman, tüm servislerde kimlik yapısının değiştiğini ve satıcı panelinden (Bilgilerim → Entegrasyon → Entegratör Bilgileri) **servis anahtarı** alındığını söylüyor. ⚠️ Kullanıcı adı = merchantId mi, parola = servis anahtarı mı → **doğrulanmalı** (HB entegrasyon ekibi / test hesabı ile). |
| Ortam | ✅ Dokümandaki URL'ler test ortamı; **canlı = URL'den `-sit` çıkarılarak**. Ör. `listing-external-sit.hepsiburada.com` → `listing-external.hepsiburada.com`, `oms-external-sit…` → `oms-external…` |
| Stok gönder | ✅ `POST https://listing-external-sit.hepsiburada.com/listings/merchantid/{merchantId}/stock-uploads`, gövde dizi: `[{"hepsiburadaSku" veya "merchantSku", "availableStock", "maximumPurchasableQuantity"?}]`. Toplu güncellemede tek istekte **en fazla 4000 SKU**, aynı anda bekleyen işlem sayısı **5'i geçemez**. Stok 0 → satışa kapanır. |
| Sonuç | ✅ `GET …/listings/merchantid/{merchantId}/stock-uploads/id/{id}` ⚠️ yanıt şeması sayfada gösterilmiyor (örnek fiyat yanıtında `id`, `status:"Done"`, `total`, `errors` var) → **doğrulanmalı** |
| Kanal stoğu | ✅ `GET …/listings/merchantid/{merchantId}?offset&limit&merchantSkuList&hbSkuList` → `MerchantSku`, `HepsiburadaSku`, `AvailableStock`, `IsSalable` |
| Siparişler | ✅ Model **kalem bazlı**: `GET https://oms-external-sit.hepsiburada.com/orders/merchantid/{merchantId}?offset&limit(≤100, zorunlu)&begindate&enddate` → ödemesi tamamlanmış (Open/Unpacked) kalemler: `items[].id` (kalem), `orderNumber`, `orderDate`, `lastStatusUpdateDate`, `sku` (HBSKU), `merchantSku`, `quantity`, `unitPrice.amount`, `totalPrice.amount`, `commission.amount`, `customerName`, `shippingAddress`, `cargoCompany`. Diğer durumlar ayrı uçlardan: `/orders/merchantid/{id}/paymentawaiting` (ödeme bekleyen — **dokümana göre stok rezerve edilmeli**), `/orders/merchantid/{id}/cancelled`, `/packages/merchantid/{id}/shipped`, `/packages/merchantid/{id}/delivered` (+ teslim edilemeyen, unpack). Limit ≤50. |
| Limitler | ✅ Sipariş servisleri: saniyede 1000 istek, aşılınca 429 (`X-RateLimit-*` başlıkları). Komisyon sorgusu 240/dk. |
| Durum uçları | ✅ İptal (`/orders/merchantid/{id}/cancelled`: orderNumber, lineItemId, cancelDate, cancelledBy, quantity, sku, merchantSku), kargoya verilen (`/packages/merchantid/{id}/shipped`: orderNumber, PackageNumber, Barcode, ShippedDate), teslim (`/packages/merchantid/{id}/delivered`: orderNumber, DeliveredDate). Hepsi **son 1 ay**, limit ≤50, tarih penceresi ≤24 saat. Kalem durumları: Open, Packaged, InTransit, Delivered, CancelledByMerchant, CancelledByCustomer, CancelledBySap, ClaimCreated. |
| ⚠️ Belirsiz | `begindate/enddate` biçimi; POST stok yanıtı; liste kök şemaları; iade (Talep Entegrasyonu) uçları henüz okunmadı. |

**Durum eşleme önerisi:** paymentawaiting → Yeni (rezerve); Open → Onaylandı; paketlenmiş → Hazırlanıyor; shipped → Kargoda; delivered → TeslimEdildi; cancelled → İptal; teslim edilemedi/iade talebi → İade.

## 3. Beymen — https://partner-docs.beymen.com/en (+ Türkçe sürüm)

| Konu | Bilgi |
|---|---|
| Kimlik | ✅ `Authorization: Basic Base64(ApiKey:ApiPassword)` (Partner Portal → Hesap Bilgileri → Entegrasyon Bilgileri). İngilizce yanıt için `Accept-Language: en`. |
| Ortam | ✅ Prod `https://partner-api.beymen.com` · Stage `https://stage-partner-api.beymen.com` |
| Stok gönder | ✅ `POST /batch/products/price-stock`, `{"items":[{"productId"(GUID), "quantity"}]}` — fiyat alanları opsiyonel. ≤1000 kalem, quantity 0–1.000.000, **aynı istek 15 dk tekrar gönderilemez**. Yanıt `{"processId"}`. ⚠️ Anahtar **Beymen productId (GUID)**: eşleştirmede `KanalUrunKodu` olarak bu tutulmalı (barkodla `GET /products?barcode=` üzerinden bulunabilir). |
| Sonuç | ✅ `GET /batch/products/price-stock/{processId}` → `items[]{productId, success, message}`; tamamlanmadıysa **HTTP 400 "The matching is incomplete."** (= beklemede). |
| Kanal stoğu | ✅ `GET /products/price-stock/search?barcodes=`(≤50)`&size`(≤1000) → `items[]{id, barcode, stock.count}` |
| Siparişler | ✅ `GET /orders?page&size&startDate&endDate&shipmentStatus` → `orderNumber`, `orderDate`, `shipmentStatus` (NewOrder 1, ReadyToShip 2, Shipped 4, DeliveredToCustomer 8, Cancelled 16, Divided 32), `customerName`, `shipmentAddress.city/district`, `shipmentProvider`, `shipmentTrackingNumber`, `totalAmount`, `shipmentLines[]{productId, quantity, price, commission, product.barcode, product.stockCode}` |
| İptal / iade | ✅ İptaller `GET /orders?shipmentStatus=Cancelled` (CanceledDateTime'a göre). İadeler `GET /claims` (New 0, ActionNeeded 1, Approved 2, Reject 3, Dispute 4). |
| ⚠️ Belirsiz | Tarih parametre biçimi (yalnızca "DateTime"); siparişte "son güncellenme" alanı yok → örtüşmeli pencere sipariş tarihine göre olur, durum değişiklikleri için ayrıca durum bazlı sorgu gerekir; hız limiti yazılmamış. |

## 3a. Satıcı Bilgileri (2026-11-18 okundu, ADR-025)

| Kanal | Kargo firmaları | İade red gerekçeleri |
|---|---|---|
| Trendyol | ✅ `GET /product/lookup/cargo-providers` → `[{code, name}]` (docs/cargo-providers-service). Dokümana göre ürün oluşturma/güncellemedeki `cargoProviders` alanı için. ⚠️ Sipariş tarafında aynı kodların kullanıldığı doğrulanmalı. | ✅ `GET /order/claim-issue-reasons` → `[{id, name}]` (docs/claim-issue-reasons-getclaimsissuereasons). `id` → createClaimIssue `claimIssueReasonId`. 1651 ilk 24 saatte seçilemez. |
| Hepsiburada | ⚠️ `GET https://shipping-external-sit.hepsiburada.com/cargoFirms/{merchantId}` ("teslimat profili oluşturmada kullanılır"); yanıt şeması sayfada yok. | ⚠️ `POST oms-external-sit…/claims/number/{claimNumber}/reject` gövdesi `{ClaimRejectionReason (string, zorunlu), MerchantStatement (zorunlu), Reports[], UploadedReportsUrls[]}`; değer listesi yok (örnek "PhotoNotClear"). |
| Beymen | ✅ Dokümanda sabit "Cargo Companies" tablosu (Code / Description), servis yok. | ✅ "Problem Reporting of Return" → `PATCH /claims/{id}/dispute`, `reasonType` = `SellerReasonType` (0–11); `images` PackageIsEmpty dışında zorunlu. |

## 4. softcotton.com.tr
Altyapı hâlâ bilinmiyor → mock adaptör devam ediyor (sorulacak).

## 5. Ortak sonuçlar
- Üç kanal da **asenkron toplu stok** + **sonuç sorgulama** modelinde: mevcut `PushStockAsync` / `CheckBatchAsync` sözleşmesi uygun.
- Trendyol ve Beymen'de **aynı isteğin 15 dk tekrar gönderilememesi** var: yeniden denemede istek içeriği değişmezse 400 alınabilir → bu hata "zaten gönderildi" olarak ele alınmalı (doğrulanmalı).
- Eşleştirme anahtarları: Trendyol **barkod**, Hepsiburada **merchantSku** (veya HBSKU), Beymen **productId (GUID)**.
- Kimlik bilgileri `Channel.KimlikSifreli` alanında `IDataProtector` ile şifreli saklanacak.
