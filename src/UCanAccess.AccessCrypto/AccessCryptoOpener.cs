using UCanAccess.File;

namespace UCanAccess.AccessCrypto;

/// <summary>
/// Optional pure-.NET opener for password-encrypted modern Access files.
/// Microsoft Access or ACE is not required at runtime.
/// </summary>
public sealed class AccessCryptoOpener : IAccessDatabaseOpener
{
    /// <summary>
    /// Opens the requested file with the password supplied in the connection
    /// string.  A missing password is accepted for plaintext files and is
    /// rejected by the descriptor verifier for encrypted files. The opener is
    /// stateless, so it is safe to reuse for staged database and transaction
    /// reloads.
    /// </summary>
    public Database Open(AccessDatabaseOpenRequest request)
    {
        return Database.Open(
            request.Path,
            request.Encoding,
            request.ReadOnly,
            request.AllowExternalLinks,
            new AccessCryptoPageCodecFactory(request.Password ?? string.Empty));
    }
}
