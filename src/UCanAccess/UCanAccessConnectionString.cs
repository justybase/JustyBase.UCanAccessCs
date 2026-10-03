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
///   Memory                                 -- upstream alias; false selects a file-backed mirror
///   Immediately Release Resources          -- upstream one-shot connection mode
///   Prevent Reloading                       -- do not reopen a changed file during this connection
///   Mirror Mode                            -- "memory" (default) or "file"
///   Mirror Path                            -- SQLite file used by file mode
///   Mirror Folder                          -- folder for an automatically named file mirror
///   Time Zone                              -- accepted for compatibility; Access values remain timezone-free
///   Prefer Date Timestamp                  -- accepted for compatibility; Access values retain provider precision
///   New Database Version                   -- version for created databases (2000/2002/2003/2007/2010/2016)
///   Remap                                  -- upstream linked-db remap: orig|new&amp;orig2|new2 (trusted explicit config)
///   Skip Indexes                           -- upstream alias; accepted, mirror has no secondary indexes (default false)
///   Open Exclusive | Lock Mdb              -- lock the file even for Read Only opens (default false)
///   Ignore Case                            -- case-insensitive text comparison (default true)
///   Concat Nulls                           -- NULL &amp; 'x' yields NULL when true, 'x' when false (default false)
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
        ["memory"] = "memory",
        ["immediately release resources"] = "immediatelyreleaseresources",
        ["immediatelyreleaseresources"] = "immediatelyreleaseresources",
        ["single connection"] = "immediatelyreleaseresources",
        ["singleconnection"] = "immediatelyreleaseresources",
        ["prevent reloading"] = "preventreloading",
        ["preventreloading"] = "preventreloading",
        ["sys schema"] = "sysschema",
        ["sysschema"] = "sysschema",
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
        ["remap"] = "remap",
        ["skip indexes"] = "skipindexes",
        ["skipindexes"] = "skipindexes",
        ["open exclusive"] = "openexclusive",
        ["openexclusive"] = "openexclusive",
        ["lock mdb"] = "openexclusive",
        ["lockmdb"] = "openexclusive",
        ["ignore case"] = "ignorecase",
        ["ignorecase"] = "ignorecase",
        ["concat nulls"] = "concatnulls",
        ["concatnulls"] = "concatnulls",
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
        => GetBoolean("showschema", defaultValue: false) || GetBoolean("sysschema", defaultValue: false);

    /// <summary>whether the upstream <c>sysSchema</c> alias was enabled</summary>
    public bool SysSchema
        => GetBoolean("sysschema", defaultValue: false);

    /// <summary>whether linked databases outside the main database directory may be opened</summary>
    public bool AllowExternalLinks
        => GetBoolean("allowexternallinks", defaultValue: false);

    /// <summary>whether the mirror is built during Open (false) or on first use (true)</summary>
    public bool LazyLoad
        => GetBoolean("lazyload", defaultValue: true);

    /// <summary>whether the SQLite mirror is retained for the connection lifetime</summary>
    public bool KeepMirror
    {
        get
        {
            if (ImmediatelyReleaseResources)
            {
                return false;
            }
            if (!_values.TryGetValue("keepmirror", out string? value) || value.Trim().Length == 0)
            {
                return true;
            }
            return IsBoolean(value) ? ParseBoolean(value, "keepmirror") : true;
        }
    }

    /// <summary>
    /// Releases the provider-owned mirror as soon as the last operation ends.
    /// This is the UCanAccess <c>immediatelyReleaseResources</c>/<c>singleConnection</c>
    /// compatibility mode for one-shot jobs.
    /// </summary>
    public bool ImmediatelyReleaseResources
        => GetBoolean("immediatelyreleaseresources", defaultValue: false);

    /// <summary>
    /// Prevents automatic reopening when another process changes the Access file.
    /// </summary>
    public bool PreventReloading
        => GetBoolean("preventreloading", defaultValue: false);

    /// <summary>
    /// The upstream-compatible persistent mirror path supplied through
    /// <c>keepMirror=&lt;path&gt;</c>. Boolean Keep Mirror values retain their
    /// established provider meaning.
    /// </summary>
    public string? PersistentMirrorPath
        => _values.TryGetValue("keepmirror", out string? value) && !string.IsNullOrWhiteSpace(value)
            && !IsBoolean(value) ? value.Trim() : null;

    /// <summary>SQLite mirror storage mode: memory (default) or file.</summary>
    public string MirrorMode
    {
        get
        {
            if (_values.TryGetValue("mirrormode", out string? value) && value.Trim().Length > 0)
            {
                return value.Trim();
            }
            if (_values.TryGetValue("memory", out string? memory) && memory.Trim().Length > 0)
            {
                return ParseBoolean(memory, "memory") ? "memory" : "file";
            }
            return PersistentMirrorPath != null ? "file" : "memory";
        }
    }

    /// <summary>whether the effective SQLite mirror mode is in-memory</summary>
    public bool Memory
        => MirrorMode.Equals("memory", StringComparison.OrdinalIgnoreCase);

    /// <summary>explicit SQLite mirror path used when <see cref="MirrorMode"/> is file</summary>
    public string? MirrorPath
        => _values.TryGetValue("mirrorpath", out string? value) && value.Length > 0
            ? value
            : PersistentMirrorPath;

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

    /// <summary>raw upstream remap value: orig|new&amp;orig2|new2 (null = no remap)</summary>
    public string? Remap
        => _values.TryGetValue("remap", out string? value) && value.Trim().Length > 0 ? value.Trim() : null;

    /// <summary>
    /// Upstream <c>skipIndexes</c>: skips simple (non-constraint) indexes when
    /// building the mirror. The SQLite mirror currently carries no secondary
    /// indexes, so this is accepted for compatibility and has no effect on file
    /// data or referential integrity (default false).
    /// </summary>
    public bool SkipIndexes
        => GetBoolean("skipindexes", defaultValue: false);

    /// <summary>
    /// Upstream <c>openExclusive</c> (legacy <c>lockMdb</c>): lock the Access file
    /// even for read-only opens, failing when another process holds the lock
    /// (default false; writable opens always lock).
    /// </summary>
    public bool OpenExclusive
        => GetBoolean("openexclusive", defaultValue: false);

    /// <summary>
    /// Upstream <c>ignoreCase</c>: case-insensitive text comparison
    /// (default true, matches HSQLDB SQL_TEXT_UCC).
    /// </summary>
    public bool IgnoreCase
        => GetBoolean("ignorecase", defaultValue: true);

    /// <summary>
    /// Upstream <c>concatNulls</c>: when true, <c>&amp;</c>/<c>||</c> with NULL
    /// yields NULL (pre-3.0 behavior). Default false maps NULL to ''.
    /// </summary>
    public bool ConcatNulls
        => GetBoolean("concatnulls", defaultValue: false);

    /// <summary>parsed trusted remap map (original -&gt; new path, case-insensitive).</summary>
    public IReadOnlyDictionary<string, string> LinkRemap => ParseRemap(Remap);

    /// <summary>
    /// Parses the upstream remap syntax: pairs separated by '&amp;', original and
    /// new path separated by '|'. Empty value yields an empty map.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseRemap(string? raw)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return map;
        }
        foreach (string pair in raw.Split('&'))
        {
            if (string.IsNullOrWhiteSpace(pair))
            {
                continue;
            }
            int sep = pair.IndexOf('|');
            if (sep < 0)
            {
                throw new ArgumentException($"Invalid Remap entry '{pair}'. Expected 'original|new'.");
            }
            string original = pair[..sep].Trim();
            string target = pair[(sep + 1)..].Trim();
            if (original.Length == 0 || target.Length == 0)
            {
                throw new ArgumentException($"Invalid Remap entry '{pair}'. Expected 'original|new'.");
            }
            map[original] = target;
        }
        return map;
    }

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

        return ParseBoolean(value, key);
    }

    private static bool IsBoolean(string value)
        => value.Trim().ToLowerInvariant() is "true" or "yes" or "1" or "false" or "no" or "0";

    private static bool ParseBoolean(string value, string key)
        => value.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "1" => true,
            "false" or "no" or "0" => false,
            _ => throw new ArgumentException($"Invalid boolean value '{value}' for '{key}'."),
        };

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
