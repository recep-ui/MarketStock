namespace PazaryeriHub.Application.DepotMigration;

/// <summary>Depo Excel'inin bir satırı (ADR-059): her satır bir ortak SKU olur.</summary>
/// <param name="Row">Depo Excel'indeki satır numarası (eşleştirme bu numarayla yapılır).</param>
/// <param name="Model">Model adı; aynı modeldeki satırlar aynı ürün olur.</param>
/// <param name="Variant">Varyant metni ("YELLOW/SARI"); SKU rengi "/" sonrası Türkçe kısımdır.</param>
/// <param name="Quantity">Serbest miktar: yeni SKU'nun fiziksel stoğu (sayım düzeltmesiyle).</param>
/// <param name="SkuCode">Ortak SKU kodu (onay dosyasında kullanıcı değiştirebilir).</param>
/// <param name="Barcode">Depo Excel'indeki ürün barkodu (ADR-066); varsa ortak SKU'nun barkodu olur.</param>
public sealed record DepotRow(int Row, string Model, string Variant, string? Kind, string? Size, int Quantity, string SkuCode, string? Barcode = null)
{
    /// <summary>"YELLOW/SARI" → "SARI"; "WHITE/BEYAZ (ofre)" → "BEYAZ"; "/" yoksa metnin tamamı.</summary>
    public string Color
    {
        get
        {
            var part = Variant.Contains('/') ? Variant[(Variant.IndexOf('/') + 1)..] : Variant;
            var paren = part.IndexOf('(');
            return (paren >= 0 ? part[..paren] : part).Trim();
        }
    }
}

/// <summary>Bir kanal ürününün depo satırına atanması: ya mevcut ilan (ListingId) ya da eşleşmeyen kanal ürünü (UnmatchedId).</summary>
public sealed record DepotAssignment(int DepotRow, int? ListingId, int? UnmatchedId);

/// <summary>Planlamada gereken mevcut durum (veritabanından okunur).</summary>
/// <param name="SkuIdsByCode">Mevcut SKU kodları → Id.</param>
/// <param name="ListingSku">İlan Id → bağlı olduğu SKU Id.</param>
/// <param name="ListingsBySku">SKU Id → o SKU'ya bağlı tüm ilan Id'leri.</param>
/// <param name="PendingUnmatched">Bekleyen (bağlanmamış) eşleşmeyen kayıt Id'leri.</param>
/// <param name="Balances">SKU Id → (fiziksel, rezerve) toplamı.</param>
public sealed record DepotState(
    IReadOnlyDictionary<string, int> SkuIdsByCode,
    IReadOnlyDictionary<int, int> ListingSku,
    IReadOnlyDictionary<int, IReadOnlyCollection<int>> ListingsBySku,
    IReadOnlySet<int> PendingUnmatched,
    IReadOnlyDictionary<int, (int Physical, int Reserved)> Balances)
{
    /// <summary>SKU Id → mevcut barkodu (yalnızca barkodu dolu SKU'lar).</summary>
    public IReadOnlyDictionary<int, string> SkuBarcodes { get; init; } = new Dictionary<int, string>();
}

public sealed record PlannedSku(DepotRow Row, int? ExistingSkuId);
/// <summary>Depo satırının barkodu ortak SKU'ya yazılır; <paramref name="ClearFromSkuId"/> doluysa barkod önce o (eski) SKU'dan kaldırılır.</summary>
public sealed record PlannedBarcode(DepotRow Row, string Barcode, int? ClearFromSkuId);
public sealed record PlannedMove(int ListingId, int FromSkuId, DepotRow Row);
public sealed record PlannedLink(int UnmatchedId, DepotRow Row);
public sealed record PlannedZero(int SkuId, int Physical);

/// <summary>Geçiş planı. <see cref="Errors"/> doluysa uygulanmaz.</summary>
public sealed record DepotMigrationPlan(
    IReadOnlyList<PlannedSku> Skus,
    IReadOnlyList<PlannedMove> Moves,
    IReadOnlyList<PlannedLink> Links,
    IReadOnlyList<PlannedZero> ZeroOldSkus,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Ortak SKU'lara yazılacak barkodlar (ADR-066).</summary>
    public IReadOnlyList<PlannedBarcode> Barcodes { get; init; } = [];

    public int NewSkuCount => Skus.Count(s => s.ExistingSkuId is null);
    public int NewStockTotal => Skus.Where(s => s.ExistingSkuId is null).Sum(s => s.Row.Quantity);
}

/// <summary>
/// Depo Excel'i → ortak SKU geçiş planı (ADR-059). SAF fonksiyon: veritabanına dokunmaz.
/// <list type="bullet">
/// <item>Her depo satırı bir SKU: kod zaten varsa mevcut SKU kullanılır ve stoğu DEĞİŞTİRİLMEZ (yeniden çalıştırma güvenli).</item>
/// <item>Atanan ilanlar yeni SKU'ya taşınır; zaten oradaysa atlanır. Bekleyen eşleşmeyen kanal ürünleri yeni SKU'ya bağlanır.</item>
/// <item>Bütün ilanları taşınan eski SKU'nun fiziksel stoğu 0'a sayılır (depo Excel'i esas); rezervi varsa sıfırlanmaz, uyarı verilir.</item>
/// </list>
/// </summary>
public static class DepotMigrationPlanner
{
    public static DepotMigrationPlan Plan(IReadOnlyList<DepotRow> rows, IReadOnlyList<DepotAssignment> assignments, DepotState state)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(state);
        List<string> errors = [], warnings = [];

        foreach (var dup in rows.GroupBy(r => r.Row).Where(g => g.Count() > 1))
        {
            errors.Add($"Depo satırı {dup.Key} birden fazla kez var.");
        }
        foreach (var r in rows.Where(r => string.IsNullOrWhiteSpace(r.SkuCode) || r.SkuCode.Length > 100))
        {
            errors.Add($"Depo satırı {r.Row}: ortak SKU kodu boş ya da 100 karakterden uzun.");
        }
        foreach (var dup in rows.Where(r => !string.IsNullOrWhiteSpace(r.SkuCode)).GroupBy(r => r.SkuCode.Trim(), StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"Ortak SKU kodu '{dup.Key}' birden fazla depo satırında: {string.Join(", ", dup.Select(r => r.Row))}.");
        }
        foreach (var r in rows.Where(r => r.Model.Trim().Length > Catalog.CatalogLimits.ProductName || r.Color.Length > Catalog.CatalogLimits.Color
                     || (r.Size?.Trim().Length ?? 0) > Catalog.CatalogLimits.Size || (r.Kind?.Trim().Length ?? 0) > Catalog.CatalogLimits.Category))
        {
            errors.Add($"Depo satırı {r.Row}: model, renk, beden veya ürün cinsi alan sınırını aşıyor "
                + $"(model {Catalog.CatalogLimits.ProductName}, renk {Catalog.CatalogLimits.Color}, beden {Catalog.CatalogLimits.Size} karakter).");
        }
        // Defter negatif stok tutmaz (Fiziksel >= 0): depo Excel'indeki negatif serbest miktar 0 sayılır, uyarı verilir
        foreach (var r in rows.Where(r => r.Quantity < 0))
        {
            warnings.Add($"Depo satırı {r.Row} ({r.Model} {r.Variant} {r.Size}): miktar {r.Quantity}; stok 0 sayılır.");
        }
        rows = rows.Select(r => r.Quantity < 0 ? r with { Quantity = 0 } : r).ToList();

        var byRow = rows.GroupBy(r => r.Row).ToDictionary(g => g.Key, g => g.First());
        var skus = rows.Select(r => new PlannedSku(r, state.SkuIdsByCode.TryGetValue(r.SkuCode.Trim(), out var id) ? id : null)).ToList();
        foreach (var s in skus.Where(s => s.ExistingSkuId is not null))
        {
            warnings.Add($"Depo satırı {s.Row.Row}: '{s.Row.SkuCode}' kodlu SKU zaten var; o SKU kullanılır, stoğu değiştirilmez.");
        }
        var existingTarget = skus.Where(s => s.ExistingSkuId is not null).ToDictionary(s => s.Row.Row, s => s.ExistingSkuId!.Value);

        List<PlannedMove> moves = [];
        List<PlannedLink> links = [];
        foreach (var dup in assignments.Where(a => a.ListingId is not null).GroupBy(a => a.ListingId!.Value).Where(g => g.Select(a => a.DepotRow).Distinct().Count() > 1))
        {
            errors.Add($"İlan {dup.Key} birden fazla depo satırına atanmış: {string.Join(", ", dup.Select(a => a.DepotRow).Distinct())}.");
        }
        foreach (var a in assignments.DistinctBy(a => (a.ListingId, a.UnmatchedId, a.DepotRow)))
        {
            if (!byRow.TryGetValue(a.DepotRow, out var row))
            {
                errors.Add($"Atama: depo satırı {a.DepotRow} depo listesinde yok.");
                continue;
            }
            if (a.ListingId is { } listingId)
            {
                if (!state.ListingSku.TryGetValue(listingId, out var from))
                {
                    errors.Add($"Atama: ilan {listingId} bulunamadı.");
                }
                else if (!(existingTarget.TryGetValue(a.DepotRow, out var target) && target == from))
                {
                    moves.Add(new PlannedMove(listingId, from, row));
                }
            }
            else if (a.UnmatchedId is { } unmatchedId)
            {
                if (state.PendingUnmatched.Contains(unmatchedId))
                {
                    links.Add(new PlannedLink(unmatchedId, row));
                }
                else
                {
                    warnings.Add($"Eşleşmeyen kayıt {unmatchedId} bekleyen durumda değil (bağlanmış ya da yok sayılmış); atlandı.");
                }
            }
        }

        // Bütün ilanları taşınan eski SKU'lar: stok 0'a sayılır (depo Excel'i esas)
        var movedBySku = moves.GroupBy(m => m.FromSkuId).ToDictionary(g => g.Key, g => g.Select(m => m.ListingId).ToHashSet());
        var targets = existingTarget.Values.ToHashSet();
        List<PlannedZero> zeros = [];
        foreach (var (skuId, moved) in movedBySku)
        {
            if (targets.Contains(skuId))
            {
                continue; // eski SKU aynı zamanda bir depo satırının hedefi
            }
            var all = state.ListingsBySku.TryGetValue(skuId, out var l) ? l : [];
            if (!all.All(moved.Contains))
            {
                continue; // bazı ilanları eski SKU'da kalıyor: stoğu korunur
            }
            var (physical, reserved) = state.Balances.TryGetValue(skuId, out var b) ? b : (0, 0);
            if (reserved > 0)
            {
                warnings.Add($"SKU {skuId}: {reserved} adet rezervi var (açık sipariş); stoğu sıfırlanmadı.");
            }
            else if (physical > 0)
            {
                zeros.Add(new PlannedZero(skuId, physical));
            }
        }

        var barcodes = PlanBarcodes(rows, skus, state, errors, warnings);
        return new DepotMigrationPlan(skus, moves, links, zeros, errors, warnings) { Barcodes = barcodes };
    }

    /// <summary>
    /// ADR-066: depo satırının barkodu ortak SKU'nun barkodu olur. Barkod tekildir: eski bir SKU'da duruyorsa oradan alınır.
    /// Aynı barkod birden fazla depo satırındaysa, ortak SKU'nun zaten başka barkodu varsa ya da barkod başka bir depo satırının
    /// SKU'sundaysa hiçbir şey değiştirilmez, uyarı verilir.
    /// </summary>
    private static List<PlannedBarcode> PlanBarcodes(IReadOnlyList<DepotRow> rows, List<PlannedSku> skus, DepotState state, List<string> errors, List<string> warnings)
    {
        var withBarcode = rows.Select(r => (Row: r, Barcode: r.Barcode?.Trim())).Where(x => !string.IsNullOrEmpty(x.Barcode)).ToList();
        foreach (var (row, barcode) in withBarcode.Where(x => x.Barcode!.Length > Catalog.CatalogLimits.Barcode))
        {
            errors.Add($"Depo satırı {row.Row}: barkod {Catalog.CatalogLimits.Barcode} karakterden uzun.");
        }
        var duplicates = withBarcode.GroupBy(x => x.Barcode!, StringComparer.Ordinal).Where(g => g.Count() > 1).ToList();
        foreach (var dup in duplicates)
        {
            warnings.Add($"Barkod {dup.Key} birden fazla depo satırında ({string.Join(", ", dup.Select(x => x.Row.Row))}); hiçbirine yazılmadı.");
        }
        var duplicateSet = duplicates.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);

        var existingByRow = skus.Where(s => s.ExistingSkuId is not null).ToDictionary(s => s.Row.Row, s => s.ExistingSkuId!.Value);
        var rowBySku = existingByRow.ToDictionary(x => x.Value, x => x.Key);
        var holderByBarcode = state.SkuBarcodes.GroupBy(x => x.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);

        List<PlannedBarcode> planned = [];
        foreach (var (row, barcode) in withBarcode.Where(x => x.Barcode!.Length <= Catalog.CatalogLimits.Barcode && !duplicateSet.Contains(x.Barcode!)))
        {
            int? target = existingByRow.TryGetValue(row.Row, out var t) ? t : null;
            if (target is { } tid && state.SkuBarcodes.TryGetValue(tid, out var current))
            {
                if (current != barcode)
                {
                    warnings.Add($"Depo satırı {row.Row}: SKU'nun barkodu {current}; depo barkodu {barcode} ile değiştirilmedi.");
                }
                continue;
            }
            int? clearFrom = null;
            if (holderByBarcode.TryGetValue(barcode!, out var holder) && holder != target)
            {
                if (rowBySku.TryGetValue(holder, out var otherRow))
                {
                    warnings.Add($"Depo satırı {row.Row}: barkod {barcode} depo satırı {otherRow}'ün SKU'sunda; alınmadı.");
                    continue;
                }
                clearFrom = holder;
            }
            planned.Add(new PlannedBarcode(row, barcode!, clearFrom));
        }
        return planned;
    }
}
