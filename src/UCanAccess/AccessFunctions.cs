using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace UCanAccess;

/// <summary>
/// Registers Access/VBA built-in functions as SQLite scalar functions
/// (port of the UCanAccess function library).
/// </summary>
public static class AccessFunctions
{
    private static Func<DateTime> _clock = () => DateTime.Now;
    private static readonly CultureInfo AccessCulture = CultureInfo.GetCultureInfo("en-US");

    /// <summary>lets tests freeze the clock used by Now()/Date()/Time()</summary>
    public static void SetClock(Func<DateTime> clock) => _clock = clock;

    /// <summary>restores the normal system clock used by date/time functions</summary>
    public static void ResetClock() => _clock = () => DateTime.Now;

    /// <summary>registers the Access functions on the given SQLite connection</summary>
    public static void Register(SqliteConnection connection, SqliteConnection domainConnection)
    {
        // Access MONEY/NUMERIC values are mirrored as invariant decimal text.
        // Register the exact operators on both the query and domain-function
        // connections because DSum/DLookup execute subqueries on the latter.
        ExactDecimalSql.Register(connection);
        ExactDecimalSql.Register(domainConnection);

        // access_like is a pure regex match, so it is deterministic; this also lets
        // SQLite use it in ORDER BY / aggregate contexts.
        RegisterVar(connection, "access_like", a => AccessLikePattern(AsString(a[0]), AsString(a[1])), true);

        // null handling / boolean
        RegisterVar(connection, "nz", a => Nz(a[0], a.Length > 1 ? a[1] : ""));
        // SQLite treats ISNULL as a keyword, so the Access IsNull() function is
        // registered under a different name and rewritten by the translator
        RegisterVar(connection, "access_isnull", a => a[0] is null or DBNull ? 1L : 0L);

        // date/time
        RegisterVar(connection, "now", _ => FormatDate(_clock()));
        RegisterVar(connection, "date", _ => FormatDate(_clock().Date));
        RegisterVar(connection, "time", _ => FormatDate(new DateTime(1899, 12, 30).Add(_clock().TimeOfDay)));
        RegisterVar(connection, "datevalue", a => FormatDate(ToDate(a[0]).Date));
        RegisterVar(connection, "timevalue", a => FormatDate(new DateTime(1899, 12, 30).Add(ToDate(a[0]).TimeOfDay)));
        RegisterVar(connection, "cdate", a => FormatDate(ToDate(a[0])));
        RegisterVar(connection, "dateserial", a => FormatDate(new DateTime((int)ToLong(a[0]), (int)ToLong(a[1]), (int)ToLong(a[2]))));
        RegisterVar(connection, "timeserial", a => FormatDate(new DateTime(1899, 12, 30, (int)ToLong(a[0]), (int)ToLong(a[1]), (int)ToLong(a[2]))));
        RegisterVar(connection, "dateadd", a => FormatDate(DateAdd(AsString(a[0]), ToDouble(a[1]), ToDate(a[2]))));
        RegisterVar(connection, "datediff", a => DateDiff(AsString(a[0]), ToDate(a[1]), ToDate(a[2])));
        RegisterVar(connection, "datepart", a => DatePart(AsString(a[0]), ToDate(a[1]),
            a.Length > 2 ? ToLong(a[2]) : 1, a.Length > 3 ? ToLong(a[3]) : 1));
        RegisterVar(connection, "weekday", a => (long)Weekday(ToDate(a[0]), a.Length > 1 ? ToLong(a[1]) : 1));
        RegisterVar(connection, "weekdayname", WeekdayName);
        RegisterVar(connection, "monthname", MonthName);
        RegisterVar(connection, "year", a => (long)ToDate(a[0]).Year);
        RegisterVar(connection, "month", a => (long)ToDate(a[0]).Month);
        RegisterVar(connection, "day", a => (long)ToDate(a[0]).Day);
        RegisterVar(connection, "hour", a => (long)ToDate(a[0]).Hour);
        RegisterVar(connection, "minute", a => (long)ToDate(a[0]).Minute);
        RegisterVar(connection, "second", a => (long)ToDate(a[0]).Second);

        // string functions
        RegisterVar(connection, "instr", a => InStr(a));
        RegisterVar(connection, "instrrev", a => InStrRev(a));
        RegisterVar(connection, "mid", a => Mid(AsString(a[0]), (int)ToLong(a[1]), a.Length > 2 ? (int)ToLong(a[2]) : int.MaxValue));
        RegisterVar(connection, "left", a => Left(AsString(a[0]), (int)ToLong(a[1])));
        RegisterVar(connection, "right", a => Right(AsString(a[0]), (int)ToLong(a[1])));
        RegisterVar(connection, "asc", a => AsString(a[0]) is { Length: > 0 } s ? (long)s[0] : 0L);
        RegisterVar(connection, "chr", a => ((char)(int)ToLong(a[0])).ToString());
        RegisterVar(connection, "strconv", a => StrConv(AsString(a[0]) ?? "", (int)ToLong(a[1])));
        RegisterVar(connection, "strcomp", a => StrComp(AsString(a[0]) ?? "", AsString(a[1]) ?? ""));
        RegisterVar(connection, "strreverse", a => new string((AsString(a[0]) ?? "").Reverse().ToArray()));
        RegisterVar(connection, "ucase", a => (AsString(a[0]) ?? "").ToUpperInvariant());
        RegisterVar(connection, "lcase", a => (AsString(a[0]) ?? "").ToLowerInvariant());
        RegisterVar(connection, "trim", a => (AsString(a[0]) ?? "").Trim());
        RegisterVar(connection, "ltrim", a => (AsString(a[0]) ?? "").TrimStart());
        RegisterVar(connection, "rtrim", a => (AsString(a[0]) ?? "").TrimEnd());
        RegisterVar(connection, "space", a => new string(' ', (int)ToLong(a[0])));
        RegisterVar(connection, "string", a => new string((char)(int)ToLong(a[0]), (int)ToLong(a[1])));
        RegisterVar(connection, "len", a => (long)(AsString(a[0])?.Length ?? 0));
        RegisterVar(connection, "format", a => Format(a[0], a.Length > 1 ? AsString(a[1]) : null));

        // numeric
        RegisterVar(connection, "val", a => Val(AsString(a[0]) ?? ""));
        RegisterVar(connection, "int", a => (long)Math.Floor(ToDouble(a[0])));
        RegisterVar(connection, "fix", a => (long)Math.Truncate(ToDouble(a[0])));
        RegisterVar(connection, "sgn", a => Math.Sign(ToDouble(a[0])));
        RegisterVar(connection, "sqr", a => Math.Sqrt(ToDouble(a[0])));
        RegisterVar(connection, "abs", a => Math.Abs(ToDouble(a[0])), true);
        RegisterVar(connection, "sin", a => Math.Sin(ToDouble(a[0])), true);
        RegisterVar(connection, "cos", a => Math.Cos(ToDouble(a[0])), true);
        RegisterVar(connection, "tan", a => Math.Tan(ToDouble(a[0])), true);
        RegisterVar(connection, "asin", a => Math.Asin(ToDouble(a[0])), true);
        RegisterVar(connection, "acos", a => Math.Acos(ToDouble(a[0])), true);
        RegisterVar(connection, "exp", a => Math.Exp(ToDouble(a[0])), true);
        RegisterVar(connection, "log", a => Math.Log(ToDouble(a[0])), true);
        RegisterVar(connection, "log10", a => Math.Log10(ToDouble(a[0])), true);
        RegisterVar(connection, "str", a => StrAccess(ToDouble(a[0])));
        RegisterVar(connection, "rnd", a => Rnd(a));
        RegisterVar(connection, "isdate", a => TryParseDate(AsString(a[0])) ? 1L : 0L);
        RegisterVar(connection, "isnumeric", a => double.TryParse(AsString(a[0]), NumberStyles.Any, CultureInfo.InvariantCulture, out _) ? 1L : 0L);

        // type conversions
        RegisterVar(connection, "cstr", a => CStrAccess(a[0]));
        RegisterVar(connection, "cbool", a => ToBool(a[0]) ? 1L : 0L);
        RegisterVar(connection, "cint", a => (long)(int)Math.Round(ToDouble(a[0])));
        RegisterVar(connection, "clng", a => (long)Math.Round(ToDouble(a[0])));
        RegisterVar(connection, "cdbl", a => ToDouble(a[0]));
        RegisterVar(connection, "csng", a => ToDouble(a[0]));
        RegisterVar(connection, "ccur", a => ExactDecimal.TryParse(a[0], out ExactDecimal value)
            ? value.Rescale(4).ToString()
            : null);
        RegisterVar(connection, "cdec", a => ExactDecimal.TryParse(a[0], out ExactDecimal value)
            ? value.ToString()
            : null);
        RegisterVar(connection, "cvar", a => AsString(a[0]) ?? "");

        // flow
        RegisterVar(connection, "switch", a => Switch(a));

        // financial
        RegisterVar(connection, "pmt", a => Pmt(ToDouble(a[0]), ToDouble(a[1]), ToDouble(a[2]),
            a.Length > 3 ? ToDouble(a[3]) : 0, a.Length > 4 ? ToDouble(a[4]) : 0));
        RegisterVar(connection, "nper", a => Nper(ToDouble(a[0]), ToDouble(a[1]), ToDouble(a[2]),
            a.Length > 3 ? ToDouble(a[3]) : 0, a.Length > 4 ? ToDouble(a[4]) : 0));
        RegisterVar(connection, "pv", a => Pv(ToDouble(a[0]), ToDouble(a[1]), ToDouble(a[2]),
            a.Length > 3 ? ToDouble(a[3]) : 0, a.Length > 4 ? ToDouble(a[4]) : 0));
        RegisterVar(connection, "fv", a => Fv(ToDouble(a[0]), ToDouble(a[1]), ToDouble(a[2]),
            a.Length > 3 ? ToDouble(a[3]) : 0, a.Length > 4 ? ToDouble(a[4]) : 0));
        RegisterVar(connection, "sln", a => Sln(ToDouble(a[0]), ToDouble(a[1]), ToDouble(a[2])));
        RegisterVar(connection, "syd", a => Syd(ToDouble(a[0]), ToDouble(a[1]), ToDouble(a[2]), ToDouble(a[3])));
        RegisterVar(connection, "ddb", a => Ddb(ToDouble(a[0]), ToDouble(a[1]), ToDouble(a[2]), ToDouble(a[3]), a.Length > 4 ? ToDouble(a[4]) : 2.0));
        RegisterVar(connection, "ipmt", a => Ipmt(ToDouble(a[0]), ToDouble(a[1]), ToDouble(a[2]),
            ToDouble(a[3]), a.Length > 4 ? ToDouble(a[4]) : 0, a.Length > 5 ? ToDouble(a[5]) : 0));
        RegisterVar(connection, "ppmt", a => Ppmt(ToDouble(a[0]), ToDouble(a[1]), ToDouble(a[2]),
            ToDouble(a[3]), a.Length > 4 ? ToDouble(a[4]) : 0, a.Length > 5 ? ToDouble(a[5]) : 0));
        RegisterVar(connection, "rate", a => Rate(ToDouble(a[0]), ToDouble(a[1]), ToDouble(a[2]),
            a.Length > 3 ? ToDouble(a[3]) : 0, a.Length > 4 ? ToDouble(a[4]) : 0,
            a.Length > 5 ? ToDouble(a[5]) : 0.1));

        // domain aggregate functions: they run their own subquery on a second
        // connection to the same in-memory mirror (see Mirror.DomainConnection)
        RegisterVar(connection, "dcount", a => DomainAggregate(domainConnection, a, "Count"));
        RegisterVar(connection, "dsum", a => DomainAggregate(domainConnection, a, "Sum"));
        RegisterVar(connection, "davg", a => DomainAggregate(domainConnection, a, "Avg"));
        RegisterVar(connection, "dmin", a => DomainAggregate(domainConnection, a, "Min"));
        RegisterVar(connection, "dmax", a => DomainAggregate(domainConnection, a, "Max"));
        RegisterVar(connection, "dfirst", a => DomainRow(domainConnection, a, first: true));
        RegisterVar(connection, "dlast", a => DomainRow(domainConnection, a, last: true));
        RegisterVar(connection, "dlookup", a => DomainLookup(domainConnection, a));

        // misc
        RegisterVar(connection, "atn", a => Math.Atan(ToDouble(a[0])), true);
        // Access Round(x, n) = floor(x*10^n + 0.5) / 10^n (Java Math.round semantics),
        // which differs from SQLite's builtin for negative half values.
        RegisterVar(connection, "round", a => RoundAccess(ToDouble(a[0]), ToDouble(a.Length > 1 ? a[1] : 0)), true);
        RegisterVar(connection, "partition", a => Partition(ToLong(a[0]), ToLong(a[1]), ToLong(a[2]), ToLong(a[3])), true);

        // money columns render with 4 decimal places when concatenated ('&')
        RegisterVar(connection, "money_str", a => MoneyStr(a[0]), true);

        // HSQLDB AVG() of integer values truncates to an integer; SQLite returns a real.
        // A registered aggregate shadows the SQLite builtin and mirrors HSQLDB's typing.
        connection.CreateAggregate<AvgState, object?>("avg", new AvgState(), AvgStep, AvgResult, true);
        connection.CreateAggregate<StatisticsState, object?>("stdev", new StatisticsState(), StatisticsStep,
            state => StatisticsResult(state, sample: true, squareRoot: true), true);
        connection.CreateAggregate<StatisticsState, object?>("stdevp", new StatisticsState(), StatisticsStep,
            state => StatisticsResult(state, sample: false, squareRoot: true), true);
        connection.CreateAggregate<StatisticsState, object?>("var", new StatisticsState(), StatisticsStep,
            state => StatisticsResult(state, sample: true, squareRoot: false), true);
        connection.CreateAggregate<StatisticsState, object?>("varp", new StatisticsState(), StatisticsStep,
            state => StatisticsResult(state, sample: false, squareRoot: false), true);
        connection.CreateAggregate<FirstLastState, object?>("first", new FirstLastState(), FirstLastStep,
            state => state?.HasValue == true ? state.First : null, true);
        connection.CreateAggregate<FirstLastState, object?>("last", new FirstLastState(), FirstLastStep,
            state => state?.HasValue == true ? state.Last : null, true);
    }

    /// <summary>
    /// Registers a connection-local scalar function. The callback receives the
    /// values in the same order as the SQL call. Use <paramref name="arity"/>
    /// set to -1 for a variable number of arguments.
    /// </summary>
    /// <param name="connection">The SQLite connection on which to register the function.</param>
    /// <param name="name">SQL function name; ASCII letters, digits and underscore are accepted.</param>
    /// <param name="arity">Number of arguments, or -1 for a variable number.</param>
    /// <param name="function">Function implementation.</param>
    /// <param name="deterministic">Whether the result depends only on its arguments.</param>
    public static void RegisterFunction(SqliteConnection connection, string name, int arity,
        Func<IReadOnlyList<object?>, object?> function, bool deterministic = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(function);
        if (arity < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(arity), arity, "Arity must be -1 or greater.");
        }
        if (!Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            throw new ArgumentException("Function names must start with a letter or underscore and contain only ASCII letters, digits or underscores.", nameof(name));
        }

        connection.CreateFunction<object?, object?>(name, arity == -1 ? null : arity,
            (_, args) => function(args), deterministic);
    }

    private static void RegisterVar(SqliteConnection connection, string name, Func<object?[], object?> function, bool deterministic = false)
    {
        connection.CreateFunction<object?, object?>(name, null, (_, args) => function(args), deterministic);
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private static string? AsString(object? value)
        => value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

    private static double ToDouble(object? value)
        => value switch
        {
            null or DBNull => 0,
            byte b => b,
            sbyte b => b,
            short n => n,
            ushort n => n,
            int n => n,
            uint n => n,
            long n => n,
            ulong n => n,
            float f => f,
            double d => d,
            decimal m => (double)m,
            bool b => b ? 1 : 0,
            string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double d) => d,
            string s when TryParseDate(s, out DateTime dt) => dt.Ticks,
            _ => 0,
        };

    private static long ToLong(object? value) => (long)Math.Round(ToDouble(value));

    private static decimal ToDecimal(object? value) => value switch
    {
        null or DBNull => 0,
        decimal m => m,
        _ => (decimal)ToDouble(value),
    };

    private static bool ToBool(object? value) => value switch
    {
        null or DBNull => false,
        bool b => b,
        byte b => b != 0,
        short n => n != 0,
        int n => n != 0,
        long n => n != 0,
        double d => d != 0,
        string s when TryParseDate(s, out DateTime dt) => dt != DateTime.MinValue,
        _ => ToDouble(value) != 0,
    };

    private static string FormatDate(DateTime dt)
        => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
        "M/d/yyyy H:mm:ss", "M/d/yyyy h:mm:ss tt", "M/d/yyyy h:mm tt", "M/d/yyyy H:mm", "M/d/yyyy",
        "d/M/yyyy H:mm:ss", "d/M/yyyy", "MM/dd/yyyy HH:mm:ss.fff",
        "O", "s",
    };

    private static bool TryParseDate(string? s, out DateTime dt)
    {
        if (s == null)
        {
            dt = default;
            return false;
        }
        return DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out dt);
    }

    private static bool TryParseDate(string? s) => TryParseDate(s, out _);

    private static DateTime ToDate(object? value) => value switch
    {
        DateTime dt => dt,
        long l => DateTime.FromOADate(l),
        double d => DateTime.FromOADate(d),
        decimal m => DateTime.FromOADate((double)m),
        string s when TryParseDate(s, out DateTime dt) => dt,
        string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double d) => DateTime.FromOADate(d),
        _ => DateTime.MinValue,
    };

    // ------------------------------------------------------------------
    // functions
    // ------------------------------------------------------------------

    private static object? Nz(object? value, object? ifNull)
        => value is null or DBNull ? ifNull : value;

    private static DateTime DateAdd(string? interval, double number, DateTime date)
    {
        try
        {
            return (interval ?? "").ToUpperInvariant() switch
            {
                "YYYY" => date.AddYears((int)number),
                "Q" => date.AddMonths((int)(number * 3)),
                "M" => date.AddMonths((int)number),
                "Y" or "D" or "W" => date.AddDays(number),
                "WW" => date.AddDays(number * 7),
                "H" => date.AddHours(number),
                "N" => date.AddMinutes(number),
                "S" => date.AddSeconds(number),
                _ => date,
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return date;
        }
    }

    private static long DateDiff(string? interval, DateTime d1, DateTime d2)
    {
        // Port of net.ucanaccess.converters.Functions.dateDiff: the interval is
        // computed between the two timestamps ordered as (earlier, later) and the
        // result is negated when d2 < d1. Day/hour/minute/second arithmetic uses
        // rounding that mirrors the Java Math.rint/round behavior.
        bool negative = d2 < d1;
        DateTime start = negative ? d2 : d1;
        DateTime end = negative ? d1 : d2;
        TimeSpan span = end - start;
        double result = (interval ?? "").ToUpperInvariant() switch
        {
            "YYYY" => end.Year - start.Year,
            "Q" => 4 * (end.Year - start.Year) + (end.Month - start.Month) / 3,
            "M" => 12 * (end.Year - start.Year) + (end.Month - start.Month),
            "Y" or "D" => Rint(span.TotalDays),
            "W" or "WW" => Math.Floor(span.TotalDays / 7.0),
            "H" => Math.Floor(span.TotalHours + 0.5),
            "N" => Rint(span.TotalMinutes),
            "S" => Rint(span.TotalSeconds),
            _ => 0,
        };
        return (long)(negative ? -result : result);
    }

    /// <summary>Java Math.rint: round half to even.</summary>
    private static double Rint(double d) => Math.Round(d, MidpointRounding.ToEven);

    private static long DatePart(string? interval, DateTime date, long firstDay = 1, long firstWeek = 1)
        => (interval ?? "").ToUpperInvariant() switch
        {
            "YYYY" => date.Year,
            "Q" => (date.Month - 1) / 3 + 1,
            "M" => date.Month,
            "D" => date.Day,
            "Y" => date.DayOfYear,
            "W" => Weekday(date, firstDay),
            "WW" => WeekOfYear(date, firstDay, firstWeek),
            "H" => date.Hour,
            "N" => date.Minute,
            "S" => date.Second,
            _ => 0,
        };

    private static int Weekday(DateTime date, long firstDay = 1)
    {
        // Access: Sunday = 1 ... Saturday = 7.  With a non-default first day,
        // the returned value is rotated so that that day is 1.
        int first = NormalizeFirstDay(firstDay);
        return ((int)date.DayOfWeek + 1 - first + 7) % 7 + 1;
    }

    private static string WeekdayName(object?[] args)
    {
        int weekday = (int)ToLong(args[0]);
        if (weekday is < 1 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Weekday must be between 1 and 7.");
        }
        long firstDay = args.Length > 2 ? ToLong(args[2]) : 1;
        int first = NormalizeFirstDay(firstDay);
        int dayIndex = (first - 1 + weekday - 1) % 7;
        string name = AccessCulture.DateTimeFormat.DayNames[dayIndex];
        return args.Length > 1 && ToBool(args[1]) ? name[..3] : name;
    }

    private static string MonthName(object?[] args)
    {
        int month = (int)ToLong(args[0]);
        if (month is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Month must be between 1 and 12.");
        }
        string name = AccessCulture.DateTimeFormat.MonthNames[month - 1];
        return args.Length > 1 && ToBool(args[1]) ? name[..3] : name;
    }

    private static int WeekOfYear(DateTime date, long firstDay, long firstWeek)
    {
        DayOfWeek day = (DayOfWeek)(NormalizeFirstDay(firstDay) - 1);
        CalendarWeekRule rule = firstWeek switch
        {
            2 => CalendarWeekRule.FirstFourDayWeek,
            3 => CalendarWeekRule.FirstFullWeek,
            _ => CalendarWeekRule.FirstDay,
        };
        return AccessCulture.Calendar.GetWeekOfYear(date, rule, day);
    }

    private static int NormalizeFirstDay(long firstDay)
        => firstDay is >= 1 and <= 7 ? (int)firstDay : 1;

    private static long InStr(object?[] a)
    {
        int start = a.Length > 2 ? (int)ToLong(a[0]) : 1;
        string s = AsString(a[a.Length > 2 ? 1 : 0]) ?? "";
        string find = AsString(a[a.Length > 2 ? 2 : 1]) ?? "";
        if (start < 1 || start > s.Length + 1)
        {
            return 0;
        }
        if (find.Length == 0)
        {
            return start;
        }
        int idx = s.IndexOf(find, start - 1, StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? idx + 1 : 0;
    }

    private static long InStrRev(object?[] a)
    {
        string s = AsString(a[0]) ?? "";
        string find = AsString(a[1]) ?? "";
        int start = a.Length > 2 ? (int)ToLong(a[2]) : s.Length;
        if (start < 1)
        {
            return 0;
        }
        int idx = s.LastIndexOf(find, Math.Min(start, s.Length) - 1, StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? idx + 1 : 0;
    }

    private static string Mid(string? s, int start, int length)
    {
        s ??= "";
        if (start < 1 || start > s.Length)
        {
            return "";
        }
        int idx = start - 1;
        int len = Math.Min(length, s.Length - idx);
        return s.Substring(idx, len);
    }

    private static string Left(string? s, int length)
    {
        s ??= "";
        return s.Length <= length ? s : s[..length];
    }

    private static string Right(string? s, int length)
    {
        s ??= "";
        return s.Length <= length ? s : s[^length..];
    }
    private static string StrConv(string? s, int conversion)
    {
        // 1 = uppercase, 2 = lowercase, 3 = proper case
        string str = s ?? "";
        return conversion switch
        {
            1 => str.ToUpperInvariant(),
            2 => str.ToLowerInvariant(),
            3 => ToProperCase(str),
            _ => str,
        };
    }

    private static string ToProperCase(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool newWord = true;
        foreach (char c in s)
        {
            sb.Append(newWord ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
            newWord = char.IsWhiteSpace(c);
        }
        return sb.ToString();
    }

    private static long StrComp(string? s1, string? s2)
        => string.Compare(s1, s2, StringComparison.OrdinalIgnoreCase) switch
        {
            < 0 => -1,
            > 0 => 1,
            _ => 0,
        };

    private static double Val(string s)
    {
        var match = Regex.Match(s ?? "", @"^\s*[+-]?(\d+\.?\d*|\.\d+)([Ee][+-]?\d+)?");
        return match.Success && double.TryParse(match.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d)
            ? d
            : 0;
    }

    /// <summary>Access/VBA Rnd: LCG with 24-bit modulus.</summary>
    private static double Rnd(object?[] a)
    {
        const long modulus = 1L << 24;
        const long multiplier = 1140671485;
        const long increment = 12820163;
        long seed;
        if (a.Length > 0 && a[0] != null && a[0] is not DBNull)
        {
            seed = (long)ToLong(a[0]) & 0xFFFFFF;
        }
        else
        {
            seed = _rndState;
        }
        seed = (seed * multiplier + increment) % modulus;
        _rndState = seed;
        return (double)seed / modulus;
    }

    private static long _rndState = 0;

    private static string Format(object? value, string? format)
    {
        string? s = AsString(value);
        if (s == null)
        {
            return "";
        }
        if (format == null)
        {
            return s;
        }
        if (TryParseDate(s, out DateTime dateValue))
        {
            switch (format.ToUpperInvariant())
            {
                case "GENERAL DATE" or "GENERALDATE":
                    return dateValue.ToString("G", CultureInfo.InvariantCulture);
                case "LONG DATE" or "LONGDATE":
                    return dateValue.ToString("D", CultureInfo.InvariantCulture);
                case "MEDIUM DATE" or "MEDIUMDATE":
                    return dateValue.ToString("dd-MMM-yy", CultureInfo.InvariantCulture);
                case "SHORT DATE" or "SHORTDATE":
                    return dateValue.ToString("M/d/yyyy", CultureInfo.InvariantCulture);
                case "LONG TIME" or "LONGTIME" or "MEDIUM TIME" or "MEDIUMTIME" or "SHORT TIME" or "SHORTTIME":
                    return dateValue.ToString("h:mm:ss tt", CultureInfo.InvariantCulture);
                default:
                    return FormatDateTime(dateValue, format);
            }
        }
        return FormatNumber(s, format);
    }

    private static string FormatDateTime(DateTime dt, string format)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < format.Length)
        {
            char c = format[i];
            if (c == '\\' && i + 1 < format.Length)
            {
                sb.Append(format[i + 1]);
                i += 2;
                continue;
            }
            if (c is 'd' or 'm' or 'y' or 'h' or 'n' or 's')
            {
                int run = 0;
                while (i + run < format.Length && format[i + run] == c)
                {
                    run++;
                }
                sb.Append(c switch
                {
                    'd' => run switch
                    {
                        1 => dt.Day.ToString(),
                        2 => dt.Day.ToString("00"),
                        3 => dt.DayOfYear.ToString("000"), // Access 'ddd' = day of year
                        _ => dt.ToString("dddd", CultureInfo.InvariantCulture),
                    },
                    'm' => run switch { 1 => dt.Month.ToString(), 2 => dt.Month.ToString("00"), 3 => dt.ToString("MMM", CultureInfo.InvariantCulture), _ => dt.ToString("MMMM", CultureInfo.InvariantCulture) },
                    'y' => run <= 2 ? dt.Year.ToString("00") : dt.Year.ToString("0000"),
                    'h' => run switch { 1 => Hour12(dt).ToString(), _ => Hour12(dt).ToString("00") },
                    'n' => run switch { 1 => dt.Minute.ToString(), _ => dt.Minute.ToString("00") },
                    's' => run switch { 1 => dt.Second.ToString(), _ => dt.Second.ToString("00") },
                    _ => new string(c, run),
                });
                i += run;
                continue;
            }
            if (c is 't' or 'a' or 'p')
            {
                // AM/PM markers
                int run = 0;
                while (i + run < format.Length && format[i + run] == c)
                {
                    run++;
                }
                string ampm = dt.Hour < 12 ? "AM" : "PM";
                sb.Append(run switch
                {
                    1 => ampm[0].ToString(),
                    2 => ampm,
                    _ => ampm.ToLowerInvariant(),
                });
                i += run;
                continue;
            }
            if (c is ':' or '/' or ' ' or '-' or '.' or ',')
            {
                sb.Append(c);
                i++;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static int Hour12(DateTime dt)
    {
        int h = dt.Hour % 12;
        return h == 0 ? 12 : h;
    }

    private static string FormatNumber(string s, string format)
    {
        if (format == null || !double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
        {
            return s;
        }
        string pattern = format.ToUpperInvariant() switch
        {
            "GENERAL NUMBER" => "0.###############",
            "FIXED" => "0.00",
            "STANDARD" => "#,##0.##",
            "CURRENCY" => "$#,##0.00",
            "PERCENT" => "0.00%",
            "YES/NO" => d != 0 ? "Yes" : "No",
            "TRUE/FALSE" => d != 0 ? "True" : "False",
            "ON/OFF" => d != 0 ? "On" : "Off",
            "SCIENTIFIC" => "0.00E+00",
            _ => format,
        };
        try
        {
            return d.ToString(pattern, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return s;
        }
    }

    private static object? Switch(object?[] a)
    {
        for (int i = 0; i + 1 < a.Length; i += 2)
        {
            if (ToBool(a[i]))
            {
                return a[i + 1];
            }
        }
        return null;
    }

    // ------------------------------------------------------------------
    // domain aggregate functions
    // ------------------------------------------------------------------

    private static string QuoteDomain(string domain)
    {
        string name = domain.Trim();
        if (name.Length >= 2 && name[0] is '[' or '`' && name[^1] is ']' or '`')
        {
            name = name[1..^1];
        }
        return SqlNames.Quote(name);
    }

    private static object? DomainAggregate(SqliteConnection domainConnection, object?[] a, string aggregate)
    {
        string? expr = AsString(a[0]);
        string domain = AsString(a[1]) ?? "";
        if (expr == null || domain.Length == 0)
        {
            return aggregate == "Count" ? 0L : null;
        }
        string? criteria = a.Length > 2 ? AsString(a[2]) : null;
        string where = string.IsNullOrEmpty(criteria) ? "" : $" WHERE {criteria}";
        string sql = $"SELECT {aggregate}({expr}) FROM {QuoteDomain(domain)}{where}";
        using var cmd = domainConnection.CreateCommand();
        cmd.CommandText = TranslateDomain(sql);
        using var reader = cmd.ExecuteReader();
        if (reader.Read() && !reader.IsDBNull(0))
        {
            object value = reader.GetValue(0);
            // Domain functions are scalar Access/VBA functions. Preserve the
            // historical CLR surface (double for numeric domain results) even
            // though the mirror itself keeps the source decimal as exact text.
            return value is string text && ExactDecimal.TryParse(text, out ExactDecimal exact)
                ? double.Parse(exact.ToString(), CultureInfo.InvariantCulture)
                : value;
        }
        return aggregate == "Count" ? 0L : null;
    }

    private static object? DomainRow(SqliteConnection domainConnection, object?[] a, bool first = false, bool last = false)
    {
        string? expr = AsString(a[0]);
        string domain = AsString(a[1]) ?? "";
        if (expr == null || domain.Length == 0)
        {
            return null;
        }
        string? criteria = a.Length > 2 ? AsString(a[2]) : null;
        string where = string.IsNullOrEmpty(criteria) ? "" : $" WHERE {criteria}";
        string order = last ? " ORDER BY rowid DESC" : "";
        string sql = $"SELECT {expr} FROM {QuoteDomain(domain)}{where}{order} LIMIT 1";
        using var cmd = domainConnection.CreateCommand();
        cmd.CommandText = TranslateDomain(sql);
        using var reader = cmd.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0) ? reader.GetValue(0) : null;
    }

    private static object? DomainLookup(SqliteConnection domainConnection, object?[] a)
    {
        string? expr = AsString(a[0]);
        string domain = AsString(a[1]) ?? "";
        if (expr == null || domain.Length == 0)
        {
            return null;
        }
        string? criteria = a.Length > 2 ? AsString(a[2]) : null;
        string where = string.IsNullOrEmpty(criteria) ? "" : $" WHERE {criteria}";
        string sql = $"SELECT {expr} FROM {QuoteDomain(domain)}{where} LIMIT 2";
        using var cmd = domainConnection.CreateCommand();
        cmd.CommandText = TranslateDomain(sql);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        return reader.IsDBNull(0) ? null : reader.GetValue(0);
    }

    private static string TranslateDomain(string sql)
        => AccessSqlTranslator.Translate(sql);

    /// <summary>Access Round(x, n): floor(x * 10^n + 0.5) / 10^n (Java Math.round, half toward +infinity).</summary>
    private static double RoundAccess(double value, double scale)
    {
        double p = Math.Pow(10.0, scale);
        return Math.Floor(value * p + 0.5) / p;
    }

    /// <summary>Access Partition(number, start, stop, interval): "low:high" bucket range with padding.</summary>
    private static string Partition(long number, long start, long stop, long interval)
    {
        // port of net.ucanaccess.converters.Functions.partition
        if (interval <= 0 || stop < start)
        {
            return "";
        }
        int width = (stop + 1).ToString().Length;
        if (number < start)
        {
            return PadLeft(0, width) + ":" + PadLeft(start - 1, width);
        }
        if (number > stop)
        {
            return PadLeft(stop + 1, width) + ":" + PadLeft(0, width);
        }
        for (long low = start; low <= stop; low += interval)
        {
            if (number >= low && number < low + interval)
            {
                string lowStr = PadLeft((long)Math.Ceiling(low - 1e-8), width);
                string highStr = low + interval > stop
                    ? PadLeft(stop, width)
                    : PadLeft((long)Math.Floor(low + interval - 1e-8), width);
                return lowStr + ":" + highStr;
            }
        }
        return "";
    }

    /// <summary>padLeft(v, w): "" for v &lt;= 0, otherwise v right-aligned in w characters.</summary>
    private static string PadLeft(long value, int width)
        => (value > 0 ? value.ToString() : "").PadLeft(width);

    /// <summary>Access Str(x): no digit grouping, with a leading space for positive values.</summary>
    private static string StrAccess(double value)
    {
        string s = value.ToString("0.##########", CultureInfo.InvariantCulture);
        return value > 0 ? " " + s : s;
    }

    /// <summary>
    /// Access CStr(): converts a value to its text form using en-US digit grouping.
    /// Mirrors net.ucanaccess.converters.Functions.cstrImpl, which formats the value's
    /// string representation with the JVM default (en-US) DecimalFormat: grouping for
    /// the integer part, all significant fraction digits, and no leading zero for |x| &lt; 1
    /// ("0.5" → ".5"). Booleans render as "true"/"false".
    /// </summary>
    private static string CStrAccess(object? value)
    {
        if (value is null or DBNull)
        {
            return "";
        }
        return value switch
        {
            bool b => b ? "true" : "false",
            string s => s,
            DateTime dt => FormatDate(dt),
            _ => FormatGroupedNumber(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        };
    }

    private static string FormatGroupedNumber(double d)
    {
        string s = d.ToString("R", CultureInfo.InvariantCulture);
        bool neg = s.StartsWith('-');
        string body = neg ? s[1..] : s;
        if (body.Contains('E', StringComparison.OrdinalIgnoreCase))
        {
            return (neg ? "-" : "") + body;
        }
        int dot = body.IndexOf('.');
        string intPart = dot < 0 ? body : body[..dot];
        string frac = dot < 0 ? "" : body[dot..];
        string grouped = GroupDigits(intPart);
        if (intPart == "0" && frac.Length > 0)
        {
            grouped = "";
        }
        return (neg ? "-" : "") + grouped + frac;
    }

    private static string GroupDigits(string digits)
    {
        var sb = new StringBuilder(digits.Length + digits.Length / 3);
        for (int i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0)
            {
                sb.Append(',');
            }
            sb.Append(digits[i]);
        }
        return sb.ToString();
    }

    /// <summary>Access money displayed in a concatenation keeps 4 decimal places.</summary>
    private static object? MoneyStr(object? value)
    {
        if (value is null or DBNull)
        {
            return null;
        }
        return ExactDecimal.Parse(value).ToFixedString(4);
    }

    /// <summary>Accumulator for the custom AVG aggregate (HSQLDB integer truncation).</summary>
    private sealed class AvgState
    {
        public decimal Sum;
        public long Count;
        public bool AllInteger = true;
    }

    private static AvgState AvgStep(AvgState? state, object?[] args)
    {
        // The seed object passed to CreateAggregate is reused for every evaluation,
        // so never mutate it: work on a fresh copy each step.
        var next = new AvgState();
        if (state != null)
        {
            next.Sum = state.Sum;
            next.Count = state.Count;
            next.AllInteger = state.AllInteger;
        }
        foreach (object? arg in args)
        {
            if (arg is null or DBNull)
            {
                continue;
            }
            next.Count++;
            if (arg is not (byte or sbyte or short or ushort or int or uint or long or ulong))
            {
                next.AllInteger = false;
            }
            next.Sum += Convert.ToDecimal(arg, CultureInfo.InvariantCulture);
        }
        return next;
    }

    private static object? AvgResult(AvgState? state)
    {
        if (state == null || state.Count == 0)
        {
            return null;
        }
        if (state.AllInteger)
        {
            return (long)(state.Sum / state.Count);
        }
        return (double)(state.Sum / state.Count);
    }

    /// <summary>Accumulator for Access STDEV/VAR aggregates.</summary>
    private sealed class StatisticsState
    {
        public long Count;
        public double Sum;
        public double SumOfSquares;
    }

    private static StatisticsState StatisticsStep(StatisticsState? state, object?[] args)
    {
        var next = new StatisticsState();
        if (state != null)
        {
            next.Count = state.Count;
            next.Sum = state.Sum;
            next.SumOfSquares = state.SumOfSquares;
        }
        foreach (object? arg in args)
        {
            if (arg is null or DBNull)
            {
                continue;
            }
            double value = ToDouble(arg);
            next.Count++;
            next.Sum += value;
            next.SumOfSquares += value * value;
        }
        return next;
    }

    private static object? StatisticsResult(StatisticsState? state, bool sample, bool squareRoot)
    {
        if (state == null || state.Count == 0 || (sample && state.Count < 2))
        {
            return null;
        }
        double denominator = sample ? state.Count - 1 : state.Count;
        double variance = (state.SumOfSquares - (state.Sum * state.Sum / state.Count)) / denominator;
        // Floating point cancellation can produce a tiny negative value for a
        // constant set. Access returns zero in that case.
        variance = Math.Max(0, variance);
        return squareRoot ? Math.Sqrt(variance) : variance;
    }

    private sealed class FirstLastState
    {
        public bool HasValue;
        public object? First;
        public object? Last;
    }

    private static FirstLastState FirstLastStep(FirstLastState? state, object?[] args)
    {
        var next = new FirstLastState();
        if (state != null)
        {
            next.HasValue = state.HasValue;
            next.First = state.First;
            next.Last = state.Last;
        }
        foreach (object? arg in args)
        {
            if (arg is null or DBNull)
            {
                continue;
            }
            if (!next.HasValue)
            {
                next.HasValue = true;
                next.First = arg;
            }
            next.Last = arg;
        }
        return next;
    }

    // ------------------------------------------------------------------
    // Access LIKE
    // ------------------------------------------------------------------

    internal static bool AccessLikePattern(string? value, string? pattern)
    {
        if (pattern == null)
        {
            return false;
        }
        if (value == null)
        {
            return false;
        }
        string regex = ConvertLikePattern(pattern);
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string ConvertLikePattern(string pattern)
    {
        var sb = new StringBuilder();
        sb.Append('^');
        int i = 0;
        int n = pattern.Length;
        while (i < n)
        {
            char c = pattern[i];
            if (c is '*' or '%')
            {
                sb.Append(".*");
                i++;
            }
            else if (c is '?' or '_')
            {
                sb.Append('.');
                i++;
            }
            else if (c == '#')
            {
                sb.Append(@"\d");
                i++;
            }
            else if (c == '[')
            {
                int j = i + 1;
                bool neg = false;
                if (j < n && (pattern[j] is '!' or '^'))
                {
                    neg = true;
                    j++;
                }
                int close = pattern.IndexOf(']', j);
                if (close < 0)
                {
                    sb.Append(Regex.Escape("["));
                    i++;
                    continue;
                }
                sb.Append('[');
                if (neg)
                {
                    sb.Append('^');
                }
                for (int k = j; k < close; k++)
                {
                    char ch = pattern[k];
                    if (ch == '\\')
                    {
                        sb.Append(@"\\");
                    }
                    else if (ch == ']')
                    {
                        sb.Append(@"\]");
                    }
                    else if (ch == '^' && k > j)
                    {
                        sb.Append(@"\^");
                    }
                    else if (ch == '-' && (k == j || k == close - 1))
                    {
                        sb.Append(@"\-");
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                }
                sb.Append(']');
                i = close + 1;
            }
            else
            {
                int start = i;
                while (i < n && pattern[i] is not ('*' or '%' or '?' or '_' or '#' or '['))
                {
                    i++;
                }
                sb.Append(Regex.Escape(pattern[start..i]));
            }
        }
        sb.Append('$');
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // financial
    // ------------------------------------------------------------------

    private static double Pmt(double rate, double nper, double pv, double fv = 0, double type = 0)
    {
        if (rate == 0)
        {
            return -(pv + fv) / nper;
        }
        double r = rate;
        double d = Math.Pow(1 + r, nper);
        return -(pv * d + fv) * r / ((1 + r * type) * (d - 1));
    }

    private static double Nper(double rate, double pmt, double pv, double fv = 0, double type = 0)
    {
        if (rate == 0)
        {
            return -(pv + fv) / pmt;
        }
        double r = rate;
        double payment = pmt * (1 + r * type);
        return Math.Log((fv * r + payment) / (pv * r + payment)) / Math.Log(1 + r);
    }

    private static double Pv(double rate, double nper, double pmt, double fv = 0, double type = 0)
    {
        if (rate == 0)
        {
            return -pmt * nper - fv;
        }
        double r = rate;
        double d = Math.Pow(1 + r, nper);
        return -(pmt * (1 + r * type) * (d - 1) / r + fv) / d;
    }

    private static double Fv(double rate, double nper, double pmt, double pv = 0, double type = 0)
    {
        if (rate == 0)
        {
            return -(pv + pmt * nper);
        }
        double r = rate;
        double d = Math.Pow(1 + r, nper);
        return -(pv * d + pmt * (1 + r * type) * (d - 1) / r);
    }

    private static double Sln(double cost, double salvage, double life)
        => (cost - salvage) / life;

    private static double Syd(double cost, double salvage, double life, double period)
    {
        double syd = life * (life + 1) / 2.0;
        return (cost - salvage) * (life - period + 1) / syd;
    }

    private static double Ddb(double cost, double salvage, double life, double period, double factor)
    {
        double depreciation = 0;
        double currentValue = cost;
        for (double p = 1; p <= period; p++)
        {
            depreciation = Math.Min(currentValue - salvage, currentValue * factor / life);
            currentValue -= depreciation;
        }
        return depreciation;
    }

    private static double Ipmt(double rate, double per, double nper, double pv, double fv = 0, double type = 0)
    {
        double pmt = Pmt(rate, nper, pv, fv, type);
        if (rate == 0 || (type != 0 && per == 1))
        {
            return 0;
        }
        double r = rate;
        double before = pv * Math.Pow(1 + r, per - 1)
                        + pmt * (1 + r * type) * (Math.Pow(1 + r, per - 1) - 1) / r;
        return -before * r;
    }

    private static double Ppmt(double rate, double per, double nper, double pv, double fv = 0, double type = 0)
        => Pmt(rate, nper, pv, fv, type) - Ipmt(rate, per, nper, pv, fv, type);

    private static double Rate(double nper, double pmt, double pv, double fv = 0, double type = 0, double guess = 0.1)
    {
        // Solve the same cash-flow equation as the Java implementation using
        // Newton iteration, with a guarded bisection fallback.
        if (pmt == 0)
        {
            return 0;
        }

        double r = guess;
        for (int i = 0; i < 100; i++)
        {
            double f = Fv(r, nper, pmt, pv, type) - fv;
            if (Math.Abs(f) < 1e-10)
            {
                return r;
            }
            double h = Math.Max(1e-7, Math.Abs(r) * 1e-5);
            double derivative = (Fv(r + h, nper, pmt, pv, type) - Fv(r - h, nper, pmt, pv, type)) / (2 * h);
            if (!double.IsFinite(derivative) || Math.Abs(derivative) < 1e-14)
            {
                break;
            }
            double next = r - f / derivative;
            if (!double.IsFinite(next) || next <= -0.999999999)
            {
                break;
            }
            r = next;
        }

        return r;
    }
}
