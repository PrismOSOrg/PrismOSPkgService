using Microsoft.EntityFrameworkCore;

namespace PrismOS.Infrastructure.Persistence;

public sealed class RepositoryDbContext(DbContextOptions<RepositoryDbContext> options) : DbContext(options)
{
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<SessionEntity> Sessions => Set<SessionEntity>();
    public DbSet<FavoriteEntity> Favorites => Set<FavoriteEntity>();
    public DbSet<DownloadStatEntity> DownloadStats => Set<DownloadStatEntity>();
    public DbSet<UserDownloadStatEntity> UserDownloadStats => Set<UserDownloadStatEntity>();
    public DbSet<SchemaMigrationEntity> SchemaMigrations => Set<SchemaMigrationEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserEntity>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(user => user.Username);
            entity.Property(user => user.Username).HasColumnName("username").UseCollation("NOCASE");
            entity.Property(user => user.Salt).HasColumnName("salt").IsRequired();
            entity.Property(user => user.PasswordHash).HasColumnName("password_hash").IsRequired();
            entity.Property(user => user.CreatedUtc).HasColumnName("created_utc");
        });

        modelBuilder.Entity<SessionEntity>(entity =>
        {
            entity.ToTable("sessions");
            entity.HasKey(session => session.TokenHash);
            entity.Property(session => session.TokenHash).HasColumnName("token_hash");
            entity.Property(session => session.Username).HasColumnName("username");
            entity.Property(session => session.ExpiresUtc).HasColumnName("expires_utc");
            entity.HasOne(session => session.User).WithMany(user => user.Sessions)
                .HasForeignKey(session => session.Username).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(session => session.ExpiresUtc).HasDatabaseName("ix_sessions_expiration");
        });

        modelBuilder.Entity<FavoriteEntity>(entity =>
        {
            entity.ToTable("favorites");
            entity.HasKey(favorite => new { favorite.Username, favorite.PackageId, favorite.Version });
            entity.Property(favorite => favorite.Username).HasColumnName("username");
            entity.Property(favorite => favorite.PackageId).HasColumnName("package_id");
            entity.Property(favorite => favorite.Version).HasColumnName("version");
            entity.HasOne(favorite => favorite.User).WithMany(user => user.Favorites)
                .HasForeignKey(favorite => favorite.Username).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DownloadStatEntity>(entity =>
        {
            entity.ToTable("download_stats");
            entity.HasKey(stat => new { stat.PackageId, stat.Version });
            entity.Property(stat => stat.PackageId).HasColumnName("package_id");
            entity.Property(stat => stat.Version).HasColumnName("version");
            entity.Property(stat => stat.DownloadStarts).HasColumnName("download_starts");
            entity.Property(stat => stat.BytesServed).HasColumnName("bytes_served");
            entity.Property(stat => stat.LastRequestedUtc).HasColumnName("last_requested_utc");
        });
        modelBuilder.Entity<UserDownloadStatEntity>(entity =>
        {
            entity.ToTable("user_download_stats");
            entity.HasKey(stat => new { stat.Username, stat.PackageId, stat.Version });
            entity.Property(stat => stat.Username).HasColumnName("username");
            entity.Property(stat => stat.PackageId).HasColumnName("package_id");
            entity.Property(stat => stat.Version).HasColumnName("version");
            entity.Property(stat => stat.DownloadStarts).HasColumnName("download_starts");
            entity.Property(stat => stat.BytesServed).HasColumnName("bytes_served");
            entity.Property(stat => stat.LastRequestedUtc).HasColumnName("last_requested_utc");
            entity.HasOne(stat => stat.User).WithMany(user => user.Downloads)
                .HasForeignKey(stat => stat.Username).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(stat => new { stat.Username, stat.LastRequestedUtc })
                .IsDescending(false, true).HasDatabaseName("ix_user_download_stats_recent");
        });

        modelBuilder.Entity<SchemaMigrationEntity>(entity =>
        {
            entity.ToTable("schema_migrations");
            entity.HasKey(migration => migration.Version);
            entity.Property(migration => migration.Version).HasColumnName("version");
            entity.Property(migration => migration.AppliedUtc).HasColumnName("applied_utc");
        });
    }

}

public sealed class UserEntity
{
    public string Username { get; set; } = string.Empty;
    public byte[] Salt { get; set; } = [];
    public byte[] PasswordHash { get; set; } = [];
    public long CreatedUtc { get; set; }
    public List<SessionEntity> Sessions { get; set; } = [];
    public List<FavoriteEntity> Favorites { get; set; } = [];
    public List<UserDownloadStatEntity> Downloads { get; set; } = [];
}

public sealed class SessionEntity
{
    public string TokenHash { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public long ExpiresUtc { get; set; }
    public UserEntity User { get; set; } = null!;
}

public sealed class FavoriteEntity
{
    public string Username { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public UserEntity User { get; set; } = null!;
}

public class DownloadStatEntity
{
    public string PackageId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public long DownloadStarts { get; set; }
    public long BytesServed { get; set; }
    public long LastRequestedUtc { get; set; }
}

public sealed class UserDownloadStatEntity
{
    public string Username { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public long DownloadStarts { get; set; }
    public long BytesServed { get; set; }
    public long LastRequestedUtc { get; set; }
    public UserEntity User { get; set; } = null!;
}

public sealed class SchemaMigrationEntity
{
    public int Version { get; set; }
    public long AppliedUtc { get; set; }
}
