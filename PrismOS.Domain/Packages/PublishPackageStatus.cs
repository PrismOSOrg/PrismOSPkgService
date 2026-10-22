namespace PrismOS.Domain.Packages;

public enum PublishPackageStatus
{
    Success,
    InvalidMetadata,
    TooLarge,
    AlreadyExists,
    RepositoryFull,
    Empty,
    LengthMismatch,
    CatalogFull
}
