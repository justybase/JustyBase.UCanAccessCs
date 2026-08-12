using UCanAccess.File;

namespace UCanAccess.AccessCrypto;

/// <summary>Thrown when an Access encryption envelope cannot be opened.</summary>
public sealed class AccessEncryptionException : DatabaseException
{
    public AccessEncryptionException(string message) : base(message)
    {
    }

    public AccessEncryptionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
