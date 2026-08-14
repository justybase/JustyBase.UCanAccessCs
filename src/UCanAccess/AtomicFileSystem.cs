namespace UCanAccess;

/// <summary>
/// File operations used by the atomic staging boundary.  Keeping these calls
/// behind a small internal seam makes replacement failures deterministic in
/// tests without exposing a filesystem abstraction as part of the provider API.
/// </summary>
internal interface IAtomicFileSystem
{
    void Copy(string sourceFileName, string destFileName, bool overwrite);

    void Replace(string sourceFileName, string destinationFileName,
        string? destinationBackupFileName, bool ignoreMetadataErrors);

    void Delete(string path);
}

internal sealed class PhysicalAtomicFileSystem : IAtomicFileSystem
{
    internal static PhysicalAtomicFileSystem Instance { get; } = new();

    private PhysicalAtomicFileSystem()
    {
    }

    public void Copy(string sourceFileName, string destFileName, bool overwrite)
        => System.IO.File.Copy(sourceFileName, destFileName, overwrite);

    public void Replace(string sourceFileName, string destinationFileName,
        string? destinationBackupFileName, bool ignoreMetadataErrors)
        => System.IO.File.Replace(sourceFileName, destinationFileName,
            destinationBackupFileName, ignoreMetadataErrors);

    public void Delete(string path)
        => System.IO.File.Delete(path);
}
