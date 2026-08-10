using System.Text;
using UCanAccess.File;

namespace UCanAccess;

/// <summary>Arguments passed to a pluggable Access database opener.</summary>
public sealed record AccessDatabaseOpenRequest(
    string Path,
    bool ReadOnly,
    Encoding? Encoding,
    bool AllowExternalLinks,
    string? Password);

/// <summary>
/// Opens an Access file, optionally decoding a password-protected/encrypted
/// container before handing it to the provider. Implementations belong in an
/// optional package; the core provider does not contain cryptography.
/// </summary>
public interface IAccessDatabaseOpener
{
    Database Open(AccessDatabaseOpenRequest request);
}
