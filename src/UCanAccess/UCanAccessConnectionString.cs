using System.Collections;
using System.Text;
using UCanAccess.File;

namespace UCanAccess;

/// <summary>
/// Parses connection strings for the UCanAccess provider.
/// Supported keys (case-insensitive):
///   Data Source | Path | File | Database  -- the .mdb/.accdb file path (required)
///   Read Only                              -- open without write intent (default true)
///   Password | PWD                         -- password for an opener/codec (never echoed)
///   Encoding | Code Page                   -- text encoding for Jet 3 databases (e.g. "936" or "GBK")
///   Show Schema                            -- expose system objects (default false)
///   Column Order                           -- "natural" (default) or "display"
///   Lazy Load                              -- load linked tables on demand (default true)
///   Keep Mirror                            -- keep the SQLite mirror cached (default true)
///   Mirror Mode                            -- "memory" (default) or "file"
///   Mirror Path                            -- SQLite file used by file mode
///   Mirror Folder                          -- folder for an automatically named file mirror
///   Time Zone                              -- accepted for compatibility; Access values remain timezone-free
///   Prefer Date Timestamp                  -- accepted for compatibility; Access values retain provider precision
///   New Database Version                   -- version for created databases (2000/2002/2003/2007/2010/2016)
/// </summary>
public sealed class UCanAccessConnectionString
{
    private static readonly Dictionary<string, string> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["data source"] = "datasource",
        ["path"] = "datasource",
        ["file"] = "datasource",
        ["database"] = "datasource",
        ["datasource"] = "datasource",
        ["read only"] = "readonly",
        ["readonly"] = "readonly",
        ["password"] = "password",
        ["pwd"] = "password",
        ["encoding"] = "encoding",
        ["code page"] = "encoding",
        ["show schema"] = "showschema",
        ["showschema"] = "showschema",
        ["column order"] = "columnorder",
        ["columnorder"] = "columnorder",
        ["lazy load"] = "lazyload",
        ["lazyload"] = "lazyload",
        ["keep mirror"] = "keepmirror",
        ["keepmirror"] = "keepmirror",
        ["mirror mode"] = "mirrormode",
        ["mirrormode"] = "mirrormode",
        ["mirror path"] = "mirrorpath",
        ["mirrorpath"] = "mirrorpath",
        ["mirror folder"] = "mirrorfolder",
        ["mirrorfolder"] = "mirrorfolder",
        ["time zone"] = "timezone",
        ["timezone"] = "timezone",
        ["prefer date timestamp"] = "preferdatetimestamp",
        ["preferdatetimestamp"] = "preferdatetimestamp",
        ["allow external links"] = "allowexternallinks",
        ["allowexternallinks"] = "allowexternallinks",
        ["new database version"] = "newdatabaseversion",
        ["newdatabaseversion"] = "newdatabaseversion",
    };

    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public UCanAccessConnectionString(string connectionString)
    {
        foreach (string part in Split(connectionString))
        {
            int eq = part.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }
            string key = part[..eq].Trim();
            string value = Unquote(part[(eq + 1)..].Trim());
            if (key.Length == 0)
            {
                continue;
            }
            if (KeyAliases.TryGetValue(key, out string? canonical))
            {
                key = canonical;
            }
            _values[key] = value;
        }
    }

    public string DataSource
        => _values.TryGetValue("datasource", out string? value) ? value : string.Empty;

    public bool ReadOnly
        => GetBoolean("readonly", defaultValue: true);

    /// <summary>optional database password; omitted from <see cref="ToString"/></summary>
    public string? Password
        => _values.TryGetValue("password", out string? value) ? value : null;

    public string? EncodingName
        => _values.TryGetValue("encoding", out string? value) ? value : null;

    /// <summary>expose system objects (MSys*) when true; default false</summary>
    public bool ShowSchema
        => GetBoolean("showschema", defaultValue: false);

    /// <summary>whether linked databases outside the main database directory may be opened</summary>
    public bool AllowExternalLinks
        => GetBoolean("allowexternallinks", defaultValue: false);

    /// <summary>whether the mirror is built during Open (false) or on first use (true)</summary>
    public bool LazyLoad
        => GetBoolean("lazyload", defaultValue: true);

    /// <summary>whether the SQLite mirror is retained for the connection lifetime</summary>
    public bool KeepMirror
        => GetBoolean("keepmirror", defaultValue: true);

    /// <summary>SQLite mirror storage mode: memory (default) or file.</summary>
    public string MirrorMode
        => _values.TryGetValue("mirrormode", out string? value) && value.Trim().Length > 0
            ? value.Trim()
            : "memory";

    /// <summary>explicit SQLite mirror path used when <see cref="MirrorMode"/> is file</summary>
    public string? MirrorPath
        => _values.TryGetValue("mirrorpath", out string? value) && value.Length > 0 ? value : null;

    /// <summary>folder for an automatically named file mirror</summary>
    public string? MirrorFolder
        => _values.TryGetValue("mirrorfolder", out string? value) && value.Length > 0 ? value : null;

    /// <summary>optional date/time zone identifier</summary>
    public string? TimeZoneName
        => _values.TryGetValue("timezone", out string? value) ? value : null;

    /// <summary>whether date values should retain timestamp precision</summary>
    public bool PreferDateTimestamp
        => GetBoolean("preferdatetimestamp", defaultValue: false);

    /// <summary>column order: "natural" (default) or "display"</summary>
    public string ColumnOrder
        => _values.TryGetValue("columnorder", out string? value) ? value.Trim() : "natural";

    /// <summary>version of a newly created database: "2000", "2002", "2003", "2007", "2010" or "2016" (null = don't create)</summary>
    public string? NewDatabaseVersion
        => _values.TryGetValue("newdatabaseversion", out string? value) ? value.Trim() : null;

    public System.Text.Encoding? ResolveEncoding()
    {
        string? name = EncodingName;
        if (name == null)
        {
            return null;
        }
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return int.TryParse(name, out int codePage)
                ? System.Text.Encoding.GetEncoding(codePage)
                : System.Text.Encoding.GetEncoding(name);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Unsupported encoding '{name}'.", ex);
        }
    }

    public override string ToString()
        => string.Join(";", _values.Select(kv => kv.Key.Equals("password", StringComparison.OrdinalIgnoreCase)
            ? "Password=***"
            : $"{kv.Key}={kv.Value}"));

    private bool GetBoolean(string key, bool defaultValue)
    {
        if (!_values.TryGetValue(key, out string? value) || value.Trim().Length == 0)
        {
            return defaultValue;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "1" => true,
            "false" or "no" or "0" => false,
            _ => throw new ArgumentException($"Invalid boolean value '{value}' for '{key}'."),
        };
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && (value[0] == '\'' || value[0] == '"') && value[^1] == value[0])
        {
            char quote = value[0];
            string escaped = new string(quote, 2);
            return value[1..^1].Replace(escaped, quote.ToString(), StringComparison.Ordinal);
        }
        return value;
    }

    private static IEnumerable<string> Split(string connectionString)
    {
        // respects single/double quotes around values
        var sb = new StringBuilder();
        char? quote = null;
        string input = connectionString ?? string.Empty;
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (quote != null)
            {
                sb.Append(c);
                if (c == quote)
                {
                    if (i + 1 < input.Length && input[i + 1] == quote)
                    {
                        sb.Append(input[++i]);
                    }
                    else
                    {
                        quote = null;
                    }
                }
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                sb.Append(c);
            }
            else if (c == ';')
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }
}
