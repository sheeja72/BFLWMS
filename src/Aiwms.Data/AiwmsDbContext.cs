using Aiwms.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiwms.Data;

public class AiwmsDbContext(DbContextOptions<AiwmsDbContext> options) : DbContext(options)
{
    public DbSet<AiwmsUser> Users => Set<AiwmsUser>();
    public DbSet<AiwmsRole> Roles => Set<AiwmsRole>();
    public DbSet<AiwmsUserRole> UserRoles => Set<AiwmsUserRole>();
    public DbSet<AiwmsAuditLog> AuditLogs => Set<AiwmsAuditLog>();
    public DbSet<AppConfig> AppConfigs => Set<AppConfig>();
    public DbSet<AiwmsBoxSequence> BoxSequences => Set<AiwmsBoxSequence>();
    public DbSet<AiwmsContainerPhotoCheck> ContainerPhotoChecks => Set<AiwmsContainerPhotoCheck>();
    public DbSet<AiwmsWHMaster> WHMasters => Set<AiwmsWHMaster>();
    public DbSet<AiwmsOpenBox> OpenBoxes => Set<AiwmsOpenBox>();
    public DbSet<AiwmsOpenBoxItem> OpenBoxItems => Set<AiwmsOpenBoxItem>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<AiwmsUser>(e =>
        {
            e.ToTable("AiwmsUser");
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

        mb.Entity<AiwmsWHMaster>(e =>
        {
            e.ToTable("AiwmsWHMaster");
            e.HasKey(x => new { x.Country, x.Warehouse });
            e.Property(x => x.Country).HasMaxLength(20);
            e.Property(x => x.Warehouse).HasMaxLength(50);
            e.Property(x => x.CreatedBy).HasMaxLength(100);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<AiwmsOpenBox>(e =>
        {
            e.ToTable("AiwmsOpenBox");
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

        mb.Entity<AiwmsOpenBoxItem>(e =>
        {
            e.ToTable("AiwmsOpenBoxItem");
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

        mb.Entity<AiwmsRole>(e =>
        {
            e.ToTable("AiwmsRole");
            e.HasKey(x => x.RoleCode);
            e.Property(x => x.RoleCode).HasMaxLength(20);
            e.Property(x => x.RoleName).HasMaxLength(100);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<AiwmsUserRole>(e =>
        {
            e.ToTable("AiwmsUserRole");
            e.HasKey(x => new { x.Username, x.RoleCode });
            e.Property(x => x.Username).HasMaxLength(100);
            e.Property(x => x.RoleCode).HasMaxLength(20);
            e.Property(x => x.CreateTS).HasColumnType("datetime2(0)");
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleCode).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<AiwmsAuditLog>(e =>
        {
            e.ToTable("AiwmsAuditLog");
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

        mb.Entity<AiwmsBoxSequence>(e =>
        {
            e.ToTable("AiwmsBoxSequence");
            e.HasKey(x => x.Contno);
            e.Property(x => x.Contno).HasColumnType("varchar(50)");
            e.Property(x => x.UpdatedTS).HasColumnType("datetime2(0)");
        });

        mb.Entity<AiwmsContainerPhotoCheck>(e =>
        {
            e.ToTable("AiwmsContainerPhotoCheck");
            e.HasKey(x => x.Contno);
            e.Property(x => x.Contno).HasColumnType("varchar(50)");
            e.Property(x => x.CheckedTS).HasColumnType("datetime2(0)");
            e.Property(x => x.CheckedBy).HasMaxLength(100);
        });
    }
}
