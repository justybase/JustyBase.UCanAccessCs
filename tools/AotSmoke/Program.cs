// NativeAOT/trim smoke host for JustyBase.UCanAccessCs.
//
// Exercises the PublishAot closure that unit tests (xunit, not AOT-clean)
// cannot cover: embedded template/index-code resources, the Access SQL
// tokenizer/translator (Superpower graph), SQLite UDF/aggregate/collation
// registration, MirrorReader conversions (GetFieldValue<T>/GetFieldType),
// and the AccessCrypto opener. Fails with a non-zero exit code on any
// mismatch so CI can gate on the published (native) binary.
//
// Run locally with:
//   dotnet publish tools/AotSmoke/AotSmoke.csproj -c Release -r win-x64 -p:PublishAot=true
//   ./artifacts/aot-smoke/AotSmoke.exe  (or the win-x64 publish output)

using System.Data.Common;
using UCanAccess;
using UCanAccess.AccessCrypto;
using UCanAccess.File;

static void AddParam(DbCommand cmd, string name, object? value)
{
    var p = cmd.CreateParameter();
    p.ParameterName = name;
    p.Value = value ?? DBNull.Value;
    cmd.Parameters.Add(p);
}

static void Check(bool condition, string name)
{
    Console.WriteLine((condition ? "  ok   " : "  FAIL ") + name);
    if (!condition)
    {
        Environment.ExitCode = 1;
    }
}

string workDir = Path.Combine(Path.GetTempPath(), "ucanaccess_aot_smoke_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workDir);
string dbPath = Path.Combine(workDir, "smoke.mdb");
try
{
    // 1. Embedded database template resource (trim-visible literal map).
    using (Database.Create(dbPath, version: "2003"))
    {
    }
    Check(File.Exists(dbPath), "Database.Create template resource");

    using (var conn = new UCanAccessConnection($"Data Source={dbPath};Read Only=false"))
    {
        // Custom scalar UDF registered before Open (roots RegisterFunction path).
        conn.RegisterFunction("smoke_double", 1,
            args => args[0] is null or DBNull ? null : Convert.ToDouble(args[0]) * 2.0,
            deterministic: true);
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            // 2. DDL roots the index-codes resources (PRIMARY KEY index write).
            cmd.CommandText = "CREATE TABLE t_smoke (id COUNTER PRIMARY KEY, name TEXT(50), " +
                "score DOUBLE, amount MONEY, active YESNO, created DATETIME)";
            cmd.ExecuteNonQuery();
        }
        Check(true, "CREATE TABLE with index");

        // 3. Parameterised writes (roots AccessValueCodec parameter path).
        string[] names = ["Alpha", "Beta", "Gamma"];
        double[] scores = [1.5, 2.5, 3.5];
        for (int i = 0; i < names.Length; i++)
        {
            using var insert = conn.CreateCommand();
            insert.CommandText = "INSERT INTO t_smoke (name, score, amount, active, created) " +
                "VALUES (@p0, @p1, @p2, @p3, @p4)";
            AddParam(insert, "@p0", names[i]);
            AddParam(insert, "@p1", scores[i]);
            AddParam(insert, "@p2", 10.25m + i);
            AddParam(insert, "@p3", i != 0);
            AddParam(insert, "@p4", new DateTime(2024, 1, 15).AddDays(i));
            Check(insert.ExecuteNonQuery() == 1, $"INSERT row {names[i]}");
        }

        // 4. LIKE via the interpreted (non-Compiled) regex cache.
        using (var like = conn.CreateCommand())
        {
            like.CommandText = "SELECT name FROM t_smoke WHERE name LIKE 'A*' ORDER BY name";
            using var reader = like.ExecuteReader();
            var matched = new List<string>();
            while (reader.Read())
            {
                matched.Add(reader.GetString(0));
            }
            Check(matched.Count == 1 && matched[0] == "Alpha", "LIKE 'A*' match");
        }

        // 5. Shadowed aggregates (avg/stdev/var/first/last UDF graph).
        using (var agg = conn.CreateCommand())
        {
            agg.CommandText = "SELECT COUNT(*), AVG(score), FIRST(name), LAST(name) FROM t_smoke";
            using var reader = agg.ExecuteReader();
            Check(reader.Read(), "aggregate row present");
            Check(reader.GetInt32(0) == 3, "COUNT(*) = 3");
            Check(Math.Abs(reader.GetFieldValue<double>(1) - 2.5) < 1e-9, "AVG(score) = 2.5");
            Check(reader.GetString(2) == "Alpha", "FIRST(name)");
            Check(reader.GetString(3) == "Gamma", "LAST(name)");
        }

        // 6. MirrorReader conversions + metadata.
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT name, score, amount, active, created FROM t_smoke ORDER BY id";
            using var reader = read.ExecuteReader();
            Check(reader.Read(), "reader row present");
            Check(reader.GetFieldType(0) == typeof(string), "GetFieldType TEXT");
            Check(reader.GetFieldType(1) == typeof(double), "GetFieldType DOUBLE");
            Check(reader.GetFieldValue<string>(0) == "Alpha", "GetFieldValue<string>");
            Check(reader.GetFieldValue<double>(1) == 1.5, "GetFieldValue<double>");
            Check(reader.GetFieldValue<decimal>(2) == 10.25m, "GetFieldValue<decimal> MONEY");
            Check(reader.GetFieldValue<bool>(3) == false, "GetFieldValue<bool>");
            Check(reader.GetFieldValue<DateTime>(4) == new DateTime(2024, 1, 15), "GetFieldValue<DateTime>");
            Check(reader.GetDataTypeName(0).Length > 0, "GetDataTypeName");
            Check(reader.GetSchemaTable() is not null, "GetSchemaTable");
            // Custom UDF through SQL.
            using var udf = conn.CreateCommand();
            udf.CommandText = "SELECT smoke_double(score) FROM t_smoke ORDER BY id";
            using var udfReader = udf.ExecuteReader();
            udfReader.Read();
            Check(Math.Abs(udfReader.GetDouble(0) - 3.0) < 1e-9, "custom UDF smoke_double");
        }
    }

    // 7. AccessCrypto opener on the plaintext file (roots the crypto graph
    //    without needing an encrypted fixture).
    using (var conn = new UCanAccessConnection($"Data Source={dbPath};Read Only=true"))
    {
        conn.DatabaseOpener = new AccessCryptoOpener();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM t_smoke";
        Check(Convert.ToInt32(cmd.ExecuteScalar()) == 3, "AccessCryptoOpener plaintext open");
    }

    Console.WriteLine(Environment.ExitCode == 0 ? "AOT-SMOKE-OK" : "AOT-SMOKE-FAILED");
}
finally
{
    try
    {
        Directory.Delete(workDir, recursive: true);
    }
    catch
    {
        // Best effort cleanup of the temp database.
    }
}

return Environment.ExitCode;
