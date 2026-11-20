using PazaryeriHub.Application.Questions;
using PazaryeriHub.Domain.Orders;

namespace PazaryeriHub.Application.Channels;

/// <summary>
/// Kanal adaptörü sözleşmesi (CLAUDE.md §6). Çekirdek kanalı tanımaz; her kanal bu arayüzü uygular.
/// Kimlik doğrulama, hız limiti, sayfalama ve hata çevirisi adaptörün içinde çözülür.
/// Bütün çağrılar Hangfire job'larından yapılır (ilke 5).
/// </summary>
public interface IChannelAdapter
{
    /// <summary>TRENDYOL | HEPSIBURADA | BEYMEN | SOFTCOTTON</summary>
    string ChannelCode { get; }

    /// <summary><paramref name="sinceUtc"/> sonrasında oluşturulan veya güncellenen siparişler.</summary>
    Task<IReadOnlyList<NormalizedOrder>> FetchOrdersAsync(DateTime sinceUtc, CancellationToken ct);

    Task<IReadOnlyList<ChannelStock>> FetchStockAsync(IEnumerable<string>? skus, CancellationToken ct);
    Task<BatchResult> PushStockAsync(IReadOnlyList<StockUpdate> updates, CancellationToken ct);
    Task<BatchResult> PushPriceAsync(IReadOnlyList<PriceUpdate> updates, CancellationToken ct);
    Task<BatchStatus> CheckBatchAsync(string batchId, CancellationToken ct);

    /// <summary>
    /// Kanalın fiyat gönderim kuralı (ADR-028; spesifikasyon §6'ya EK; statik, kanal çağrısı yok). KDV'nin dahil olduğu
    /// resmi dokümanda doğrulanmamışsa <see cref="PriceUpdateRule.VatIncluded"/> null'dır ve FIYAT yeteneği açılamaz.
    /// </summary>
    PriceUpdateRule DescribePriceUpdate();

    /// <summary>
    /// Sipariş durumu / kargo bildirimi (ADR-026). Spesifikasyon §6'daki <c>(channelOrderNo, status, cargo)</c> imzasından
    /// SAPMA: kanallar satır ve paket kimliği istediği için istek nesnesi alır. Kanala yazar → yalnızca KanalKomutGonder
    /// job'ından ve yazma kapısından (GuardedChannelAdapter) geçerek çağrılır.
    /// </summary>
    Task UpdateOrderStatusAsync(OrderStatusUpdate update, CancellationToken ct);

    /// <summary>Bu kanalda hedef duruma bildirim yapılabilir mi, hangi alanlar zorunlu (statik; kanal çağrısı yok).</summary>
    /// <param name="ownCargo">Sipariş kanalın anlaşmalı kargosu yerine kendi kargomuzla mı gönderiliyor.</param>
    OrderNotificationRule DescribeOrderNotification(OrderStatus target, bool ownCargo);
    Task<IReadOnlyList<NormalizedReturn>> FetchReturnsAsync(DateTime sinceUtc, CancellationToken ct);

    /// <summary>
    /// İade kararı bildirimi (ADR-027; spesifikasyon §6'ya EK). Kanala yazar → yalnızca KanalKomutGonder'den ve yazma
    /// kapısından (IADE_KARAR yeteneği) geçerek çağrılır.
    /// </summary>
    Task DecideReturnAsync(ReturnDecisionRequest request, CancellationToken ct);

    /// <summary>Kanalın bu karar tipi için kuralı (statik; kanal çağrısı yok).</summary>
    ReturnDecisionRule DescribeReturnDecision(ReturnDecisionKind kind);

    /// <summary>
    /// Kanaldaki ürün kataloğu (sayfalı; ADR-022). <paramref name="cursor"/> null → ilk sayfa; dönen
    /// <see cref="ChannelCatalogPage.NextCursor"/> null ise son sayfadır. İmleç adaptöre özeldir (opak).
    /// Yalnızca resmi dokümanla doğrulanmış alanlar doldurulur; kanal vermiyorsa alan null kalır.
    /// </summary>
    Task<ChannelCatalogPage> FetchCatalogAsync(string? cursor, CancellationToken ct);

    /// <summary>
    /// Satıcı Bilgileri: kanal referans listesi (ADR-025; spesifikasyon §6'ya EK). Salt okumadır.
    /// Kanal bu listeyi doğrulanmış bir servis veya doküman listesiyle vermiyorsa <see cref="NotSupportedException"/> fırlatır.
    /// </summary>
    Task<ChannelReferenceList> FetchReferenceAsync(ChannelReferenceType type, CancellationToken ct);

    /// <summary>
    /// Müşteri soruları (ADR-029; spesifikasyon §6'ya EK). Salt okuma; müşteri adı/kimliği döndürülmez (KVKK).
    /// Kanalın soru API'si resmi dokümanda doğrulanmamışsa <see cref="NotSupportedException"/> fırlatır.
    /// </summary>
    Task<IReadOnlyList<NormalizedQuestion>> FetchQuestionsAsync(DateTime sinceUtc, CancellationToken ct);

    /// <summary>Soru cevabı. Kanala yazar → yalnızca KanalKomutGonder'den ve yazma kapısından (SORU_CEVAP) geçerek çağrılır.</summary>
    Task AnswerQuestionAsync(QuestionAnswerRequest request, CancellationToken ct);

    /// <summary>Kanalın cevap kuralı (uzunluk; statik, kanal çağrısı yok).</summary>
    QuestionAnswerRule DescribeQuestionAnswer();
}

/// <summary>Kanal kataloğundaki tek ürün (varyant). Alanlar kanalın verdiği kadar doludur.</summary>
/// <param name="ChannelProductCode">Kanalın ürün kimliği (Hepsiburada HBSKU, Beymen productId). Trendyol'da barkod kimliktir → null.</param>
/// <param name="StockCode">Satıcının kendi stok kodu (Hepsiburada merchantSku, Trendyol stockCode…).</param>
/// <param name="Passive">Kanalda kapalı / arşivlenmiş.</param>
/// <param name="VariantKey">
/// Kanalın varyantı kesin tanımlayan anahtarı (Trendyol: içerik kimliği + varyant özellikleri). Doluysa ürün oluştururken yalnızca
/// aynı anahtarlı satırlar tek SKU'da birleşir; farklı renk/ebat içerikleri asla birleşmez (ADR-031).
/// </param>
/// <param name="VariantAttributes">Kanalın varyant özellikleri, okunur metin ("Renk: Krem; Ebat: 75x150"); bilgi amaçlı.</param>
/// <param name="ProductUrl">Ürünün kanaldaki sayfası (yalnızca kanal API'si veriyorsa; adres biçimi tahmin edilmez).</param>
/// <param name="SalePrice">Kanalda görünen satış fiyatı (salt okuma; KDV durumu kanal dokümanına bağlı).</param>
/// <param name="ListPrice">Kanalda görünen liste fiyatı.</param>
public sealed record ChannelCatalogEntry(
    string? ChannelProductCode,
    string? Barcode,
    string? StockCode,
    string? ModelCode,
    string? Color,
    string? Size,
    string? Name,
    string? Brand,
    string? Category,
    string? ChannelStatus,
    bool Passive,
    string? VariantKey = null,
    string? VariantAttributes = null,
    string? ProductUrl = null,
    decimal? SalePrice = null,
    decimal? ListPrice = null,
    string? ImageUrl = null);

public sealed record ChannelCatalogPage(IReadOnlyList<ChannelCatalogEntry> Items, string? NextCursor);

/// <summary>Kanaldan gelen siparişin kanal bağımsız hali.</summary>
/// <param name="ChannelOrderNo">Kanal sipariş numarası (idempotentlik anahtarı, büyük/küçük harf duyarlı).</param>
/// <param name="Status">Kanaldaki durumun normalize edilmiş karşılığı.</param>
/// <param name="Customer">KVKK: yalnızca gerekli özet (ad soyad, il, ilçe). Telefon/e-posta/açık adres TAŞINMAZ.</param>
public sealed record NormalizedOrder(
    string ChannelOrderNo,
    DateTime OrderDateUtc,
    OrderStatus Status,
    decimal TotalAmount,
    IReadOnlyList<NormalizedOrderLine> Lines,
    CustomerSummary? Customer = null,
    CargoInfo? Cargo = null,
    string? ChannelPackageId = null);

/// <summary>Sipariş satırı. SKU eşleştirmesi sırayla: kanal barkodu, kanal ürün kodu (ChannelListing), SKU barkodu, satıcı SKU kodu.</summary>
public sealed record NormalizedOrderLine(
    string? ChannelProductCode,
    string? Barcode,
    string? SellerSku,
    int Quantity,
    decimal UnitPrice,
    decimal? Commission = null,
    string? ChannelLineId = null);

public sealed record CustomerSummary(string? FullName, string? City, string? District);

public sealed record CargoInfo(string? Company, string? TrackingNumber, string? TrackingUrl);

/// <summary>Kanala durum bildirimi (ADR-026).</summary>
/// <param name="ChannelPackageId">Kanalın paket/sipariş kimliği (Trendyol shipmentPackageId, Beymen sipariş id GUID).</param>
/// <param name="Lines">Kanal satır kimlikleri ve adet (Trendyol lineId, Hepsiburada kalem id).</param>
/// <param name="OwnCargo">Kendi kargomuzla gönderim (Kargoda bildiriminde takip bilgisi bizden gider).</param>
public sealed record OrderStatusUpdate(
    string ChannelOrderNo, string? ChannelPackageId, OrderStatus Status, IReadOnlyList<OrderStatusUpdateLine> Lines, CargoInfo? Cargo, bool OwnCargo);

public sealed record OrderStatusUpdateLine(string ChannelLineId, int Quantity);

/// <summary>Kanalın bir hedef durum için bildirim kuralı.</summary>
/// <param name="Supported">Bildirim bu kanalda uygulanmış ve dokümanla doğrulanmış.</param>
/// <param name="NotRequired">Kanal bu durumu kendisi günceller (ör. anlaşmalı kargo teslim alınca Kargoda); bildirim yapılmaz.</param>
public sealed record OrderNotificationRule(
    bool Supported,
    bool NotRequired,
    string? Note,
    bool RequiresPackageId = false,
    bool RequiresLineIds = false,
    bool RequiresTrackingNumber = false,
    bool RequiresTrackingUrl = false,
    bool RequiresCargoCompany = false)
{
    public static OrderNotificationRule NotSupportedBecause(string note) => new(false, false, note);
    public static OrderNotificationRule NotRequiredBecause(string note) => new(false, true, note);
}

/// <summary>Kanalda görünen stok (mutabakat için).</summary>
public sealed record ChannelStock(string? ChannelProductCode, string? Barcode, int Quantity);

public sealed record StockUpdate(string? ChannelProductCode, string? Barcode, int Quantity);

/// <param name="ListPrice">Liste (üstü çizili) fiyat; KDV dahil.</param>
/// <param name="SalePrice">Satış fiyatı; KDV dahil.</param>
public sealed record PriceUpdate(string? ChannelProductCode, string? Barcode, decimal ListPrice, decimal SalePrice);

/// <summary>Kanalın fiyat gönderim kuralı (ADR-028).</summary>
/// <param name="Supported">Fiyat gönderimi bu adaptörde uygulanmış ve uç noktası resmi dokümanda doğrulanmış.</param>
/// <param name="VatIncluded">
/// Kanalın beklediği fiyatın KDV dahil olduğu resmi dokümanda yazıyor mu: true = dahil (doğrulandı), false = hariç (doğrulandı;
/// uygulama KDV oranı tutmadığı için desteklenmez), null = dokümanda yok → <b>yetenek açılamaz</b> (varsayım yapılmaz).
/// </param>
/// <param name="SendsListPrice">Kanal liste (üstü çizili) fiyatı da alıyor; false ise yalnızca satış fiyatı gider.</param>
/// <param name="SamePriceForProductVariants">Kanal aynı ürünün tüm beden varyantlarına aynı fiyatı istiyor (Beymen).</param>
/// <param name="Source">Doğrulama kaynağı (doküman sayfası).</param>
public sealed record PriceUpdateRule(bool Supported, bool? VatIncluded, bool SendsListPrice, bool SamePriceForProductVariants, string Note, string? Source = null)
{
    /// <summary>Yetenek DENEME/ACIK yapılabilir mi.</summary>
    public bool CanEnable => Supported && VatIncluded == true;

    public string? BlockReason => !Supported ? $"Fiyat gönderimi desteklenmiyor: {Note}"
        : VatIncluded is null ? $"KDV dahil/hariç resmi dokümanda doğrulanamadı; fiyat yeteneği açılamaz. {Note}"
        : VatIncluded == false ? "Kanal KDV hariç fiyat istiyor; uygulama KDV oranı tutmadığı için desteklenmiyor."
        : null;

    public static PriceUpdateRule NotSupported(string note) => new(false, null, false, false, note);
}

/// <summary>Toplu gönderim sonucu. Kanal işi asenkron işliyorsa <see cref="BatchId"/> ile sonradan sorgulanır.</summary>
public sealed record BatchResult(string? BatchId, bool Accepted, string? Error = null);

public enum BatchState
{
    Pending,
    Completed,
    PartiallyFailed,
    Failed
}

public sealed record BatchItemError(string? ChannelProductCode, string? Barcode, string Message);

public sealed record BatchStatus(string BatchId, BatchState State, IReadOnlyList<BatchItemError> Errors);

/// <param name="ReturnNo">Kanalın iade talebi kimliği (Trendyol claimId, Beymen claim id). İdempotentlik anahtarı.</param>
/// <param name="ChannelStatus">Kanaldaki talep durumu (ham ad; ör. Trendyol WaitingInAction).</param>
public sealed record NormalizedReturn(
    string ChannelOrderNo,
    string? ReturnNo,
    DateTime CreatedUtc,
    IReadOnlyList<NormalizedReturnLine> Lines,
    string? Reason = null,
    string? ChannelStatus = null);

/// <param name="LineKey">Kanal içinde satırı tanımlar (Trendyol sipariş satırı id, Beymen productId).</param>
/// <param name="ChannelItemIds">Karar servislerinin istediği kalem kimlikleri (Trendyol claimItem id'leri).</param>
public sealed record NormalizedReturnLine(
    string? ChannelProductCode, string? Barcode, int Quantity, string? LineKey = null, IReadOnlyList<string>? ChannelItemIds = null);

public enum ReturnDecisionKind
{
    /// <summary>İadeyi onayla.</summary>
    Approve,

    /// <summary>İadeyi reddet / sorun bildir (gerekçe zorunlu).</summary>
    Reject,

    /// <summary>İade paketi elime ulaştı (Beymen "I Have Received").</summary>
    Received
}

public sealed record ReturnDecisionLine(string LineKey, string? ChannelProductCode, IReadOnlyList<string> ChannelItemIds, int Quantity);

public sealed record ReturnDecisionRequest(
    string ReturnNo, ReturnDecisionKind Kind, IReadOnlyList<ReturnDecisionLine> Lines, string? ReasonCode, string? Description);

/// <param name="AllowedReasons">Reddetmede kullanılabilecek gerekçe kodları; null = kanalın tüm red gerekçeleri (M1 referansı).</param>
public sealed record ReturnDecisionRule(bool Supported, string? Note, bool RequiresReason = false, IReadOnlyList<string>? AllowedReasons = null)
{
    public static ReturnDecisionRule NotSupportedBecause(string note) => new(false, note);
}
