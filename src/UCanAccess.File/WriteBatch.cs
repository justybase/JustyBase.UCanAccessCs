namespace UCanAccess.File;

/// <summary>
/// A non-atomic write scope which defers the database stream flush until the
/// scope is completed. Row and page changes are still applied immediately to
/// the open database; disposing this object does not roll them back.
/// </summary>
public sealed class WriteBatch : IDisposable
{
    private Database? _database;

    internal WriteBatch(Database database)
    {
        _database = database;
        database.PageChannel.BeginBatch();
    }

    /// <summary>
    /// Flushes the pending stream writes and completes the batch.
    /// </summary>
    public void Commit()
    {
        Database database = _database
            ?? throw new InvalidOperationException("The write batch has already completed.");
        _database = null;
        database.PageChannel.FinishBatch();
    }

    /// <summary>
    /// Completes the scope and flushes pending writes. Changes are not rolled back.
    /// </summary>
    public void Dispose()
    {
        if (_database is not Database database)
        {
            return;
        }
        _database = null;
        database.PageChannel.FinishBatch();
    }
}
