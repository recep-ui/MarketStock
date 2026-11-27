using Microsoft.EntityFrameworkCore;

namespace PazaryeriHub.Infrastructure.Persistence;

// EF Core yalnızca genel CRUD içindir (CLAUDE.md §2). Şema DbUp script'leriyle yönetilir; EF migration KULLANILMAZ.
// Stok bakiyesi/hareketleri EF ile YAZILMAZ: stok yazımları saklı yordamlar (SqlStockLedger) üzerinden yapılır.
// Kolon adları Türkçe (veritabanı), özellik adları İngilizce (kod).

public sealed class ProductEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Brand { get; set; }
    public string? Category { get; set; }
    /// <summary>Kanalların "Model Kodu" (ürün grubu). Eşleştirmenin model + renk + beden adımında kullanılır.</summary>
    public string? ModelCode { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public List<SkuEntity> Skus { get; set; } = [];
}

public sealed class SkuEntity
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public ProductEntity Product { get; set; } = null!;
    public string SkuCode { get; set; } = "";
    public string? Barcode { get; set; }
    public string? Color { get; set; }
    public string? Size { get; set; }
}

public sealed class WarehouseEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class ChannelEntity
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal SharePercent { get; set; }
    public bool Active { get; set; }
    public string? SettingsJson { get; set; }
    public byte[]? EncryptedCredentials { get; set; }
}

public sealed class ChannelListingEntity
{
    public int Id { get; set; }
    public int SkuId { get; set; }
    public SkuEntity Sku { get; set; } = null!;
    public int ChannelId { get; set; }
    public ChannelEntity Channel { get; set; } = null!;
    public string? ChannelProductCode { get; set; }
    public string? ChannelBarcode { get; set; }
    public int? LastSentStock { get; set; }
    public DateTime? LastSyncUtc { get; set; }
    public string Status { get; set; } = "AKTIF";
    public string? ChannelDataJson { get; set; }
}

/// <summary>Kanalda olup hiçbir SKU'ya bağlanamayan ürün (kanal kataloğu içe alma).</summary>
public sealed class UnmatchedChannelItemEntity
{
    public int Id { get; set; }
    public int ChannelId { get; set; }
    public string Source { get; set; } = "";
    public string ChannelKey { get; set; } = "";
    public string? ChannelProductCode { get; set; }
    public string? ChannelBarcode { get; set; }
    public string? ChannelStockCode { get; set; }
    public string? ModelCode { get; set; }
    public string? Color { get; set; }
    public string? Size { get; set; }
    public string? ProductName { get; set; }
    public string? Brand { get; set; }
    public string? Category { get; set; }
    public string? ChannelStatus { get; set; }

    /// <summary>Kanal ürününün küçük resmi (yalnızca https; ADR-042).</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Kanalın çekimde bildirdiği satış/liste fiyatı, ürün linki ve varyant; bağlanınca ilana aynen geçer (ADR-063).</summary>
    public decimal? SalePrice { get; set; }
    public decimal? ListPrice { get; set; }
    public string? ProductUrl { get; set; }
    public string? VariantAttributes { get; set; }
    public string Reason { get; set; } = "";
    public string? ReasonDetail { get; set; }
    public int? SuggestedSkuId { get; set; }
    public decimal? SuggestionScore { get; set; }
    public string Status { get; set; } = "BEKLIYOR";
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
}

/// <summary>Salt okunur: stok bakiyesi (yazımlar saklı yordamlarla).</summary>
public sealed class StockBalanceView
{
    public int SkuId { get; set; }
    public int WarehouseId { get; set; }
    public int Physical { get; set; }
    public int Reserved { get; set; }
    public int SafetyStock { get; set; }
    public int Available { get; set; }
}

public sealed class PazaryeriHubDbContext(DbContextOptions<PazaryeriHubDbContext> options) : DbContext(options)
{
    public DbSet<ProductEntity> Products => Set<ProductEntity>();
    public DbSet<SkuEntity> Skus => Set<SkuEntity>();
    public DbSet<WarehouseEntity> Warehouses => Set<WarehouseEntity>();
    public DbSet<ChannelEntity> Channels => Set<ChannelEntity>();
    public DbSet<ChannelListingEntity> Listings => Set<ChannelListingEntity>();
    public DbSet<UnmatchedChannelItemEntity> UnmatchedChannelItems => Set<UnmatchedChannelItemEntity>();
    public IQueryable<StockBalanceView> StockBalances => Set<StockBalanceView>().AsNoTracking();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("dbo");

        modelBuilder.Entity<ProductEntity>(e =>
        {
            e.ToTable("Product");
            e.Property(x => x.Name).HasColumnName("Ad").HasMaxLength(300);
            e.Property(x => x.Brand).HasColumnName("Marka").HasMaxLength(100);
            e.Property(x => x.Category).HasColumnName("Kategori").HasMaxLength(200);
            e.Property(x => x.ModelCode).HasColumnName("ModelKodu").HasMaxLength(100);
            e.Property(x => x.CreatedAtUtc).HasColumnName("OlusturmaTarihi").HasColumnType("datetime2(3)")
                .HasDefaultValueSql("SYSUTCDATETIME()").ValueGeneratedOnAdd();
            e.HasMany(x => x.Skus).WithOne(s => s.Product).HasForeignKey(s => s.ProductId);
        });

        modelBuilder.Entity<SkuEntity>(e =>
        {
            e.ToTable("Sku");
            e.Property(x => x.SkuCode).HasColumnName("SkuKodu").HasMaxLength(100);
            e.Property(x => x.Barcode).HasColumnName("Barkod").HasMaxLength(50);
            e.Property(x => x.Color).HasColumnName("Renk").HasMaxLength(50);
            e.Property(x => x.Size).HasColumnName("Beden").HasMaxLength(30);
        });

        modelBuilder.Entity<WarehouseEntity>(e =>
        {
            e.ToTable("Warehouse");
            e.Property(x => x.Name).HasColumnName("Ad").HasMaxLength(100);
        });

        modelBuilder.Entity<ChannelEntity>(e =>
        {
            e.ToTable("Channel");
            e.Property(x => x.Code).HasColumnName("Kod").HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.Name).HasColumnName("Ad").HasMaxLength(100);
            e.Property(x => x.SharePercent).HasColumnName("PayYuzde").HasPrecision(5, 2);
            e.Property(x => x.Active).HasColumnName("Aktif");
            e.Property(x => x.SettingsJson).HasColumnName("Ayarlar");
            e.Property(x => x.EncryptedCredentials).HasColumnName("KimlikSifreli");
        });

        modelBuilder.Entity<ChannelListingEntity>(e =>
        {
            e.ToTable("ChannelListing");
            e.Property(x => x.ChannelProductCode).HasColumnName("KanalUrunKodu").HasMaxLength(100);
            e.Property(x => x.ChannelBarcode).HasColumnName("KanalBarkod").HasMaxLength(50);
            e.Property(x => x.LastSentStock).HasColumnName("SonGonderilenStok");
            e.Property(x => x.LastSyncUtc).HasColumnName("SonSenkron").HasColumnType("datetime2(3)");
            e.Property(x => x.Status).HasColumnName("Durum").HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.ChannelDataJson).HasColumnName("KanalVerisi");
            e.HasOne(x => x.Sku).WithMany().HasForeignKey(x => x.SkuId);
            e.HasOne(x => x.Channel).WithMany().HasForeignKey(x => x.ChannelId);
        });

        modelBuilder.Entity<UnmatchedChannelItemEntity>(e =>
        {
            e.ToTable("UnmatchedChannelItem");
            e.Property(x => x.Source).HasColumnName("Kaynak").HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.ChannelKey).HasColumnName("KanalAnahtar").HasMaxLength(100);
            e.Property(x => x.ChannelProductCode).HasColumnName("KanalUrunKodu").HasMaxLength(100);
            e.Property(x => x.ChannelBarcode).HasColumnName("KanalBarkod").HasMaxLength(50);
            e.Property(x => x.ChannelStockCode).HasColumnName("KanalStokKodu").HasMaxLength(100);
            e.Property(x => x.ModelCode).HasColumnName("ModelKodu").HasMaxLength(100);
            e.Property(x => x.Color).HasColumnName("Renk").HasMaxLength(50);
            e.Property(x => x.Size).HasColumnName("Beden").HasMaxLength(30);
            e.Property(x => x.ProductName).HasColumnName("UrunAdi").HasMaxLength(300);
            e.Property(x => x.Brand).HasColumnName("Marka").HasMaxLength(100);
            e.Property(x => x.Category).HasColumnName("Kategori").HasMaxLength(200);
            e.Property(x => x.ChannelStatus).HasColumnName("KanalDurumu").HasMaxLength(100);
            e.Property(x => x.ImageUrl).HasColumnName("GorselUrl").HasMaxLength(1000);
            e.Property(x => x.SalePrice).HasColumnName("KanalSatisFiyati").HasPrecision(18, 2);
            e.Property(x => x.ListPrice).HasColumnName("KanalListeFiyati").HasPrecision(18, 2);
            e.Property(x => x.ProductUrl).HasColumnName("UrunLinki").HasMaxLength(1000);
            e.Property(x => x.VariantAttributes).HasColumnName("Varyant").HasMaxLength(300);
            e.Property(x => x.Reason).HasColumnName("Neden").HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.ReasonDetail).HasColumnName("NedenAciklama").HasMaxLength(400);
            e.Property(x => x.SuggestedSkuId).HasColumnName("OnerilenSkuId");
            e.Property(x => x.SuggestionScore).HasColumnName("OneriSkoru").HasPrecision(5, 2);
            e.Property(x => x.Status).HasColumnName("Durum").HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.FirstSeenUtc).HasColumnName("IlkGorulme").HasColumnType("datetime2(3)");
            e.Property(x => x.LastSeenUtc).HasColumnName("SonGorulme").HasColumnType("datetime2(3)");
            e.Property(x => x.ResolvedBy).HasColumnName("Cozen").HasMaxLength(100);
            e.Property(x => x.ResolvedAtUtc).HasColumnName("CozulmeTarihi").HasColumnType("datetime2(3)");
        });

        modelBuilder.Entity<StockBalanceView>(e =>
        {
            e.ToTable("StockBalance", t => t.ExcludeFromMigrations());
            e.HasKey(x => new { x.SkuId, x.WarehouseId });
            e.Property(x => x.Physical).HasColumnName("Fiziksel");
            e.Property(x => x.Reserved).HasColumnName("Rezerve");
            e.Property(x => x.SafetyStock).HasColumnName("GuvenlikStogu");
            e.Property(x => x.Available).HasColumnName("Satilabilir");
        });
    }
}
