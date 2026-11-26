using System.Text.Json;

namespace PazaryeriHub.Application.Excel;

/// <summary>
/// Panel listelerinin genel Excel dışa aktarımı (ADR-060). Panel, ekrandaki filtreyle (sayfalanmış listelerde bütün sayfalar)
/// yetkili olduğu liste uç noktalarından çektiği satırları gönderir; sunucu yalnızca çalışma kitabına çevirir.
/// Veri rolün zaten gördüğü (maskelenmiş) veridir: bu uç nokta veritabanından hiçbir şey okumaz.
/// </summary>
/// <param name="FileName">İndirilecek dosyanın adı (uzantısız; güvenli hale getirilir, tarih eklenir).</param>
/// <param name="SheetName">Çalışma sayfası adı (Excel kuralları: en fazla 31 karakter, : \ / ? * [ ] yok).</param>
/// <param name="Rows">Her satır sütun sayısı kadar değer: metin, sayı, true/false veya null.</param>
public sealed record TableExportRequest(
    string? FileName, string? SheetName, IReadOnlyList<TableExportColumn>? Columns, IReadOnlyList<IReadOnlyList<JsonElement>>? Rows);

/// <param name="Kind">text (varsayılan) | number | money | date | datetime | bool. Tarihler UTC ISO-8601 gelir, Türkiye saatine çevrilir.</param>
public sealed record TableExportColumn(string? Header, string? Kind = null);

public static class TableExportKinds
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Money = "money";
    public const string Date = "date";
    public const string DateTime = "datetime";
    public const string Bool = "bool";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Text, Number, Money, Date, DateTime, Bool };
}
