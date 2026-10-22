namespace PrismOS.Domain.Packages;

public sealed record PublishPackageResult
{
    private PublishPackageResult(PublishPackageStatus status, PackageRecord? package)
    {
        Status = status;
        Package = package;
    }

    public PublishPackageStatus Status { get; }
    public PackageRecord? Package { get; }

    public static PublishPackageResult Succeeded(PackageRecord package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return new PublishPackageResult(PublishPackageStatus.Success, package);
    }

    public static PublishPackageResult Failed(PublishPackageStatus status)
    {
        if (!Enum.IsDefined(status) || status == PublishPackageStatus.Success)
            throw new ArgumentOutOfRangeException(nameof(status), status,
                "A failed publish result must use a defined failure status.");

        return new PublishPackageResult(status, null);
    }
}
