using System.Data.Common;

namespace UCanAccess;

/// <summary>
/// The <see cref="DbProviderFactory"/> for the UCanAccess provider.
/// </summary>
public sealed class UCanAccessFactory : DbProviderFactory
{
    public static readonly UCanAccessFactory Instance = new();

    private UCanAccessFactory()
    {
    }

    public override DbConnection CreateConnection() => new UCanAccessConnection();

    public override DbCommand CreateCommand() => new UCanAccessCommand(new UCanAccessConnection());

    public override DbParameter CreateParameter() => new UCanAccessParameter();

    public override UCanAccessConnectionStringBuilder CreateConnectionStringBuilder() => new();

    public override bool CanCreateDataSourceEnumerator => false;
}
