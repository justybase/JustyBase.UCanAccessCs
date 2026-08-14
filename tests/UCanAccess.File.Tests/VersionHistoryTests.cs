using UCanAccess.File;
using Xunit;

namespace UCanAccess.File.Tests;

public sealed class VersionHistoryTests
{
    [Fact]
    public void Reads_and_writes_version_history_child_rows()
    {
        string? configured = Environment.GetEnvironmentVariable("UCANACCESS_VERSION_FIXTURE");
        string path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "fixtures", "generated", "version.accdb")
            : configured;
        if (!System.IO.File.Exists(path))
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "Set UCANACCESS_VERSION_FIXTURE or generate version.accdb with Generate-VersionFixture.ps1.");
        }

        string copy = Path.Combine(Path.GetTempPath(), $"uca-version-{Guid.NewGuid():N}.accdb");
        System.IO.File.Copy(path, copy, true);
        try
        {
            using (var db = Database.Open(copy, readOnly: false))
            {
                Table table = db.GetTable("VersionFixture")
                    ?? throw new InvalidOperationException("VersionFixture table is missing.");
                int historyIndex = table.Columns
                    .First(column => column.Name.Equals("History", StringComparison.OrdinalIgnoreCase))
                    .ColumnIndex;
                Row row = Assert.Single(table.Rows());
                AccessVersion[] history = Assert.IsType<AccessVersion[]>(row[historyIndex]);
                Assert.True(history.Length >= 2);
                Assert.Contains(history, value => value.Value?.ToString() == "first version");

                object?[] values = row.ToArray();
                values[historyIndex] = history.Append(
                    new AccessVersion("managed version", DateTime.UtcNow)).ToArray();
                Table.RowLocation location = Assert.Single(table.RowLocations());
                table.UpdateRow(location.PageNumber, location.RowNumber, values);
            }

            using var reopened = Database.Open(copy, readOnly: true);
            Table reopenedTable = reopened.GetTable("VersionFixture")!;
            int reopenedIndex = reopenedTable.Columns
                .First(column => column.Name.Equals("History", StringComparison.OrdinalIgnoreCase))
                .ColumnIndex;
            AccessVersion[] updated = Assert.IsType<AccessVersion[]>(
                Assert.Single(reopenedTable.Rows())[reopenedIndex]);
            Assert.Contains(updated, value => value.Value?.ToString() == "managed version");
        }
        finally
        {
            System.IO.File.Delete(copy);
        }
    }
}
