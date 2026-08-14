namespace UCanAccess.File;

/// <summary>
/// Information supplied to an optional page codec when a database is opened.
/// <paramref name="RawRootPage"/> contains the root page exactly as stored on
/// disk, before Jet's normal header mask is removed.
/// </summary>
public sealed record AccessPageCodecContext(
    string Path,
    JetFormat Format,
    bool ReadOnly,
    ReadOnlyMemory<byte> RawRootPage);

/// <summary>Creates an optional codec for a database file.</summary>
public interface IAccessPageCodecFactory
{
    IAccessPageCodec Create(AccessPageCodecContext context);
}

/// <summary>
/// Transforms complete Jet pages between their on-disk and logical forms.
/// Implementations should support overlapping input/output spans.
/// </summary>
public interface IAccessPageCodec : IDisposable
{
    void DecodePage(int pageNumber, ReadOnlySpan<byte> encrypted, Span<byte> plaintext);

    void EncodePage(int pageNumber, ReadOnlySpan<byte> plaintext, Span<byte> encrypted);
}
