using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace PazaryeriHub.Infrastructure.Logging;

/// <summary>
/// Log olaylarındaki hassas verileri maskeler (KVKK ve gizli bilgi koruması).
/// <list type="bullet">
/// <item>Adı hassas bir parça içeren özellikler (password, token, apikey, telefon, e-posta…) tamamen "***" olur.</item>
/// <item>İç içe nesne, dizi ve sözlüklerde de aynı kural uygulanır.</item>
/// <item>Metin değerlerindeki "Password=...;" / "Bearer ..." gibi kalıplar maskelenir.</item>
/// </list>
/// Not: Mesaj şablonunun sabit metni maskelenemez; hassas veriyi her zaman özellik olarak loglayın, metne gömmeyin.
/// </summary>
public sealed partial class SensitiveDataMaskingEnricher : ILogEventEnricher
{
    public const string Mask = "***";

    private static readonly string[] SensitiveNameFragments =
    [
        "password", "passwd", "pwd", "secret", "token", "apikey", "api_key", "authorization",
        "credential", "connectionstring", "sifre", "parola", "kimlik",
        "phone", "telefon", "gsm", "email", "eposta", "e_posta", "tckn", "tcno", "iban", "cardnumber", "kartno"
    ];

    private static readonly ScalarValue MaskedValue = new(Mask);

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var (name, value) in logEvent.Properties.ToArray())
        {
            var masked = MaskValue(name, value);
            if (!ReferenceEquals(masked, value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, masked));
            }
        }
    }

    public static bool IsSensitiveName(string name)
    {
        var normalized = name.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return SensitiveNameFragments.Any(f => normalized.Contains(f, StringComparison.Ordinal));
    }

    public static string MaskText(string text)
    {
        var result = ConnectionStringSecretRegex().Replace(text, m => $"{m.Groups["key"].Value}={Mask}");
        return BearerTokenRegex().Replace(result, $"$1 {Mask}");
    }

    private static LogEventPropertyValue MaskValue(string? name, LogEventPropertyValue value)
    {
        if (name is not null && IsSensitiveName(name))
        {
            return MaskedValue;
        }

        switch (value)
        {
            case ScalarValue { Value: string text }:
                var maskedText = MaskText(text);
                return ReferenceEquals(maskedText, text) || maskedText == text ? value : new ScalarValue(maskedText);

            case StructureValue structure:
            {
                var changed = false;
                var props = new List<LogEventProperty>(structure.Properties.Count);
                foreach (var p in structure.Properties)
                {
                    var m = MaskValue(p.Name, p.Value);
                    changed |= !ReferenceEquals(m, p.Value);
                    props.Add(ReferenceEquals(m, p.Value) ? p : new LogEventProperty(p.Name, m));
                }
                return changed ? new StructureValue(props, structure.TypeTag) : value;
            }

            case SequenceValue sequence:
            {
                var changed = false;
                var items = new List<LogEventPropertyValue>(sequence.Elements.Count);
                foreach (var e in sequence.Elements)
                {
                    var m = MaskValue(null, e);
                    changed |= !ReferenceEquals(m, e);
                    items.Add(m);
                }
                return changed ? new SequenceValue(items) : value;
            }

            case DictionaryValue dictionary:
            {
                var changed = false;
                var entries = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(dictionary.Elements.Count);
                foreach (var (key, v) in dictionary.Elements)
                {
                    var m = MaskValue(key.Value?.ToString(), v);
                    changed |= !ReferenceEquals(m, v);
                    entries.Add(new(key, m));
                }
                return changed ? new DictionaryValue(entries) : value;
            }

            default:
                return value;
        }
    }

    [GeneratedRegex(@"(?<key>\b(?:password|pwd)\s*)=\s*(?:""[^""]*""|'[^']*'|[^;]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringSecretRegex();

    [GeneratedRegex(@"\b(Bearer|Basic)\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();
}
