using System.Data.Common;

namespace UCanAccess;

/// <summary>
/// Connection-string builder for the UCanAccess provider.
/// </summary>
public sealed class UCanAccessConnectionStringBuilder : DbConnectionStringBuilder
{
    private UCanAccessConnectionString Parsed
        => new(ConnectionString);

    public string? DataSource
    {
        get
        {
            string value = Parsed.DataSource;
            return value.Length == 0 ? null : value;
        }
        set => this["Data Source"] = value;
    }

    public bool ReadOnly
    {
        get => Parsed.ReadOnly;
        set => this["Read Only"] = value;
    }

    /// <summary>Password passed to an optional <see cref="IAccessDatabaseOpener"/>.</summary>
    public string? Password
    {
        get => Parsed.Password;
        set => this["Password"] = value;
    }

    public override string ToString()
        => string.IsNullOrEmpty(Password)
            ? base.ToString()
            : new UCanAccessConnectionString(base.ConnectionString).ToString();

    public bool ShowSchema
    {
        get => Parsed.ShowSchema;
        set => this["Show Schema"] = value;
    }

    /// <summary>Upstream-compatible alias for exposing system objects.</summary>
    public bool SysSchema
    {
        get => Parsed.SysSchema;
        set => this["Sys Schema"] = value;
    }

    public bool AllowExternalLinks
    {
        get => Parsed.AllowExternalLinks;
        set => this["Allow External Links"] = value;
    }

    public string ColumnOrder
    {
        get => Parsed.ColumnOrder;
        set => this["Column Order"] = value;
    }

    public bool LazyLoad
    {
        get => Parsed.LazyLoad;
        set => this["Lazy Load"] = value;
    }

    public bool KeepMirror
    {
        get => new UCanAccessConnectionString(ConnectionString).KeepMirror;
        set => this["Keep Mirror"] = value;
    }

    /// <summary>
    /// Persistent mirror location represented by the upstream
    /// <c>keepMirror=&lt;path&gt;</c> form. Setting this replaces any boolean
    /// <see cref="KeepMirror"/> value.
    /// </summary>
    public string? PersistentMirrorPath
    {
        get
        {
            var options = new UCanAccessConnectionString(ConnectionString);
            return options.PersistentMirrorPath;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Remove("Keep Mirror");
            }
            else
            {
                this["Keep Mirror"] = value;
            }
        }
    }

    /// <summary>Upstream-compatible in-memory mirror toggle.</summary>
    public bool Memory
    {
        get => Parsed.Memory;
        set => this["Memory"] = value;
    }

    /// <summary>Release a one-shot connection's mirror after every operation.</summary>
    public bool ImmediatelyReleaseResources
    {
        get => Parsed.ImmediatelyReleaseResources;
        set => this["Immediately Release Resources"] = value;
    }

    /// <summary>Upstream alias for <see cref="ImmediatelyReleaseResources"/>.</summary>
    public bool SingleConnection
    {
        get => Parsed.ImmediatelyReleaseResources;
        set => this["Single Connection"] = value;
    }

    /// <summary>Disable automatic refresh when the Access file changes externally.</summary>
    public bool PreventReloading
    {
        get => Parsed.PreventReloading;
        set => this["Prevent Reloading"] = value;
    }

    public string MirrorMode
    {
        get => Parsed.MirrorMode;
        set => this["Mirror Mode"] = value;
    }

    public string? MirrorPath
    {
        get => Parsed.MirrorPath;
        set => this["Mirror Path"] = value;
    }

    public string? MirrorFolder
    {
        get => Parsed.MirrorFolder;
        set => this["Mirror Folder"] = value;
    }

    public string? TimeZone
    {
        get => Parsed.TimeZoneName;
        set => this["Time Zone"] = value;
    }

    public bool PreferDateTimestamp
    {
        get => Parsed.PreferDateTimestamp;
        set => this["Prefer Date Timestamp"] = value;
    }

    public string? NewDatabaseVersion
    {
        get => Parsed.NewDatabaseVersion;
        set => this["New Database Version"] = value;
    }

    public string? Encoding
    {
        get => Parsed.EncodingName;
        set => this["Encoding"] = value;
    }

}
