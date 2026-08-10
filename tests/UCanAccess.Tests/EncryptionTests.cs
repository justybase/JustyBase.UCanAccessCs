using System.Text;
using UCanAccess.File;
using Xunit;

namespace UCanAccess.Tests;

public sealed class EncryptionTests
{
    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void Password_is_masked_and_requires_an_opener()
    {
        string path = Fixture("pivot.mdb");
        using var connection = new UCanAccessConnection
        {
            ConnectionString = $"Data Source={path};Read Only=true;Password=secret",
        };

        Assert.DoesNotContain("secret", connection.ConnectionString, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => connection.Open());
    }

    [Fact]
    public void Opener_receives_password_without_making_core_depend_on_crypto()
    {
        var opener = new RecordingOpener();
        using var connection = new UCanAccessConnection
        {
            ConnectionString = $"Data Source={Fixture("pivot.mdb")};Read Only=true;PWD=secret",
            DatabaseOpener = opener,
        };

        connection.Open();
        Assert.Equal("secret", opener.Request?.Password);
        Assert.True(opener.Request?.ReadOnly);
        Assert.Contains("paperino", ReadCodes(connection));
    }

    private static List<string> ReadCodes(UCanAccessConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT c_cod FROM t_pivot";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private sealed class RecordingOpener : IAccessDatabaseOpener
    {
        public AccessDatabaseOpenRequest? Request { get; private set; }

        public Database Open(AccessDatabaseOpenRequest request)
        {
            Request = request;
            return Database.Open(request.Path, request.Encoding, request.ReadOnly, request.AllowExternalLinks);
        }
    }
}
