using System.Data.Common;

namespace UCanAccess;

/// <summary>
/// Connection-string builder for the UCanAccess provider.
/// </summary>
public sealed class UCanAccessConnectionStringBuilder : DbConnectionStringBuilder
{
    public string? DataSource
    {
        get => TryGetValue("Data Source", out object? value) ? value?.ToString() : null;
        set => this["Data Source"] = value;
    }

    public bool ReadOnly
    {
        get => GetBoolean("Read Only", true);
        set => this["Read Only"] = value;
    }

    /// <summary>Password passed to an optional <see cref="IAccessDatabaseOpener"/>.</summary>
    public string? Password
    {
        get => TryGetValue("Password", out object? value) ? value?.ToString() : null;
        set => this["Password"] = value;
    }

    public override string ToString()
        => string.IsNullOrEmpty(Password)
            ? base.ToString()
            : new UCanAccessConnectionString(base.ConnectionString).ToString();

    public bool ShowSchema
    {
        get => GetBoolean("Show Schema", false);
        set => this["Show Schema"] = value;
    }

    public bool AllowExternalLinks
    {
        get => GetBoolean("Allow External Links", false);
        set => this["Allow External Links"] = value;
    }

    public string ColumnOrder
    {
        get => TryGetValue("Column Order", out object? value) ? value?.ToString() ?? "natural" : "natural";
        set => this["Column Order"] = value;
    }

    public bool LazyLoad
    {
        get => GetBoolean("Lazy Load", true);
        set => this["Lazy Load"] = value;
    }

    public bool KeepMirror
    {
        get => GetBoolean("Keep Mirror", true);
        set => this["Keep Mirror"] = value;
    }

    public string MirrorMode
    {
        get => TryGetValue("Mirror Mode", out object? value) ? value?.ToString() ?? "memory" : "memory";
        set => this["Mirror Mode"] = value;
    }

    public string? MirrorPath
    {
        get => TryGetValue("Mirror Path", out object? value) ? value?.ToString() : null;
        set => this["Mirror Path"] = value;
    }

    public string? MirrorFolder
    {
        get => TryGetValue("Mirror Folder", out object? value) ? value?.ToString() : null;
        set => this["Mirror Folder"] = value;
    }

    public string? TimeZone
    {
        get => TryGetValue("Time Zone", out object? value) ? value?.ToString() : null;
        set => this["Time Zone"] = value;
    }

    public bool PreferDateTimestamp
    {
        get => GetBoolean("Prefer Date Timestamp", false);
        set => this["Prefer Date Timestamp"] = value;
    }

    public string? NewDatabaseVersion
    {
        get => TryGetValue("New Database Version", out object? value) ? value?.ToString() : null;
        set => this["New Database Version"] = value;
    }

    public string? Encoding
    {
        get => TryGetValue("Encoding", out object? value) ? value?.ToString() : null;
        set => this["Encoding"] = value;
    }

    private bool GetBoolean(string key, bool defaultValue)
    {
        if (!TryGetValue(key, out object? value) || value is null)
        {
            return defaultValue;
        }
        if (value is bool b)
        {
            return b;
        }
        return value.ToString()!.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "1" => true,
            "false" or "no" or "0" => false,
            _ => throw new FormatException($"Invalid boolean value '{value}' for '{key}'."),
        };
    }
}
