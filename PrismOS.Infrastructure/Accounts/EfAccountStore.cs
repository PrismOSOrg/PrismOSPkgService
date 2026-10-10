using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PrismOS.Application.Accounts;
using PrismOS.Domain.Accounts;
using PrismOS.Infrastructure.Persistence;

namespace PrismOS.Infrastructure.Accounts;

public sealed class EfAccountStore(IDbContextFactory<RepositoryDbContext> contextFactory) : IAccountStore
{
    private const int PasswordIterations = 310_000;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    private static readonly Regex UsernamePattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{2,23}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly byte[] DummySalt = RandomNumberGenerator.GetBytes(16);

    public static async Task InitializeAsync(IDbContextFactory<RepositoryDbContext> contextFactory,
        string? legacyJsonPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(legacyJsonPath) || !File.Exists(legacyJsonPath)) return;
        await using RepositoryDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await context.SchemaMigrations.AnyAsync(item => item.Version == 1001, cancellationToken)) return;

        await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await using var stream = File.OpenRead(legacyJsonPath);
        LegacySnapshot snapshot = await JsonSerializer.DeserializeAsync<LegacySnapshot>(stream,
            cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Legacy account data is empty or invalid.");

        foreach (LegacyUser user in snapshot.Users ?? [])
        {
            if (string.IsNullOrWhiteSpace(user.Username) || string.IsNullOrWhiteSpace(user.Salt)
                || string.IsNullOrWhiteSpace(user.PasswordHash))
                throw new InvalidDataException("Legacy account data contains an invalid user.");
            string username = user.Username.ToLowerInvariant();
            if (!await context.Users.AnyAsync(item => item.Username == username, cancellationToken))
            {
                context.Users.Add(new UserEntity
                {
                    Username = username,
                    Salt = Convert.FromBase64String(user.Salt),
                    PasswordHash = Convert.FromBase64String(user.PasswordHash),
                    CreatedUtc = user.CreatedUtc.ToUnixTimeMilliseconds()
                });
            }

            foreach (string favorite in user.Favorites ?? [])
            {
                int separator = favorite.IndexOf('/');
                if (separator <= 0 || separator == favorite.Length - 1) continue;
                context.Favorites.Add(new FavoriteEntity
                {
                    Username = username,
                    PackageId = favorite[..separator],
                    Version = favorite[(separator + 1)..]
                });
            }
            foreach (LegacyDownloadStat item in user.Downloads ?? [])
            {
                context.UserDownloadStats.Add(new UserDownloadStatEntity
                {
                    Username = username,
                    PackageId = item.Id,
                    Version = item.Version,
                    DownloadStarts = item.DownloadStarts,
                    BytesServed = item.BytesServed,
                    LastRequestedUtc = item.LastRequestedUtc.ToUnixTimeMilliseconds()
                });
            }
        }

        foreach (LegacyDownloadStat item in snapshot.Downloads ?? [])
        {
            context.DownloadStats.Add(new DownloadStatEntity
            {
                PackageId = item.Id,
                Version = item.Version,
                DownloadStarts = item.DownloadStarts,
                BytesServed = item.BytesServed,
                LastRequestedUtc = item.LastRequestedUtc.ToUnixTimeMilliseconds()
            });
        }
        context.SchemaMigrations.Add(new SchemaMigrationEntity
        {
            Version = 1001,
            AppliedUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<(string? Token, string? Error)> RegisterAsync(string username, string password,
        CancellationToken cancellationToken)
    {
        username = (username ?? string.Empty).Trim();
        if (!UsernamePattern.IsMatch(username))
            return (null, "Username must be 3–24 letters, digits, dots, underscores, or hyphens.");
        if (password is null || password.Length is < 12 or > 128)
            return (null, "Password must be between 12 and 128 characters.");

        string canonicalUsername = username.ToLowerInvariant();
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        await using RepositoryDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            context.Users.Add(new UserEntity
            {
                Username = canonicalUsername,
                Salt = salt,
                PasswordHash = HashPassword(password, salt),
                CreatedUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            string token = CreateSession(context, canonicalUsername);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (token, null);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            await transaction.RollbackAsync(cancellationToken);
            return (null, "Username is already registered.");
        }
    }

    public async Task<string?> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (password is null || password.Length > 128)
        {
            _ = HashPassword(string.Empty, DummySalt);
            return null;
        }
        username = (username ?? string.Empty).Trim().ToLowerInvariant();
        await using RepositoryDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        UserEntity? user = await context.Users.SingleOrDefaultAsync(item => item.Username == username, cancellationToken);
        if (user is null)
        {
            _ = HashPassword(password, DummySalt);
            return null;
        }
        byte[] actual = HashPassword(password, user.Salt);
        if (!CryptographicOperations.FixedTimeEquals(actual, user.PasswordHash)) return null;

        await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        string token = CreateSession(context, user.Username);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return token;
    }

    public async Task<string?> ResolveSessionAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using RepositoryDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Sessions.Where(session => session.TokenHash == HashToken(token)
                && session.ExpiresUtc > now).Select(session => session.Username)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task LogoutAsync(string token, CancellationToken cancellationToken)
    {
        await using RepositoryDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Sessions.Where(session => session.TokenHash == HashToken(token))
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>?> GetFavoritesAsync(string username,
        CancellationToken cancellationToken)
    {
        await using RepositoryDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Favorites.Where(favorite => favorite.Username == username)
            .OrderBy(favorite => favorite.PackageId).ThenBy(favorite => favorite.Version)
            .Select(favorite => favorite.PackageId + "/" + favorite.Version)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool?> SetFavoriteAsync(string username, string packageKey, bool favorite,
        CancellationToken cancellationToken)
    {
        int separator = packageKey.IndexOf('/');
        if (separator <= 0 || separator == packageKey.Length - 1) return false;
        await using RepositoryDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        string packageId = packageKey[..separator];
        string version = packageKey[(separator + 1)..];
        FavoriteEntity? existing = await context.Favorites.SingleOrDefaultAsync(item =>
            item.Username == username && item.PackageId == packageId && item.Version == version, cancellationToken);
        if (favorite)
        {
            if (existing is not null) return false;
            if (!await context.Users.AnyAsync(user => user.Username == username, cancellationToken)) return false;
            context.Favorites.Add(new FavoriteEntity { Username = username, PackageId = packageId, Version = version });
        }
        else
        {
            if (existing is null) return false;
            context.Favorites.Remove(existing);
        }
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<UserDownloadStat>?> GetUserDownloadsAsync(string username,
        CancellationToken cancellationToken)
    {
        await using RepositoryDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<UserDownloadStatEntity> rows = await context.UserDownloadStats
            .Where(item => item.Username == username)
            .OrderByDescending(item => item.LastRequestedUtc).ToListAsync(cancellationToken);
        return rows.Select(item => new UserDownloadStat
            {
                Id = item.PackageId,
                Version = item.Version,
                DownloadStarts = item.DownloadStarts,
                BytesServed = item.BytesServed,
                LastRequestedUtc = DateTimeOffset.FromUnixTimeMilliseconds(item.LastRequestedUtc)
            }).ToList();
    }

    public async Task<IReadOnlyList<PackageDownloadStat>> GetDownloadStatsAsync(CancellationToken cancellationToken)
    {
        await using RepositoryDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<DownloadStatEntity> rows = await context.DownloadStats
            .OrderByDescending(item => item.DownloadStarts).ThenBy(item => item.PackageId)
            .ToListAsync(cancellationToken);
        return rows.Select(item => new PackageDownloadStat
            {
                Id = item.PackageId,
                Version = item.Version,
                DownloadStarts = item.DownloadStarts,
                BytesServed = item.BytesServed,
                LastRequestedUtc = DateTimeOffset.FromUnixTimeMilliseconds(item.LastRequestedUtc)
            }).ToList();
    }

    public async Task RecordDownloadChunkAsync(string? username, string id, string version,
        long offset, int bytesServed, CancellationToken cancellationToken)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long starts = offset == 0 ? 1 : 0;
        await using RepositoryDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await UpsertStatAsync(context, "download_stats", id, version, starts, bytesServed, now, cancellationToken);
        if (username is not null)
            await UpsertStatAsync(context, "user_download_stats", id, version, starts, bytesServed,
                now, cancellationToken, username);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task UpsertStatAsync(RepositoryDbContext context, string table, string id,
        string version, long starts, int bytes, long now, CancellationToken cancellationToken, string? username = null)
    {
        if (username is null)
        {
            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO download_stats(package_id, version, download_starts, bytes_served, last_requested_utc)
                VALUES (@p0, @p1, @p2, @p3, @p4)
                ON CONFLICT(package_id, version) DO UPDATE SET
                    download_starts = download_starts + excluded.download_starts,
                    bytes_served = bytes_served + excluded.bytes_served,
                    last_requested_utc = excluded.last_requested_utc;
                """, new object[] { id, version, starts, bytes, now }, cancellationToken);
        }
        else
        {
            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO user_download_stats(username, package_id, version, download_starts, bytes_served, last_requested_utc)
                VALUES (@p0, @p1, @p2, @p3, @p4, @p5)
                ON CONFLICT(username, package_id, version) DO UPDATE SET
                    download_starts = download_starts + excluded.download_starts,
                    bytes_served = bytes_served + excluded.bytes_served,
                    last_requested_utc = excluded.last_requested_utc;
                """, new object[] { username, id, version, starts, bytes, now }, cancellationToken);
        }
    }

    private static string CreateSession(RepositoryDbContext context, string username)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        context.Sessions.Where(session => session.ExpiresUtc <= now).ExecuteDelete();
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        context.Sessions.Add(new SessionEntity
        {
            TokenHash = HashToken(token),
            Username = username,
            ExpiresUtc = now + (long)SessionLifetime.TotalMilliseconds
        });
        return token;
    }

    private static byte[] HashPassword(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt,
            PasswordIterations, HashAlgorithmName.SHA256, 32);

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed class LegacySnapshot
    {
        public List<LegacyUser>? Users { get; set; } = [];
        public List<LegacyDownloadStat>? Downloads { get; set; } = [];
    }

    private sealed class LegacyUser
    {
        public string Username { get; set; } = string.Empty;
        public string Salt { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public DateTimeOffset CreatedUtc { get; set; }
        public List<string>? Favorites { get; set; } = [];
        public List<LegacyDownloadStat>? Downloads { get; set; } = [];
    }

    private sealed class LegacyDownloadStat
    {
        public string Id { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public long DownloadStarts { get; set; }
        public long BytesServed { get; set; }
        public DateTimeOffset LastRequestedUtc { get; set; }
    }
}
