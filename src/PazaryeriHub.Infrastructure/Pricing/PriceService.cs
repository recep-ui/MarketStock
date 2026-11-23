using System.Data;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PazaryeriHub.Application.Channels;
using PazaryeriHub.Application.Common;
using PazaryeriHub.Application.Pricing;
using PazaryeriHub.Application.Sync;
using PazaryeriHub.Domain.Pricing;
using PazaryeriHub.Infrastructure.Persistence;

namespace PazaryeriHub.Infrastructure.Pricing;

/// <summary>
/// Fiyat yönetimi (ADR-028): SKU fiyatı, kanal kuralı, ilan fiyatı, önizleme, onaylar, geçmiş ve eşik ayarı.
/// Her değişiklik PriceHistory'ye ve FiyatDegisti outbox olayına AYNI transaction'da yazılır; kanala gönderim yalnızca
/// FiyatGonder job'ından yapılır (bu servis kanala çağrı yapmaz).
/// </summary>
public sealed class PriceService(
    SqlConnectionFactory connectionFactory,
    PriceStore priceStore,
    IEnumerable<IChannelAdapter> adapters,
    ILogger<PriceService> logger) : IPriceService
{
    public const string SourcePanel = "PANEL";
    public const string SourceExcel = "EXCEL";

    // ------------------------------------------------------------------ SKU fiyatı

    public async Task<SkuPriceDto> GetSkuPriceAsync(int skuId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return await ReadSkuAsync(connection, null, skuId, ct) ?? throw new NotFoundException($"SKU bulunamadı (Id={skuId}).");
    }

    public async Task<SkuPriceDto> SetSkuPriceAsync(int skuId, SkuPriceInput input, string? user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = new Validator();
        ValidatePrices(v, input.ListPrice, input.SalePrice, "listPrice", "salePrice");
        v.ThrowIfInvalid();

        await using var connection = await OpenAsync(ct);
        _ = await ReadSkuAsync(connection, null, skuId, ct) ?? throw new NotFoundException($"SKU bulunamadı (Id={skuId}).");
        await ApplyAsync(connection, [new SkuPriceChange(skuId, Normalize(input.ListPrice), Normalize(input.SalePrice))], SourcePanel, user, ct);
        return (await ReadSkuAsync(connection, null, skuId, ct))!;
    }

    public async Task<SkuPriceImportResult> ApplySkuPricesAsync(IReadOnlyList<SkuPriceChange> changes, string source, string? user, bool validateOnly, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return new(0, 0);
        }
        await using var connection = await OpenAsync(ct);
        var current = (await connection.QueryAsync<(int Id, decimal? ListeFiyati, decimal? SatisFiyati)>(new CommandDefinition(
                "SELECT Id, ListeFiyati, SatisFiyati FROM dbo.Sku WHERE Id IN (SELECT CAST([value] AS INT) FROM OPENJSON(@ids))",
                new { ids = JsonSerializer.Serialize(changes.Select(c => c.SkuId).Distinct()) }, cancellationToken: ct)))
            .ToDictionary(r => r.Id);
        var changed = changes
            .Select(c => c with { ListPrice = Normalize(c.ListPrice), SalePrice = Normalize(c.SalePrice) })
            .Where(c => !current.TryGetValue(c.SkuId, out var cur) || cur.ListeFiyati != c.ListPrice || cur.SatisFiyati != c.SalePrice)
            .ToList();
        if (!validateOnly && changed.Count > 0)
        {
            await ApplyAsync(connection, changed, source, user, ct);
        }
        return new(changed.Count, changes.Count - changed.Count);
    }

    /// <summary>Doğrulanmış değişiklikleri tek transaction'da yazar: geçmiş + Sku + FiyatDegisti(SkuId).</summary>
    private static async Task ApplyAsync(SqlConnection connection, IReadOnlyList<SkuPriceChange> changes, string source, string? user, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(changes.Select(c => new { k = c.SkuId, l = c.ListPrice, s = c.SalePrice }));
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT dbo.PriceHistory (Tip, SkuId, EskiSatis, YeniSatis, EskiListe, YeniListe, Kaynak, Kullanici)
            SELECT 'SKU_FIYAT', s.Id, s.SatisFiyati, j.s, s.ListeFiyati, j.l, @source, @user
              FROM dbo.Sku AS s WITH (UPDLOCK)
              JOIN OPENJSON(@payload) WITH (k INT, l DECIMAL(18,2), s DECIMAL(18,2)) AS j ON j.k = s.Id;

            UPDATE s SET s.ListeFiyati = j.l, s.SatisFiyati = j.s
              FROM dbo.Sku AS s
              JOIN OPENJSON(@payload) WITH (k INT, l DECIMAL(18,2), s DECIMAL(18,2)) AS j ON j.k = s.Id;

            INSERT dbo.Outbox (OlayTipi, Payload)
            SELECT @eventType, CONCAT(N'{"SkuId":', j.k, N'}')
              FROM OPENJSON(@payload) WITH (k INT) AS j;
            """, new { payload, source, user, eventType = OutboxEvents.PriceChanged }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    // ------------------------------------------------------------------ Kanal kuralı

    public async Task<IReadOnlyList<ChannelPriceInfo>> GetChannelsAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<(int Id, string Kod, string? Ayarlar, string? Durum)>(new CommandDefinition("""
            SELECT c.Id, c.Kod, c.Ayarlar, cap.Durum FROM dbo.Channel AS c
              LEFT JOIN dbo.ChannelCapability AS cap ON cap.ChannelId = c.Id AND cap.Yetenek = 'FIYAT'
             ORDER BY c.Id
            """, cancellationToken: ct));
        return rows.Select(r => ToInfo(r.Id, r.Kod, r.Ayarlar, r.Durum)).ToList();
    }

    public async Task<ChannelPriceInfo> SetChannelRuleAsync(int channelId, ChannelPriceRuleDto rule, string? user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var v = new Validator();
        if (!PriceRoundingCodes.TryParse(rule.Rounding, out var rounding))
        {
            v.Add("rounding", $"Geçersiz yuvarlama; {string.Join(", ", PriceRoundingCodes.All)} olmalı.");
        }
        var parsed = new PriceRule(rule.Multiplier, rule.FixedAdd, rounding, rule.Floor);
        foreach (var error in parsed.Validate())
        {
            v.Add("rule", error);
        }
        v.Must(decimal.Round(rule.FixedAdd, 2) == rule.FixedAdd && (rule.Floor is null || decimal.Round(rule.Floor.Value, 2) == rule.Floor), "rule",
            "Tutarlar en fazla 2 ondalık olabilir.");
        v.ThrowIfInvalid();

        await using var connection = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var channel = await connection.QuerySingleOrDefaultAsync<(string Kod, string? Ayarlar)?>(new CommandDefinition(
            "SELECT Kod, Ayarlar FROM dbo.Channel WITH (UPDLOCK) WHERE Id = @channelId", new { channelId }, tx, cancellationToken: ct))
            ?? throw new NotFoundException($"Kanal bulunamadı (Id={channelId}).");
        var (old, _) = priceStore.ReadRule(channel.Ayarlar, channel.Kod);
        var json = PriceStore.RuleToJson(parsed);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.Channel SET Ayarlar = JSON_MODIFY(CASE WHEN ISJSON(Ayarlar) = 1 THEN Ayarlar ELSE N'{}' END, '$.fiyatKurali', JSON_QUERY(@json))
             WHERE Id = @channelId;
            INSERT dbo.PriceHistory (Tip, ChannelId, Detay, Kaynak, Kullanici)
            VALUES ('KANAL_KURALI', @channelId, @detail, 'PANEL', @user);
            INSERT dbo.Outbox (OlayTipi, Payload) VALUES (@eventType, CONCAT(N'{"ChannelId":', @channelId, N'}'));
            """, new
            {
                channelId, json, user, eventType = OutboxEvents.PriceChanged,
                detail = JsonSerializer.Serialize(new { eski = JsonDocument.Parse(PriceStore.RuleToJson(old)).RootElement, yeni = JsonDocument.Parse(json).RootElement })
            }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        logger.LogInformation("Kanal fiyat kuralı değişti: kanal {KanalId}, {Kural}, kullanıcı {Kullanici}", channelId, json, user);
        return (await GetChannelsAsync(ct)).Single(c => c.ChannelId == channelId);
    }

    // ------------------------------------------------------------------ İlan fiyatı

    public async Task<ListingPriceDto> SetListingOverrideAsync(int listingId, ListingPriceOverrideInput input, string? user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var v = new Validator();
        if (input.Price is { } p)
        {
            v.Must(p > 0m && p <= PriceRule.MaxAmount, "price", "Fiyat 0'dan büyük olmalı.");
            v.Must(decimal.Round(p, 2) == p, "price", "Fiyat en fazla 2 ondalık olabilir.");
        }
        v.ThrowIfInvalid();

        await using var connection = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var row = await connection.QuerySingleOrDefaultAsync<(int SkuId, int ChannelId, decimal? FiyatOverride)?>(new CommandDefinition(
            "SELECT SkuId, ChannelId, FiyatOverride FROM dbo.ChannelListing WITH (UPDLOCK) WHERE Id = @listingId", new { listingId }, tx, cancellationToken: ct))
            ?? throw new NotFoundException($"Eşleştirme bulunamadı (Id={listingId}).");
        if (row.FiyatOverride != input.Price)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE dbo.ChannelListing SET FiyatOverride = @price WHERE Id = @listingId;
                INSERT dbo.PriceHistory (Tip, SkuId, ListingId, ChannelId, EskiSatis, YeniSatis, Kaynak, Kullanici)
                VALUES ('ILAN_FIYATI', @skuId, @listingId, @channelId, @old, @price, 'PANEL', @user);
                INSERT dbo.Outbox (OlayTipi, Payload) VALUES (@eventType, CONCAT(N'{"ListingId":', @listingId, N'}'));
                """, new { listingId, price = input.Price, old = row.FiyatOverride, skuId = row.SkuId, channelId = row.ChannelId, user, eventType = OutboxEvents.PriceChanged },
                tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
        return await connection.QuerySingleAsync<ListingPriceDto>(new CommandDefinition("""
            SELECT Id AS ListingId, FiyatOverride AS Override, SonGonderilenSatisFiyati AS LastSentSale, SonGonderilenListeFiyati AS LastSentList,
                   SonFiyatSenkron AS LastPriceSyncUtc
              FROM dbo.ChannelListing WHERE Id = @listingId
            """, new { listingId }, cancellationToken: ct));
    }

    // ------------------------------------------------------------------ Önizleme

    public async Task<PricePreview> GetPreviewAsync(int channelId, bool onlyChanges, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var info = (await GetChannelsAsync(ct)).FirstOrDefault(c => c.ChannelId == channelId)
                   ?? throw new NotFoundException($"Kanal bulunamadı (Id={channelId}).");
        var listingIds = (await connection.QueryAsync<int>(new CommandDefinition(
            "SELECT Id FROM dbo.ChannelListing WHERE ChannelId = @channelId AND Durum = 'AKTIF'", new { channelId }, cancellationToken: ct))).ToList();
        var targets = await priceStore.ComputeAsync(connection, null, listingIds, ct);
        var threshold = await PriceStore.ThresholdAsync(connection, null, ct);

        var rows = targets.OrderBy(t => t.SkuCode, StringComparer.Ordinal).Select(t => BuildRow(t, threshold, info.CapabilityState)).ToList();

        var result = new PricePreview(channelId, info.ChannelCode, info.CapabilityState, info.CanEnable, info.BlockReason, threshold,
            rows.Count,
            rows.Count(r => r.Status == PricePreviewStatus.Change),
            rows.Count(r => r.Status == PricePreviewStatus.Change && r.LastSentSale is null),
            rows.Count(r => r.Status == PricePreviewStatus.NeedsApproval),
            rows.Count(r => r.Status == PricePreviewStatus.Same),
            rows.Count(r => r.Status is PricePreviewStatus.Blocked or PricePreviewStatus.Rejected),
            onlyChanges ? rows.Where(r => r.Status != PricePreviewStatus.Same).ToList() : rows);
        return result;
    }

    /// <summary>
    /// Önizleme satırı. Yetenek KAPALI iken "açılırsa ne olur": ACIK varsayılır (DENEME seçilirse hepsi tek tek onaya düşer).
    /// </summary>
    private static PricePreviewRow BuildRow(ListingPriceTarget t, decimal threshold, string capabilityState)
    {
        var assumedState = capabilityState == CapabilityStates.Closed ? CapabilityStates.Open : capabilityState;
        var decision = PriceSyncPolicy.Decide(t.Target, t.LastSale, t.LastList, threshold, assumedState, t.Approved, t.Rejected);
        var noCode = t is { ChannelProductCode: null, ChannelBarcode: null };
        var status = noCode ? PricePreviewStatus.Blocked : decision.Action switch
        {
            PriceSyncAction.Send => PricePreviewStatus.Change,
            PriceSyncAction.NoChange => PricePreviewStatus.Same,
            PriceSyncAction.NeedsApproval => PricePreviewStatus.NeedsApproval,
            PriceSyncAction.Rejected => PricePreviewStatus.Rejected,
            _ => PricePreviewStatus.Blocked
        };
        var problem = noCode ? "Eşleştirmede kanal ürün kodu veya barkodu yok." : decision.Action is PriceSyncAction.Send or PriceSyncAction.NoChange ? null : decision.Reason;
        var channel = ListingChannelData.Parse(t.ChannelData);
        return new PricePreviewRow(t.ListingId, t.SkuId, t.SkuCode, t.ProductName, t.ChannelProductCode, t.ChannelBarcode,
            t.LastSale, t.LastList, t.Target.Sendable ? t.Target.SalePrice : null, t.Target.Sendable ? t.Target.ListPrice : null,
            decision.ChangePercent, t.Target.FloorApplied, t.Target.FromOverride, status, problem, t.PendingApprovalId,
            channel.SalePrice, channel.ListPrice, channel.ProductUrl, channel.ImageUrl);
    }

    // ------------------------------------------------------------------ Ürün bazında kanal fiyatları (ADR-049)

    public async Task<PagedResult<SkuPriceOverview>> SearchSkuPricesAsync(string? search, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize ?? 25);
        var term = Text.Clean(search);
        var escaped = term is null ? null : PazaryeriHub.Application.Panel.SearchTerm.EscapeLike(term);
        var args = new
        {
            term,
            contains = escaped is null ? null : "%" + escaped + "%",
            upperContains = escaped is null ? null : "%" + escaped.ToUpperInvariant() + "%",
            skip = (p - 1) * size,
            take = size
        };
        // Yalnızca en az bir eşleştirmesi olan SKU'lar (fiyatı kanala gidebilecekler)
        const string where = """
            WHERE EXISTS (SELECT 1 FROM dbo.ChannelListing l WHERE l.SkuId = s.Id)
              AND (@term IS NULL OR s.Barkod = @term
                   OR s.SkuKodu LIKE @contains ESCAPE '\' OR UPPER(s.SkuKodu) LIKE @upperContains ESCAPE '\'
                   OR p.Ad LIKE @contains ESCAPE '\'
                   OR EXISTS (SELECT 1 FROM dbo.ChannelListing l WHERE l.SkuId = s.Id AND (l.KanalBarkod = @term OR l.KanalUrunKodu = @term)))
            """;

        await using var connection = await OpenAsync(ct);
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM dbo.Sku s JOIN dbo.Product p ON p.Id = s.ProductId {where}", args, cancellationToken: ct));
        var skus = (await connection.QueryAsync<SkuRow>(new CommandDefinition($"""
            SELECT s.Id AS SkuId, s.SkuKodu AS SkuCode, p.Ad AS ProductName, s.Barkod AS Barcode, s.ListeFiyati AS ListPrice, s.SatisFiyati AS SalePrice
              FROM dbo.Sku s JOIN dbo.Product p ON p.Id = s.ProductId
            {where}
             ORDER BY p.Ad, s.SkuKodu
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY
            """, args, cancellationToken: ct))).ToList();
        if (skus.Count == 0)
        {
            return new PagedResult<SkuPriceOverview>([], total, p, size);
        }

        var listings = (await connection.QueryAsync<(int Id, int SkuId, string Durum)>(new CommandDefinition("""
            SELECT l.Id, l.SkuId, l.Durum FROM dbo.ChannelListing l
             WHERE l.SkuId IN (SELECT CAST([value] AS INT) FROM OPENJSON(@ids))
            """, new { ids = JsonSerializer.Serialize(skus.Select(s => s.SkuId)) }, cancellationToken: ct))).ToList();
        var targets = (await priceStore.ComputeAsync(connection, null, listings.Select(l => l.Id).ToList(), ct)).ToDictionary(t => t.ListingId);
        var threshold = await PriceStore.ThresholdAsync(connection, null, ct);
        var statusById = listings.ToDictionary(l => l.Id, l => l.Durum);

        var items = skus.Select(s =>
        {
            var channels = targets.Values.Where(t => t.SkuId == s.SkuId)
                .OrderBy(t => t.ChannelId).ThenBy(t => t.ListingId)
                .Select(t =>
                {
                    var row = BuildRow(t, threshold, t.CapabilityState);
                    return new SkuChannelPrice(t.ListingId, t.ChannelId, t.ChannelCode, statusById[t.ListingId], t.CapabilityState, t.Override,
                        t.LastPriceSyncUtc is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null, row);
                }).ToList();
            return new SkuPriceOverview(s.SkuId, s.SkuCode, s.ProductName, s.Barcode, channels.Select(c => c.Preview.ImageUrl).FirstOrDefault(u => u is not null),
                s.ListPrice, s.SalePrice, channels);
        }).ToList();
        return new PagedResult<SkuPriceOverview>(items, total, p, size);
    }

    public const int MaxOverrideChanges = 1_000;

    public async Task<ListingOverridesResult> SetListingOverridesAsync(IReadOnlyList<ListingOverrideChange> changes, string? user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var list = changes.GroupBy(c => c.ListingId).Select(g => g.Last()).ToList();
        var v = new Validator()
            .Must(list.Count > 0, "changes", "En az bir değişiklik olmalı.")
            .Must(list.Count <= MaxOverrideChanges, "changes", $"Tek seferde en fazla {MaxOverrideChanges:N0} ilan fiyatı değiştirilebilir.");
        foreach (var c in list.Where(c => c.Price is not null))
        {
            var price = c.Price!.Value;
            v.Must(price > 0m && price <= PriceRule.MaxAmount, "changes", $"İlan {c.ListingId}: fiyat 0'dan büyük olmalı.");
            v.Must(decimal.Round(price, 2) == price, "changes", $"İlan {c.ListingId}: fiyat en fazla 2 ondalık olabilir.");
        }
        v.ThrowIfInvalid();

        await using var connection = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var current = (await connection.QueryAsync<(int Id, int SkuId, int ChannelId, decimal? FiyatOverride)>(new CommandDefinition("""
            SELECT Id, SkuId, ChannelId, FiyatOverride FROM dbo.ChannelListing WITH (UPDLOCK)
             WHERE Id IN (SELECT CAST([value] AS INT) FROM OPENJSON(@ids))
            """, new { ids = JsonSerializer.Serialize(list.Select(c => c.ListingId)) }, tx, cancellationToken: ct))).ToDictionary(r => r.Id);
        var missing = list.Where(c => !current.ContainsKey(c.ListingId)).Select(c => c.ListingId).ToList();
        if (missing.Count > 0)
        {
            throw new NotFoundException($"Eşleştirme bulunamadı: {string.Join(", ", missing)}.");
        }

        var changed = 0;
        foreach (var c in list)
        {
            var row = current[c.ListingId];
            if (row.FiyatOverride == c.Price)
            {
                continue;
            }
            // Tek ilan fiyatıyla aynı yol: ilan + PriceHistory + FiyatDegisti aynı transaction'da
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE dbo.ChannelListing SET FiyatOverride = @price WHERE Id = @listingId;
                INSERT dbo.PriceHistory (Tip, SkuId, ListingId, ChannelId, EskiSatis, YeniSatis, Kaynak, Kullanici)
                VALUES ('ILAN_FIYATI', @skuId, @listingId, @channelId, @old, @price, 'PANEL', @user);
                INSERT dbo.Outbox (OlayTipi, Payload) VALUES (@eventType, CONCAT(N'{"ListingId":', @listingId, N'}'));
                """, new { listingId = c.ListingId, price = c.Price, old = row.FiyatOverride, skuId = row.SkuId, channelId = row.ChannelId, user,
                    eventType = OutboxEvents.PriceChanged }, tx, cancellationToken: ct));
            changed++;
        }
        await tx.CommitAsync(ct);
        logger.LogInformation("İlan fiyatları toplu değişti: {Adet} ilan, kullanıcı {Kullanici}", changed, user);
        return new ListingOverridesResult(changed, list.Count - changed);
    }

    private sealed class SkuRow
    {
        public int SkuId { get; init; }
        public string SkuCode { get; init; } = "";
        public string ProductName { get; init; } = "";
        public string? Barcode { get; init; }
        public decimal? ListPrice { get; init; }
        public decimal? SalePrice { get; init; }
    }

    // ------------------------------------------------------------------ Onaylar

    private const string ApprovalSelect = """
        SELECT a.Id, a.ListingId, l.ChannelId, c.Kod AS ChannelCode, s.SkuKodu AS SkuCode, p.Ad AS ProductName,
               a.EskiSatisFiyati AS OldSale, a.YeniSatisFiyati AS NewSale, a.YeniListeFiyati AS NewList, a.DegisimYuzde AS ChangePercent,
               a.Neden AS Reason, a.Durum AS State, a.Olusma AS CreatedUtc, a.KararVeren AS DecidedBy, a.KararTarihi AS DecidedUtc
          FROM dbo.PriceApproval AS a
          JOIN dbo.ChannelListing AS l ON l.Id = a.ListingId
          JOIN dbo.Channel AS c ON c.Id = l.ChannelId
          JOIN dbo.Sku AS s ON s.Id = l.SkuId
          JOIN dbo.Product AS p ON p.Id = s.ProductId
        """;

    public async Task<PagedResult<PriceApprovalDto>> SearchApprovalsAsync(string? state, int? channelId, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        await using var connection = await OpenAsync(ct);
        var args = new { state = Text.Clean(state)?.ToUpperInvariant(), channelId, skip = (p - 1) * size, size };
        const string where = "WHERE (@state IS NULL OR a.Durum = @state) AND (@channelId IS NULL OR l.ChannelId = @channelId)";
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM dbo.PriceApproval a JOIN dbo.ChannelListing l ON l.Id = a.ListingId {where}", args, cancellationToken: ct));
        var rows = await connection.QueryAsync<ApprovalRow>(new CommandDefinition(
            $"{ApprovalSelect} {where} ORDER BY a.Id DESC OFFSET @skip ROWS FETCH NEXT @size ROWS ONLY", args, cancellationToken: ct));
        return new PagedResult<PriceApprovalDto>(rows.Select(r => r.ToDto()).ToList(), total, p, size);
    }

    public Task<PriceApprovalDto> ApproveAsync(long id, string? user, CancellationToken ct) => DecideAsync(id, PriceApprovalStates.Approved, user, ct);

    public Task<PriceApprovalDto> RejectAsync(long id, string? user, CancellationToken ct) => DecideAsync(id, PriceApprovalStates.Rejected, user, ct);

    /// <summary>Karar + geçmiş + FiyatDegisti(ListingId) aynı transaction'da; onaylanan değer OutboxIsle'da kuyruğa girer.</summary>
    private async Task<PriceApprovalDto> DecideAsync(long id, string newState, string? user, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var row = await connection.QuerySingleOrDefaultAsync<(int ListingId, string Durum)?>(new CommandDefinition(
            "SELECT ListingId, Durum FROM dbo.PriceApproval WITH (UPDLOCK) WHERE Id = @id", new { id }, tx, cancellationToken: ct))
            ?? throw new NotFoundException($"Fiyat onayı bulunamadı (Id={id}).");
        if (row.Durum != PriceApprovalStates.Pending)
        {
            throw new ConflictException($"Bu fiyat onayı artık beklemiyor (durum: {row.Durum}).");
        }
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.PriceApproval SET Durum = @newState, KararVeren = @user, KararTarihi = SYSUTCDATETIME(), Guncelleme = SYSUTCDATETIME()
             WHERE Id = @id;
            INSERT dbo.PriceHistory (Tip, SkuId, ListingId, ChannelId, EskiSatis, YeniSatis, YeniListe, Detay, Kaynak, Kullanici)
            SELECT 'ONAY', l.SkuId, l.Id, l.ChannelId, a.EskiSatisFiyati, a.YeniSatisFiyati, a.YeniListeFiyati,
                   CONCAT(N'{"karar":"', @newState, N'","onayId":', a.Id, N'}'), 'PANEL', @user
              FROM dbo.PriceApproval AS a JOIN dbo.ChannelListing AS l ON l.Id = a.ListingId
             WHERE a.Id = @id;
            INSERT dbo.Outbox (OlayTipi, Payload) VALUES (@eventType, CONCAT(N'{"ListingId":', @listingId, N'}'));
            """, new { id, newState, user, listingId = row.ListingId, eventType = OutboxEvents.PriceChanged }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        logger.LogInformation("Fiyat onayı {OnayId}: {Karar}, kullanıcı {Kullanici}", id, newState, user);
        return (await connection.QuerySingleAsync<ApprovalRow>(new CommandDefinition($"{ApprovalSelect} WHERE a.Id = @id", new { id }, cancellationToken: ct))).ToDto();
    }

    // ------------------------------------------------------------------ Geçmiş ve ayar

    public async Task<PagedResult<PriceHistoryDto>> GetHistoryAsync(int? skuId, int? listingId, int? channelId, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        await using var connection = await OpenAsync(ct);
        var args = new { skuId, listingId, channelId, skip = (p - 1) * size, size };
        const string where = """
            WHERE (@skuId IS NULL OR h.SkuId = @skuId) AND (@listingId IS NULL OR h.ListingId = @listingId)
              AND (@channelId IS NULL OR h.ChannelId = @channelId)
            """;
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition($"SELECT COUNT(*) FROM dbo.PriceHistory h {where}", args, cancellationToken: ct));
        var rows = await connection.QueryAsync<HistoryRow>(new CommandDefinition($"""
            SELECT h.Id, h.Tip, h.SkuId, s.SkuKodu, h.ListingId, c.Kod AS ChannelCode, h.EskiSatis, h.YeniSatis, h.EskiListe, h.YeniListe,
                   h.Detay, h.Kaynak, h.Kullanici, h.Tarih
              FROM dbo.PriceHistory h
              LEFT JOIN dbo.Sku s ON s.Id = h.SkuId
              LEFT JOIN dbo.Channel c ON c.Id = h.ChannelId
             {where}
             ORDER BY h.Id DESC OFFSET @skip ROWS FETCH NEXT @size ROWS ONLY
            """, args, cancellationToken: ct));
        return new PagedResult<PriceHistoryDto>(rows.Select(r => new PriceHistoryDto(r.Id, r.Tip, r.SkuId, r.SkuKodu, r.ListingId, r.ChannelCode,
            r.EskiSatis, r.YeniSatis, r.EskiListe, r.YeniListe, r.Detay, r.Kaynak, r.Kullanici, DateTime.SpecifyKind(r.Tarih, DateTimeKind.Utc))).ToList(), total, p, size);
    }

    public async Task<PriceSettingsDto> GetSettingsAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return new PriceSettingsDto(await PriceStore.ThresholdAsync(connection, null, ct));
    }

    /// <summary>Eşik değişince FIYAT yeteneği açık kanallar yeniden değerlendirilir (bekleyen onaylar kapanabilir/açılabilir).</summary>
    public async Task<PriceSettingsDto> SetSettingsAsync(PriceSettingsDto settings, string? user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        new Validator().Must(settings.ChangeThresholdPercent is >= 0m and <= 1000m, "changeThresholdPercent", "Eşik 0 ile 1000 arasında olmalı.")
            .ThrowIfInvalid();
        await using var connection = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var value = settings.ChangeThresholdPercent.ToString("0.##", CultureInfo.InvariantCulture);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.AppSetting SET Deger = @value, Guncelleme = SYSUTCDATETIME() WHERE Anahtar = @key;
            IF @@ROWCOUNT = 0 INSERT dbo.AppSetting (Anahtar, Deger) VALUES (@key, @value);
            INSERT dbo.Outbox (OlayTipi, Payload)
            SELECT @eventType, CONCAT(N'{"ChannelId":', ChannelId, N'}') FROM dbo.ChannelCapability WHERE Yetenek = 'FIYAT' AND Durum <> 'KAPALI';
            """, new { key = PriceSettingKeys.ChangeThresholdPercent, value, eventType = OutboxEvents.PriceChanged }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        logger.LogInformation("Fiyat değişim eşiği %{Esik} yapıldı, kullanıcı {Kullanici}", value, user);
        return new PriceSettingsDto(settings.ChangeThresholdPercent);
    }

    // ------------------------------------------------------------------ Yardımcılar

    /// <summary>Panel ve Excel ortak doğrulaması.</summary>
    public static void ValidatePrices(Validator v, decimal? list, decimal? sale, string listField, string saleField)
    {
        ArgumentNullException.ThrowIfNull(v);
        if (sale is null)
        {
            v.Must(list is null, saleField, "Liste fiyatı varken satış fiyatı boş olamaz.");
            return;
        }
        v.Must(sale > 0m && sale <= PriceRule.MaxAmount, saleField, "Satış fiyatı 0'dan büyük olmalı.");
        v.Must(decimal.Round(sale.Value, 2) == sale, saleField, "Satış fiyatı en fazla 2 ondalık olabilir.");
        if (list is { } l)
        {
            v.Must(l > 0m && l <= PriceRule.MaxAmount, listField, "Liste fiyatı 0'dan büyük olmalı.");
            v.Must(decimal.Round(l, 2) == l, listField, "Liste fiyatı en fazla 2 ondalık olabilir.");
            v.Must(l >= sale, listField, "Liste fiyatı satış fiyatından düşük olamaz.");
        }
    }

    private ChannelPriceInfo ToInfo(int id, string code, string? settings, string? capability)
    {
        var (rule, _) = priceStore.ReadRule(settings, code);
        var adapterRule = adapters.FirstOrDefault(a => a.ChannelCode == code)?.DescribePriceUpdate()
                          ?? PriceUpdateRule.NotSupported("Kanal adaptörü kayıtlı değil.");
        return new ChannelPriceInfo(id, code, ChannelPriceRuleDto.From(rule), capability ?? ChannelCapabilities.Default(ChannelCapabilities.Price),
            adapterRule.Supported, adapterRule.VatIncluded, adapterRule.SendsListPrice, adapterRule.SamePriceForProductVariants, adapterRule.Note,
            adapterRule.Source, adapterRule.CanEnable, adapterRule.BlockReason);
    }

    private static decimal? Normalize(decimal? value) => value is { } v ? decimal.Round(v, 2, MidpointRounding.AwayFromZero) : null;

    private static async Task<SkuPriceDto?> ReadSkuAsync(SqlConnection connection, SqlTransaction? tx, int skuId, CancellationToken ct) =>
        await connection.QuerySingleOrDefaultAsync<SkuPriceDto>(new CommandDefinition("""
            SELECT s.Id AS SkuId, s.SkuKodu AS SkuCode, p.Ad AS ProductName, s.ListeFiyati AS ListPrice, s.SatisFiyati AS SalePrice
              FROM dbo.Sku AS s JOIN dbo.Product AS p ON p.Id = s.ProductId WHERE s.Id = @skuId
            """, new { skuId }, tx, cancellationToken: ct));

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(ct);
        return connection;
    }

    private sealed class ApprovalRow
    {
        public long Id { get; init; }
        public int ListingId { get; init; }
        public int ChannelId { get; init; }
        public string ChannelCode { get; init; } = "";
        public string SkuCode { get; init; } = "";
        public string ProductName { get; init; } = "";
        public decimal? OldSale { get; init; }
        public decimal NewSale { get; init; }
        public decimal NewList { get; init; }
        public decimal? ChangePercent { get; init; }
        public string Reason { get; init; } = "";
        public string State { get; init; } = "";
        public DateTime CreatedUtc { get; init; }
        public string? DecidedBy { get; init; }
        public DateTime? DecidedUtc { get; init; }

        public PriceApprovalDto ToDto() => new(Id, ListingId, ChannelId, ChannelCode, SkuCode, ProductName, OldSale, NewSale, NewList, ChangePercent,
            Reason, State, DateTime.SpecifyKind(CreatedUtc, DateTimeKind.Utc), DecidedBy,
            DecidedUtc is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null);
    }

    private sealed class HistoryRow
    {
        public long Id { get; init; }
        public string Tip { get; init; } = "";
        public int? SkuId { get; init; }
        public string? SkuKodu { get; init; }
        public int? ListingId { get; init; }
        public string? ChannelCode { get; init; }
        public decimal? EskiSatis { get; init; }
        public decimal? YeniSatis { get; init; }
        public decimal? EskiListe { get; init; }
        public decimal? YeniListe { get; init; }
        public string? Detay { get; init; }
        public string Kaynak { get; init; } = "";
        public string? Kullanici { get; init; }
        public DateTime Tarih { get; init; }
    }
}
