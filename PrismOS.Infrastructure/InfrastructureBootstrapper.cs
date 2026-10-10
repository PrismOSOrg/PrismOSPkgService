using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrismOS.Application.Accounts;
using PrismOS.Application.Repositories;
using PrismOS.Infrastructure.Accounts;
using PrismOS.Infrastructure.Persistence;
using PrismOS.Infrastructure.Repositories;

namespace PrismOS.Infrastructure;

public sealed record InfrastructureOptions(
    string PackagesPath,
    int ChunkSize,
    int MaxCatalogBytes,
    int MaxUploadBytes,
    long MaxRepositoryBytes,
    string DatabasePath,
    string? LegacyAccountsPath);

public static class InfrastructureBootstrapper
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services,
        InfrastructureOptions options)
    {
        string fullDatabasePath = Path.GetFullPath(options.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDatabasePath)!);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();
        services.AddDbContextFactory<RepositoryDbContext>(builder => builder.UseSqlite(connectionString));
        services.AddSingleton<IAccountStore, EfAccountStore>();
        return services;
    }

    public static async Task InitializeDatabaseAsync(IServiceProvider services, string? legacyAccountsPath,
        CancellationToken cancellationToken = default)
    {
        IDbContextFactory<RepositoryDbContext> contextFactory =
            services.GetRequiredService<IDbContextFactory<RepositoryDbContext>>();
        await using RepositoryDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        await EfAccountStore.InitializeAsync(contextFactory, legacyAccountsPath, cancellationToken);
    }

    public static async Task<IPackageRepository> LoadPackagesAsync(InfrastructureOptions options,
        CancellationToken cancellationToken = default) =>
        await PackageRepository.LoadAsync(options.PackagesPath, options.ChunkSize,
            options.MaxCatalogBytes, options.MaxUploadBytes, options.MaxRepositoryBytes, cancellationToken);
}
