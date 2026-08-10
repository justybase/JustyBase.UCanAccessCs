namespace UCanAccess.File;

/// <summary>A single value from an Access multi-valued field.</summary>
public sealed record AccessSingleValue(object? Value);

/// <summary>An attachment stored in an Access attachment field.</summary>
public sealed record AccessAttachment(
    byte[]? FileData,
    int? FileFlags,
    string? FileName,
    DateTime? FileTimeStamp,
    string? FileType,
    string? FileURL);

/// <summary>A version value from an Access version-history complex field.</summary>
public sealed record AccessVersion(object? Value, DateTime? Modified);
