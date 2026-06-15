using Wms.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Wms.Data;

public class WmsDbContext(DbContextOptions<WmsDbContext> options) : DbContext(options)
{
    public DbSet<WmsUser> Users => Set<WmsUser>();
    public DbSet<WmsRole> Roles => Set<WmsRole>();
    public DbSet<WmsUserRole> UserRoles => Set<WmsUserRole>();
    public DbSet<WmsAuditLog> AuditLogs => Set<WmsAuditLog>();
    public DbSet<AppConfig> AppConfigs => Set<AppConfig>();
    public DbSet<WmsBoxSequence> BoxSequences => Set<WmsBoxSequence>();
    public DbSet<WmsContainerPhotoCheck> ContainerPhotoChecks => Set<WmsContainerPhotoCheck>();
    public DbSet<WmsWHMaster> WHMasters => Set<WmsWHMaster>();
    public DbSet<WmsOpenBox> OpenBoxes => Set<WmsOpenBox>();
    public DbSet<WmsOpenBoxItem> OpenBoxItems => Set<WmsOpenBoxItem>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<WmsUser>(e =>
        {
            e.ToTable("WmsUser");
            e.HasKey(x => x.Username);
            e.Property(x => x.Username).HasMaxLength(100);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.Country).HasMaxLength(20);
            e.Property(x => x.Warehouse).HasMaxLength(50);
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
            e.HasMany(x => x.UserRoles).WithOne(ur => ur.User!)
                .HasForeignKey(ur => ur.Username).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<WmsWHMaster>(e =>
        {
            e.ToTable("WmsWHMaster");
            e.HasKey(x => new { x.Country, x.Warehouse });
            e.Property(x => x.Country).HasMaxLength(20);
            e.Property(x => x.Warehouse).HasMaxLength(50);
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<WmsOpenBox>(e =>
        {
            e.ToTable("WmsOpenBox");
            e.HasKey(x => x.BoxNo);
            e.Property(x => x.BoxNo).HasColumnType("varchar(50)");
            e.Property(x => x.Contno).HasColumnType("varchar(50)");
            e.Property(x => x.UserId).HasMaxLength(100);
            e.Property(x => x.PalletType).HasMaxLength(50);
            e.Property(x => x.Division).HasMaxLength(50);
            e.Property(x => x.Season).HasMaxLength(50);
            e.Property(x => x.LPMDt).HasColumnType("date");
            e.Property(x => x.ToteID).HasMaxLength(50);
            e.Property(x => x.LogisticsBoxNo).HasMaxLength(100);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
            e.HasMany(x => x.Items).WithOne()
                .HasForeignKey(i => i.BoxNo).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ToteID).IsUnique();
        });

        mb.Entity<WmsOpenBoxItem>(e =>
        {
            e.ToTable("WmsOpenBoxItem");
            e.HasKey(x => x.Id);
            e.Property(x => x.BoxNo).HasColumnType("varchar(50)");
            e.Property(x => x.ItemCode).HasMaxLength(20);
            e.Property(x => x.Result).HasMaxLength(20);
            e.Property(x => x.Size).HasMaxLength(20);
            e.Property(x => x.Color).HasMaxLength(40);
            e.Property(x => x.Style).HasMaxLength(40);
            e.Property(x => x.GroupCode).HasMaxLength(20);
            e.Property(x => x.Season).HasMaxLength(50);
            e.Property(x => x.ScannedTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<WmsRole>(e =>
        {
            e.ToTable("WmsRole");
            e.HasKey(x => x.RoleCode);
            e.Property(x => x.RoleCode).HasMaxLength(20);
            e.Property(x => x.RoleName).HasMaxLength(100);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<WmsUserRole>(e =>
        {
            e.ToTable("WmsUserRole");
            e.HasKey(x => new { x.Username, x.RoleCode });
            e.Property(x => x.Username).HasMaxLength(100);
            e.Property(x => x.RoleCode).HasMaxLength(20);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleCode).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<WmsAuditLog>(e =>
        {
            e.ToTable("WmsAuditLog");
            e.HasKey(x => x.Id);
            e.Property(x => x.EntityName).HasMaxLength(100);
            e.Property(x => x.EntityKey).HasMaxLength(200);
            e.Property(x => x.Action).HasColumnType("char(1)");
            e.Property(x => x.ChangedBy).HasMaxLength(100);
            e.Property(x => x.ChangedTS).HasColumnType("datetime2(0)");
            e.Property(x => x.ClientIp).HasMaxLength(45);
            e.Property(x => x.Context).HasMaxLength(200);
            e.Property(x => x.ChangesJson).HasColumnType("nvarchar(max)");
        });

        mb.Entity<AppConfig>(e =>
        {
            e.ToTable("AppConfig");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasColumnName("Key").HasMaxLength(100);
            e.Property(x => x.Value).HasColumnName("Value").HasColumnType("nvarchar(max)");
            e.Property(x => x.UpdatedTS).HasColumnType("datetime2(0)");
            e.Property(x => x.UpdatedBy).HasMaxLength(100);
        });

        mb.Entity<WmsBoxSequence>(e =>
        {
            e.ToTable("WmsBoxSequence");
            e.HasKey(x => x.Contno);
            e.Property(x => x.Contno).HasColumnType("varchar(50)");
            e.Property(x => x.UpdatedTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<WmsContainerPhotoCheck>(e =>
        {
            e.ToTable("WmsContainerPhotoCheck");
            e.HasKey(x => x.Contno);
            e.Property(x => x.Contno).HasColumnType("varchar(50)");
            e.Property(x => x.CheckedTS).HasColumnType("datetime2(0)");
            e.Property(x => x.CheckedBy).HasMaxLength(100);
        });
    }
}
