# Kanal API Notları (Aşama 6 hazırlık)

**Tarih:** 2026-11-16 · **Kaynak:** yalnızca resmi geliştirici dokümanları (üçüncü taraf bloglar kullanılmadı).
**Kapsam kararları:** tek depo (ADR-015); fiyat gönderimi ADR-028 ile eklendi (bkz. §3d; KDV doğrulanmayan kanalda kapalı). Gerekenler: **stok gönderme + sonuç sorgulama, sipariş çekme, iade/iptal çekme, mutabakat için kanal stoğu okuma.**

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
| Limitler | ✅ Aynı uca 10 sn'de en fazla 50 istek (51. → 429). Sipariş listeleme (Get Shipment Packages) 30 / 40 / 50 / 100 / 100 istek/dk (listeleme kademesine göre: 50.000 / 75.000 / 150.000 / 500.000 / limitsiz ürün; 2026-11-18 yeniden okundu). Paket bildirim 300 istek/dk'dan başlar. Ürün tarafında 14.09.2026'dan itibaren grup bazlı limitler var (Inventory & Price Write 350 istek/dk'dan başlar; aynı barkoda fiyat güncellemesi 30 istek/dk). | authorization, 1-service-limitations |
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

## 3b. Sipariş durumu bildirimi (2026-11-18 okundu, ADR-026)

- **Trendyol:**
  - ⚠️ **Order V2:** `/order/sellers/{sellerId}/orders` 15.10.2026'dan itibaren kullanımdan kalktı → `/order/sellers/{sellerId}/v2/orders` (getShipmentPackages sayfası).
    - Parametreler aynı (startDate/endDate ms, page, size, status, orderByField, orderByDirection, shipmentPackageIds); en fazla iki haftalık aralık.
    - V2 durum listesine `Verified` eklenmiş (anlamı yok; bilinmeyen durum olarak atlanır).
    - Yanıt: `shipmentPackageId`, `shipmentPackageStatus`, `packageHistories`, `packageTotalPrice`, `orderDate`, `lines[].lineId`, `lineUnitPrice`, `stockCode`, `commission` (oran; kullanılmıyor).
  - ✅ **Order Process Flow:** Picking → Invoiced (**opsiyonel**) → paket kargoya verilince Shipped (takip no Trendyol'dan).
  - ✅ **updatePackage:** `PUT /order/sellers/{sellerId}/shipment-packages/{packageId}` `{lines:[{lineId, quantity}], params:{}, status:"Picking"}`. Invoiced `params.invoiceNumber` ister → kullanılmıyor.
  - ✅ **Shipping Alternative Delivery:** `PUT …/{packageId}/alternative-delivery` `{isPhoneNumber, trackingInfo, params:{}}`; başarıda Shipped. ⚠️ Veri tipi tablosu "isPhoneNumber should be always true" diyor, link örneğiyle çelişkili.
- **Hepsiburada:**
  - ✅ **Paketleme:** `POST oms-external-sit…/packages/merchantid/{merchantId}` `{lineItemRequests:[{id, quantity, serialNumbers?}], cargoCompany?, carrier? (satıcı gönderiminde zorunlu), deci?, parcelQuantity?, warehouse?}`. ⚠️ Yanıt şeması yok.
  - ✅ **Kargoda (mağaza hesabı):** `POST …/packages/merchantid/{merchantId}/packagenumber/{packagenumber}/intransit` `{shippedDate (zorunlu), trackingNumber?, trackingUrl?, trackingPhoneNumber?, …}`. ⚠️ Paket numarası ve tarih biçimi doğrulanmadı.
- **Beymen:**
  - ✅ **Picking:** `PUT /orders/{id veya mpOrderId}/status/picking`.
  - ✅ **Manual Progression:** `PUT /orders/{id}/shipment/status` `{trackingNumber*, trackingUrl, cargoCompanyCode*, status (4 Shipped, 8 DeliveredToCustomer, 16 Undeliverable)}` → 202.
  - ✅ **Alternatif teslimat:** `PUT /orders/{id}/alternativeDelivery` `{shipmentTrackingUrl, deliveryType (0 farklı kargo, 1 kendi araç), shipmentTrackingNumber, cargoCompanyCode}`.

## 3c. İade talepleri (2026-11-18 okundu, ADR-027)

- **Trendyol:**
  - ✅ getClaims `GET /order/sellers/{sellerId}/claims`.
    - Parametreler: claimIds, claimItemStatus (Created, WaitingInAction, Accepted, Cancelled, Rejected, Unresolved, InAnalysis), startDate/endDate (**lastModifiedDate'e göre**, ms), orderNumber, page, size.
    - Yanıt: `id/claimId`, `orderNumber`, `orderShipmentPackageId` ("iade paketinin id'si"), `claimDate`, `items[].orderLine{id, barcode, merchantSku}`, `items[].claimItems[]{id, claimItemStatus.name, customerClaimItemReason, acceptedBySeller, autoAccepted}`.
    - 48 saat aksiyon bekleyen iadeler otomatik kabul edilir.
  - ✅ approveClaimLineItems `PUT …/claims/{claimId}/items/approve` `{claimLineItemIdList, params:{}}`. Yalnızca WaitingInAction; sonra WaitingFraudCheck görülebilir.
  - ✅ createClaimIssue `POST …/claims/{claimId}/issue?claimIssueReasonId&claimItemIdList&description(≤500)`. Dosya eki (form-data `files`, ≤5 MB) 1651, 451 ve 2101 dışında **zorunlu**.
- **Beymen:**
  - ✅ `GET /claims`: `id`, `orderNumber`, `shipmentNumber`, `shipmentLines[]{id, productId, quantity, status, product.barcode, customerReason, sellerReason}`.
  - ✅ `PATCH /claims/{id}/receive` (202), `PATCH /claims/{id}/approve` `[{productId, quantity}]` (202), `PATCH /claims/{id}/dispute` (images PackageIsEmpty dışında zorunlu).
- **Hepsiburada (Talep Entegrasyonu):**
  - ⚠️ `GET oms-external-sit…/claims/merchantId/{merchantId}?beginDate&endDate (yyyy-MM-dd HH:mm)&offset&limit`, durum bazlı `…/status/{status}`. **Yanıt şeması yok.**
  - ✅ `POST …/claims/number/{claimNumber}/accept` (FinalizedWith: Refund/Change), `…/reject` `{ClaimRejectionReason*, MerchantStatement*, Reports?, UploadedReportsUrls?}`, `…/preapprovalconfirm`.

## 3d. Fiyat (2026-11-18 okundu, ADR-028)

- **Trendyol:**
  - ✅ `POST https://apigw.trendyol.com/integration/inventory/sellers/{sellerId}/products/price-and-inventory` (stage: `stageapigw…`).
    - `items[]{barcode*, quantity, salePrice, listPrice}`; ≤1000 kalem.
    - "listPrice cannot be lower than salePrice"; "When updating only stock or price, sending the other field is not required."
    - "List price is product original price. It is the price crossed out when the selling price is low."
    - Aynı istek 15 dk tekrarlanamaz. Yanıt `{batchRequestId}`.
    - Kaynak: `/v2.0/docs/stock-and-price-update-updatepriceandinventory`, `/v2.0/reference/updatepriceandinventory`.
  - ⚠️ **KDV dahil/hariç dokümanda yazmıyor.** Ürün oluşturmada ayrı `vatRate` alanı var; fiyatın KDV'li olup olmadığı belirtilmiyor.
- **Hepsiburada (Listeleme):**
  - ✅ `POST https://listing-external-sit.hepsiburada.com/listings/merchantid/{merchantId}/price-uploads`, gövde `[{hepsiburadaSku | merchantSku, price*}]`.
  - ✅ Sonuç `GET …/price-uploads/id/{id}` (`op=uploadPrice`, `op=getPriceUpload`).
  - ✅ Rehber "Listing Bilgilerini Güncelleme":
    - ≤4000 SKU; aynı anda en fazla 5 bekleyen yükleme.
    - Fiyat 0 → listing satışa kapanır.
    - Kategori eşiği dışındaki fiyat listingi kilitler; sonuçta `priceValidations[]{hepsiburadaSku, type: MinLock|MaxLock, minPrice, maxPrice, description}`.
  - ⚠️ **KDV dahil/hariç dokümanda yazmıyor**; liste fiyatı alanı yok; POST yanıt şeması yok.
- **Beymen:**
  - ✅ `POST /batch/products/price-stock` `items[]{productId*, quantity, salesPrice, platformPrice}`.
    - "salesPrice: Market sales price (**VAT included**)", "platformPrice: Beymen sales price (**VAT included**)".
    - İkisi birlikte gönderilirse salesPrice ≥ platformPrice.
    - Aynı productId tekrarı → hepsi başarısız. ≤1000 kalem. Aynı istek 15 dk tekrarlanamaz.
  - ✅ Sonuç: "Different price information was detected for size variants of the same product. The same price must be sent for all size variants."

## 3e. Soru-Cevap (2026-11-18 okundu, ADR-029)

- **Trendyol** (`/v2.0/docs/getting-customer-questions`, `/answering-customer-questions`):
  - ✅ `GET https://apigw.trendyol.com/integration/qna/sellers/{sellerId}/questions/filter`
    - Parametreler: barcode, page (0'dan), size (≤50), startDate/endDate (ms; tarih yoksa son 1 hafta, en fazla 2 hafta), status, orderByField (LastModifiedDate | CreatedDate), orderByDirection.
    - Durumlar: WAITING_FOR_ANSWER, WAITING_FOR_APPROVE, ANSWERED, REPORTED, REJECTED, UNANSWERED.
    - Yanıt `content[]{id, text, status, creationDate, productName, productMainId, imageUrl, webUrl, answer{text,…}, rejectedAnswer, customerId, userName, showUserName}`. **customerId ve userName okunmaz.**
  - ✅ `POST …/questions/{id}/answers` `{"text"}` → `{answerId}`.
    - 10–2000 karakter; yalnızca WAITING_FOR_ANSWER cevaplanabilir.
    - Yasaklı kelimede cevap reddedilir ve soru tekrar bekler; limit aşılırsa soru UNANSWERED olur.
  - ⚠️ startDate/endDate'in hangi tarihe uygulandığı net değil.
- **Beymen** (Question-Answer Integration):
  - ✅ `GET /questions/filter?size&page&productName&modelCode&color&brandName&status&startDate&endDate`.
    - Yanıt: `items[]{id, productName, modelCode, color, size, brandName, question, answer, imageUrl, status, maxPermittedResponseTime, actionDate, createdDate}`, `page`, `itemsPerPage`, `pageCount`, `total`.
    - Müşteri bilgisi yok.
  - ✅ `POST /questions/{id}/answer` `{"answer"}` (200).
  - ✅ `POST /questions/{id}/report-issue` `{"reportReason"}` (kullanılmıyor).
  - ⚠️ `status` sayısal, enum değerleri verilmemiş (sıra varsayıldı); tarih biçimi; cevap uzunluk sınırı yok.
- **Hepsiburada** (Satıcıya Sor Entegrasyonu):
  - ✅ Liste: `GET https://api-asktoseller-merchant-sit.hepsiburada.com/api/v1.0/issues`
    - Başlık: merchantId, User-Agent.
    - Parametreler: source, subject, min/maxCreatedAt, min/maxModifiedAt, issueNumber, status (1 WaitingForAnswer, 2 Answered, 3 Rejected, 4 AutoClosed), search, sortBy, desc, page (1'den), size (25).
  - ✅ Cevap: `POST …/issues/{number}/answer`, multipart (`Answer` ≤2000, `Files`). Cevap süresi 1 iş günü (`expireDate`).
  - ✅ Alan açıklamaları: issueNumber, customerId, orderNumber, lineItemId, status, subject, lastContent, conversations[], product{sku, name, stockCode}, expireDate, lastModifiedAt.
  - ⚠️ **Liste yanıtının kök şeması ve tarih parametre biçimi yok** → mock.

## 3f. Katalog: varyant, fiyat, ürün linki (2026-11-18 okundu, ADR-031)

- **Trendyol** (filterApprovedProducts):
  - ✅ `content.contentId`, `variants[].attributes[]{attributeName, attributeValue}`.
  - ✅ `variants[].productUrl`.
  - ✅ `variants[].price{salePrice, listPrice}`.
- **Beymen** (Product Listing, `GET /products?size&page&barcode&modelCode&brand&stockCode&status&substatus`):
  - ✅ `items[]{id, name, barcode, modelCode, stockCode, brand{name}, category{name}, merchantColorCode, size{name}, platformColor{name}, salesPrice{value}, platformSalesPrice{value}, stock{count}, status{status, localizedDescription}}`, `page`, `itemsPerPage`, `pageCount`, `total`.
  - ⚠️ Ürün sayfası adresi yok. Sayfa boyutu üst sınırı yok.
- **Hepsiburada** (Listing Bilgilerini Sorgulama):
  - ✅ Alan tablosu: HepsiburadaSku, MerchantSku, Price, AvailableStock, Pricing.FinalPrice, IsSalable…
  - ⚠️ JSON örneği yok (XML örneğinde `ProductName`, `Price`). Ürün sayfası adresi yok.

## 4. softcotton.com.tr
Altyapı hâlâ bilinmiyor → mock adaptör devam ediyor (sorulacak).

## 5. Ortak sonuçlar
- Üç kanal da **asenkron toplu stok** + **sonuç sorgulama** modelinde: mevcut `PushStockAsync` / `CheckBatchAsync` sözleşmesi uygun.
- Trendyol ve Beymen'de **aynı isteğin 15 dk tekrar gönderilememesi** var: yeniden denemede istek içeriği değişmezse 400 alınabilir → bu hata "zaten gönderildi" olarak ele alınmalı (doğrulanmalı).
- Eşleştirme anahtarları: Trendyol **barkod**, Hepsiburada **merchantSku** (veya HBSKU), Beymen **productId (GUID)**.
- Kimlik bilgileri `Channel.KimlikSifreli` alanında `IDataProtector` ile şifreli saklanacak.

## Hepsiburada test ortamı (SIT) doğrulama — canlıya geçiş adımları (2026-11-19, ADR-037)

Kaynak: developers.hepsiburada.com, uygulama içi tarayıcıyla okundu (otomatik istekler 403 alıyor). Sayfalar: "Katalog Ürün Entegrasyonu Test Süreci Adımları", "Listeleme Entegrasyonu Test Süreci Adımları", "Sipariş Entegrasyonu Test Süreci Adımları" ve ilgili uç sayfaları. Kimlik: Basic Auth + User-Agent. Canlı bilgi: test adımları bitince canlı Merchant Panel → Yardım Merkezi → Talepler → API Entegrasyon Teknik Destek ticket'ı (katalog için trackingId iletilir).

| Alan | Uç (SIT) | Dokümandan | SIT'te görülen |
|---|---|---|---|
| Katalog — Hızlı Ürün Yükleme | `POST https://mpop-sit.hepsiburada.com/product/api/products/fastlisting` | ✅ gövde dizi `[{merchant*, merchantSku*, productName*, barcode, hbSku, stock(string), price(string), itemOrderID}]`; yalnızca HB katalogunda olan barkodlar yüklenir | ✅ `{"trackingId":"<guid>"}` |
| Katalog — Ürün Durumu Sorgulama | `GET …/product/api/products/status/{trackingId}?version=1&page=0&size=1000` | ✅ | ✅ `{success, totalElements, data[]{merchantSku, importStatus (PROCESSING / FAILED…), importMessages[]{severity, message}, hbSku, productStatus…}}`. Dokümandaki test barkodları (7541828790114, 7541828790155, 7541828790080) fastlisting'de "HB product not found by barcode" → FAILED |
| Katalog — Yeni ürün (tam akış) | kategoriler `GET mpop-sit…/product/api/categories/get-all-categories?leaf&status&available&page&size`; özellikler `GET …/categories/{id}/attributes`; değerler `GET …/categories/{id}/attribute/{attributeId}/values?page&size`; gönderim `POST …/products/import?version=1` (multipart `file`, integrator.json); onay/red `POST …/products/approve-prematch` / `reject-prematch` `[{merchant, merchantSkuList}]` | ✅ uçlar ve integrator.json örneği | ✅ kategori ağacı (≈5.000 uç kategori, `data[]{categoryId, name, paths[]}`), özellikler (`data{baseAttributes[], attributes[], variantAttributes[]}`: `{id, name, mandatory, type: string/integer/enum/media/video, multiValue}`), enum değerleri `[{id, value}]`. Bornoz (60005084) zorunluları: merchantSku, UrunAdi, UrunAciklamasi, Barcode, Marka, GarantiSuresi(int), tax_vat_rate, kg, Image1, tipi, paket_icerigi, kumas_dokumasi_, 00000MU (paket görseli), renk_variant_property, beden_variant_property. 2026-11-20'de import her denemede 200 `{"success":false,"code":3003,"message":"File not found"}` döndü. ✅ 2026-11-21: HB'nin istediği sırayla (kategori → özellik → değer → gönderim) aynı istek (multipart `file`, `integrator.json`, octet-stream, tırnaksız boundary; kod değişmedi) kabul edildi: trackingId, `importStatus=SUCCESS`, ürün `Incelenecek`. Test barkodlu 3 ürün de `Incelenecek`; onay/red uçları 200 (boş gövde), ürünler `Eşleşen` olmadığı için durum değişmedi. **HB destek (2026-11-21): test ortamında statü ilerletilemiyor; `Incelenecek` statüsü + onay/red çağrılarının hatasız kayıtları yeterli → katalog testi tamamlandı sayılır.** Doküman yeni portalda: developers.hepsiburada.com/tr/companies/hepsiburada → Ürün Yönetimi → Katalog (op=uploadProductViaFile) |
| Listeleme — Listing sorgulama | `GET listing-external-sit…/listings/merchantid/{id}?offset&limit` | ✅ | ✅ `{listings[]{listingId, hepsiburadaSku, merchantSku, price, availableStock, dispatchTime, cargoCompany1-3, shippingAddressLabel, claimAddressLabel, isSalable, isLocked, lockReasons, productId, …}}` — **`productName` YOK** |
| Listeleme — Envanter güncelleme | `POST https://listing-external-sit.hepsiburada.com/listings/merchantid/{merchantId}/inventory-uploads` (+ `GET …/inventory-uploads/id/{id}`) | ✅ Uç sayfası (op=uploadInventory, 2026-11-21): sarmalayıcısız dizi; `price` number (double) zorunlu, `availableStock` int zorunlu; `hepsiburadaSku` / `merchantSku` biri yeterli; opsiyonel fixedShippingPrice, cargoCompany1-3, shippingAddressLabel, claimAddressLabel, maximumPurchasableQuantity, customizationText*, bt/ytCargoCompany, availableWarehouses, shippingProfileName, hasInstallation; `User-Agent` zorunlu; cURL örneği `Content-Type: application/json`. `dispatchTime` şemada yok, destek örneğinde ve rehberde var (gönderildi, hata yok). | ✅ 2026-11-21 SIT: `{"id"}` → `Ready` → `Done`, `errors: null`. **1) Başlık:** `application/json; charset=utf-8` ile her biçim 400 "An error occurred while deserializing input data"; eksiz `application/json` ile kabul. **2) Fiyat:** destek örneğindeki METİN `"200.00"` kabul edildi ama fiyat **20000** kaydedildi (nokta binlik ayırıcı); SAYI `200` doğru kaydedildi. Test listingi HBV00001136ES 200'e geri alındı ve okunarak doğrulandı. SIT istemcisi yalnızca sayı gönderir. Uygulama adaptörü bu ucu kullanmaz (stok/fiyat `stock-uploads` / `price-uploads`, sayısal fiyat, charset ekiyle 200). |
| Listeleme — Stok / Fiyat | `POST …/stock-uploads`, `…/price-uploads` (+ `/id/{id}`) | ✅ | ✅ POST `{"id":"<guid>"}`; sorgu `{id, status: Ready → Done, createdAt, total, errors, priceValidations}` |
| Listeleme — Activate / Deactivate | `POST …/sku/{sku}/activate`, `…/deactivate` (gövde `{}`) | ✅ (`{sku}` türü yazmıyor) | ✅ HBSKU ile 200 |
| Listeleme — Toplu kilit kaldırma | `POST …/bulk-unlock` `{hbSkuList:[…]}` | ✅ | ✅ uç çalışıyor; kilitsiz listing için 400 code 1163 "Unlocked listings can not unlock." |
| Sipariş — Test siparişi | `POST https://oms-stub-external-sit.hepsiburada.com/orders/merchantId/{merchantId}` (yalnızca TEST) | ✅ `LineItems[]{MerchantId*, Sku*, MerchantSku, Quantity, Price{Amount, Currency}, …}`; OrderNumber "opsiyonel" | ✅ Hepsiburada desteğinin örneğiyle (2026-11-20): `OrderNumber` (10 haneli sayısal), `OrderDate` (saat dilimsiz), satırda `CargoCompanyId: 21`, `DeliveryOptionId: 1` → 201 `{}`. Bunlar olmadan "ShippingMethodId cannot be empty" |
| Sipariş — sonraki adımlar | ödemesi tamamlanmışlar `GET oms-external-sit…/orders/merchantid/{id}`; `GET …/lineitems/merchantid/{id}/packageablewith/lineitemid/{lineItemId}`; paketleme `POST …/packages/merchantid/{id}`; paketler `GET …/packages/merchantid/{id}` | ✅ | ✅ test siparişi listede göründü (`{totalCount, items[]{id, orderId, orderNumber?, sku, quantity, dueDate, …}}`); adaptörle paketleme 201 `{"packageNumber","barcode"}`; paket listesi dizi `[{id, status, packageNumber, barcode, cargoCompany, …}]`. ⚠️ packageablewith tek kalemli siparişte 404 (Hepsiburada'ya soruldu) |
