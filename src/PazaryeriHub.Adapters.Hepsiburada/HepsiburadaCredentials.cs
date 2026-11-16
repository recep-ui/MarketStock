using System.Text.Json;
using PazaryeriHub.Application.Common;

namespace PazaryeriHub.Adapters.Hepsiburada;

/// <summary>
/// Hepsiburada API bilgileri. Kaynak: developers.hepsiburada.com (Listeleme / Sipariş Entegrasyonu Önemli Bilgiler) —
/// "API, HTTP Basic Auth. ile korunmaktadır ... kullanıcı adı ve şifreyi HTTP Authorization Header bilgisinde göndermelidir";
/// User-Agent başlığı zorunlu; servis anahtarı satıcı panelinden (Bilgilerim → Entegrasyon → Entegratör Bilgileri) alınır.
/// // TODO: doğrulanmalı — kullanıcı adı/şifrenin tam olarak hangi değerler olduğu (merchantId / servis anahtarı / entegratör kullanıcısı)
/// dokümanda açık değil; bu yüzden Hepsiburada'nın verdiği değerler olduğu gibi girilir.
/// </summary>
public sealed record HepsiburadaCredentials(string MerchantId, string UserName, string Password, string Environment, string? UserAgent = null)
{
    public const string Production = "Prod";
    public const string Test = "Test";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Dokümana göre: test URL'lerinden "-sit" çıkarılarak canlı URL elde edilir.</summary>
    public string ListingBaseUrl => Environment == Test ? "https://listing-external-sit.hepsiburada.com" : "https://listing-external.hepsiburada.com";

    public string OrderBaseUrl => Environment == Test ? "https://oms-external-sit.hepsiburada.com" : "https://oms-external.hepsiburada.com";

    /// <summary>User-Agent zorunlu; biçimi dokümanda tanımlı değil. // TODO: doğrulanmalı</summary>
    public string EffectiveUserAgent => string.IsNullOrWhiteSpace(UserAgent) ? "PazaryeriHub" : UserAgent.Trim();

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static HepsiburadaCredentials Parse(string json)
    {
        HepsiburadaCredentials? value = null;
        try
        {
            value = JsonSerializer.Deserialize<HepsiburadaCredentials>(json, Json);
        }
        catch (JsonException)
        {
            // aşağıda doğrulama hatası
        }

        var v = new Validator();
        v.Must(Guid.TryParse(value?.MerchantId, out _), "merchantId", "Merchant ID bir GUID olmalı (Hepsiburada'nın verdiği satıcı kimliği).")
         .Must(!string.IsNullOrWhiteSpace(value?.UserName), "userName", "Kullanıcı adı zorunlu.")
         .Must(!string.IsNullOrWhiteSpace(value?.Password), "password", "Şifre / servis anahtarı zorunlu.")
         .Must(value?.Environment is Production or Test, "environment", "Ortam 'Prod' veya 'Test' olmalı.");
        v.ThrowIfInvalid();

        return value! with { MerchantId = value.MerchantId.Trim(), UserName = value.UserName.Trim(), Password = value.Password.Trim() };
    }

    public override string ToString() => $"HepsiburadaCredentials {{ MerchantId = {MerchantId}, Environment = {Environment} }}";
}
