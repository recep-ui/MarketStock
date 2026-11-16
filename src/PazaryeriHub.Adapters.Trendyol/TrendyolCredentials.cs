using System.Text.Json;
using PazaryeriHub.Application.Common;

namespace PazaryeriHub.Adapters.Trendyol;

/// <summary>
/// Trendyol API kimlik bilgileri (satıcı paneli → Hesap Bilgilerim → Entegrasyon Bilgileri).
/// Kaynak: https://developers.trendyol.com/v2.0/docs/authorization — Basic Auth (kullanıcı = API Key, parola = API Secret);
/// prod ve stage bilgileri farklıdır.
/// </summary>
public sealed record TrendyolCredentials(long SellerId, string ApiKey, string ApiSecret, string Environment)
{
    public const string Production = "Prod";
    public const string Stage = "Stage";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Kaynak: https://developers.trendyol.com/v2.0/docs/3-prod-stage-environments-1
    /// Stage ortamı IP beyaz listesi ister (yoksa 503).
    /// </summary>
    public string BaseUrl => Environment == Stage ? "https://stageapigw.trendyol.com/integration" : "https://apigw.trendyol.com/integration";

    /// <summary>Kaynak: authorization sayfası — "{sellerId} - SelfIntegration" (kendi yazılımı olan satıcılar için). Yoksa 403.</summary>
    public string UserAgent => $"{SellerId} - SelfIntegration";

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Panelden gelen JSON'u doğrular ve normalize eder; hatalıysa <see cref="RequestValidationException"/>.</summary>
    public static TrendyolCredentials Parse(string json)
    {
        TrendyolCredentials? value = null;
        try
        {
            value = JsonSerializer.Deserialize<TrendyolCredentials>(json, Json);
        }
        catch (JsonException)
        {
            // aşağıda doğrulama hatası verilir
        }

        var v = new Validator();
        v.Must(value is { SellerId: > 0 }, "sellerId", "Satıcı (supplier) numarası pozitif bir sayı olmalı.")
         .Must(!string.IsNullOrWhiteSpace(value?.ApiKey), "apiKey", "API Key zorunlu.")
         .Must(!string.IsNullOrWhiteSpace(value?.ApiSecret), "apiSecret", "API Secret zorunlu.")
         .Must(value?.Environment is Production or Stage, "environment", "Ortam 'Prod' veya 'Stage' olmalı.");
        v.ThrowIfInvalid();

        return value! with { ApiKey = value.ApiKey.Trim(), ApiSecret = value.ApiSecret.Trim() };
    }

    /// <summary>Gizli bilgiler log/ToString çıktısına düşmesin.</summary>
    public override string ToString() => $"TrendyolCredentials {{ SellerId = {SellerId}, Environment = {Environment} }}";
}
