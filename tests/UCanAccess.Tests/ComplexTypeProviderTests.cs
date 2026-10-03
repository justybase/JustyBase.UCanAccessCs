using UCanAccess.File;
using Xunit;

namespace UCanAccess.Tests;

public sealed class ComplexTypeProviderTests
{
    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void Provider_returns_typed_complex_values()
    {
        using var connection = new UCanAccessConnection
        {
            ConnectionString = $"Data Source={Fixture("generated/complex.accdb")};Read Only=true",
        };
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Tags, Files FROM ComplexFixture";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());

        AccessSingleValue[] tags = Assert.IsType<AccessSingleValue[]>(reader.GetValue(0));
        Assert.Equal(new object?[] { "alpha", "beta" }, tags.Select(v => v.Value));
        Assert.IsType<AccessAttachment[]>(reader.GetValue(1));
        Assert.Equal("COMPLEX", reader.GetDataTypeName(0));
    }

    [Fact]
    public void Parameterized_insert_writes_complex_child_rows()
    {
        string source = Fixture("generated/complex.accdb");
        string path = Path.Combine(Path.GetTempPath(), $"uca-complex-provider-{Guid.NewGuid():N}.accdb");
        System.IO.File.Copy(source, path);
        try
        {
            using var connection = new UCanAccessConnection
            {
                ConnectionString = $"Data Source={path};Read Only=false"
            };
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO ComplexFixture (ID, Tags, Files) VALUES (?, ?, ?)";
            foreach (object value in new object[]
                     {
                         2,
                         new[] { new AccessSingleValue("provider") },
                         new[] { new AccessAttachment(new byte[] { 7 }, 0, "provider.bin", null, "bin", null) },
                     })
            {
                var parameter = command.CreateParameter();
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }
            Assert.Equal(1, command.ExecuteNonQuery());

            using var verify = connection.CreateCommand();
            verify.CommandText = "SELECT Tags, Files FROM ComplexFixture WHERE ID = 2";
            using var reader = verify.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("provider",
                Assert.Single(Assert.IsType<AccessSingleValue[]>(reader.GetValue(0))).Value);
            Assert.Equal("provider.bin",
                Assert.Single(Assert.IsType<AccessAttachment[]>(reader.GetValue(1))).FileName);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Insert_omitting_complex_columns_stores_null_values()
    {
        string source = Fixture("generated/complex.accdb");
        string path = Path.Combine(Path.GetTempPath(), $"uca-complex-provider-null-{Guid.NewGuid():N}.accdb");
        System.IO.File.Copy(source, path);
        try
        {
            using var connection = new UCanAccessConnection
            {
                ConnectionString = $"Data Source={path};Read Only=false"
            };
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO ComplexFixture (ID) VALUES (2)";
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            using var verify = connection.CreateCommand();
            verify.CommandText = "SELECT Tags, Files FROM ComplexFixture WHERE ID = 2";
            using var reader = verify.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Equals_matches_exact_order()
    {
        using var connection = new UCanAccessConnection
        {
            ConnectionString = $"Data Source={Fixture("generated/complex.accdb")};Read Only=true",
        };
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM ComplexFixture WHERE Equals(Tags, ?)";
        var parameter = command.CreateParameter();
        parameter.Value = new[] { new AccessSingleValue("alpha"), new AccessSingleValue("beta") };
        command.Parameters.Add(parameter);
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public void Equals_is_order_sensitive()
    {
        using var connection = new UCanAccessConnection
        {
            ConnectionString = $"Data Source={Fixture("generated/complex.accdb")};Read Only=true",
        };
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM ComplexFixture WHERE Equals(Tags, ?)";
        var parameter = command.CreateParameter();
        parameter.Value = new[] { new AccessSingleValue("beta"), new AccessSingleValue("alpha") };
        command.Parameters.Add(parameter);
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void EqualsIgnoreOrder_ignores_order()
    {
        using var connection = new UCanAccessConnection
        {
            ConnectionString = $"Data Source={Fixture("generated/complex.accdb")};Read Only=true",
        };
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM ComplexFixture WHERE EqualsIgnoreOrder(Tags, ?)";
        var parameter = command.CreateParameter();
        parameter.Value = new[] { new AccessSingleValue("beta"), new AccessSingleValue("alpha") };
        command.Parameters.Add(parameter);
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public void Contains_matches_subset()
    {
        using var connection = new UCanAccessConnection
        {
            ConnectionString = $"Data Source={Fixture("generated/complex.accdb")};Read Only=true",
        };
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT count(*) FROM ComplexFixture WHERE Contains(Tags, ?)";
            var parameter = command.CreateParameter();
            parameter.Value = new[] { new AccessSingleValue("alpha") };
            command.Parameters.Add(parameter);
            Assert.Equal(1L, command.ExecuteScalar());
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT count(*) FROM ComplexFixture WHERE Contains(Tags, ?)";
            var parameter = command.CreateParameter();
            parameter.Value = new[] { new AccessSingleValue("missing") };
            command.Parameters.Add(parameter);
            Assert.Equal(0L, command.ExecuteScalar());
        }
    }
}
