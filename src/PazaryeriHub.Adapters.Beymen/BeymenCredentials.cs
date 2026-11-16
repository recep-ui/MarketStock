using System.Text.Json;
using PazaryeriHub.Application.Common;

namespace PazaryeriHub.Adapters.Beymen;

/// <summary>
/// Beymen Partner API bilgileri. Kaynak: https://partner-docs.beymen.com/en/#authentication —
/// <c>Authorization: Basic Base64(ApiKey:ApiPassword)</c>; değerler Partner Portal → Hesap Bilgileri → Entegrasyon Bilgileri.
/// </summary>
public sealed record BeymenCredentials(string ApiKey, string ApiPassword, string Environment)
{
    public const string Production = "Prod";
    public const string Stage = "Stage";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Kaynak: dokümandaki tüm uç noktalar (TEST / PROD).</summary>
    public string BaseUrl => Environment == Stage ? "https://stage-partner-api.beymen.com" : "https://partner-api.beymen.com";

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static BeymenCredentials Parse(string json)
    {
        BeymenCredentials? value = null;
        try
        {
            value = JsonSerializer.Deserialize<BeymenCredentials>(json, Json);
        }
        catch (JsonException)
        {
            // aşağıda doğrulama hatası
        }

        var v = new Validator();
        v.Must(!string.IsNullOrWhiteSpace(value?.ApiKey), "apiKey", "API Key zorunlu.")
         .Must(!string.IsNullOrWhiteSpace(value?.ApiPassword), "apiPassword", "API Password zorunlu.")
         .Must(value?.Environment is Production or Stage, "environment", "Ortam 'Prod' veya 'Stage' olmalı.");
        v.ThrowIfInvalid();

        return value! with { ApiKey = value.ApiKey.Trim(), ApiPassword = value.ApiPassword.Trim() };
    }

    public override string ToString() => $"BeymenCredentials {{ Environment = {Environment} }}";
}
