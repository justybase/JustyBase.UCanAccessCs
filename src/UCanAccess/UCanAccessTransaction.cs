using System.Data;
using System.Data.Common;
using System.Runtime.ExceptionServices;

namespace UCanAccess;

/// <summary>
/// ADO.NET transaction for UCanAccess. Data-modification statements executed inside a
/// transaction are buffered and applied to the database file only on
/// <see cref="Commit()"/>; <see cref="Rollback()"/> discards them.
/// </summary>
public sealed class UCanAccessTransaction : DbTransaction
{
    private readonly UCanAccessConnection _connection;
    private readonly IsolationLevel _isolationLevel;
    private readonly List<(string Sql, IReadOnlyList<object?>? Parameters)> _pending = new();
    private readonly List<SavepointState> _savepoints = new();
    private File.Database? _stagedDatabase;
    private Mirror? _stagedMirror;
    private string? _stagedPath;
    private Exception? _stageFailure;
    private bool _completed;

    internal UCanAccessTransaction(UCanAccessConnection connection, IsolationLevel isolationLevel)
    {
        _connection = connection;
        _isolationLevel = isolationLevel;
    }

    public override IsolationLevel IsolationLevel => _isolationLevel;

    internal UCanAccessConnection OwnerConnection => _connection;

    /// <summary>
    /// The transaction view of the database. It is backed by a private copy after
    /// the first write, so reads in the transaction observe its own changes without
    /// exposing uncommitted bytes to other connections.
    /// </summary>
    internal Mirror QueryMirror
    {
        get
        {
            Check();
            return _stagedMirror ?? _connection.Mirror;
        }
    }

    protected override DbConnection DbConnection => _connection;

    /// <summary>buffers a pending data-modification statement for the transaction</summary>
    internal int AddPending(string sql, IReadOnlyList<object?>? parameters)
    {
        Check();
        _pending.Add((sql, parameters));
        if (_stageFailure != null)
        {
            return -1;
        }

        try
        {
            EnsureStage();
            string kind = FirstWord(sql);
            if (kind is "CREATE" or "DROP" or "ALTER")
            {
                return AccessDdl.Execute(_stagedDatabase!, _stagedMirror!, sql, false);
            }
            else
            {
                return AccessDml.Execute(_stagedDatabase!, _stagedMirror!, sql, parameters, false);
            }
        }
        catch (Exception ex)
        {
            // Keep the historical transaction behavior: a statement is accepted into
            // the transaction and the commit reports that the complete unit could not
            // be installed. The staged copy is never copied to the real database.
            _stageFailure = ex;
            return -1;
        }
    }

    public override void Commit()
    {
        Check();
        string? stagedPath = _stagedPath;
        try
        {
            if (_stageFailure != null)
            {
                ExceptionDispatchInfo.Capture(_stageFailure).Throw();
            }
            if (stagedPath != null)
            {
                DisposeStage(keepFile: true);
                _connection.ReplaceDatabaseFile(stagedPath);
                stagedPath = null; // File.Replace consumed the staged copy.
            }
        }
        finally
        {
            DisposeStage(keepFile: false);
            if (stagedPath != null)
            {
                try
                {
                    System.IO.File.Delete(stagedPath);
                }
                catch
                {
                    // Preserve the original exception and leave diagnostics if deletion fails.
                }
            }
            _pending.Clear();
            DeleteSavepoints();
            _completed = true;
            _connection.ClearTransaction(this);
        }
    }

    /// <summary>
    /// Creates an ADO.NET savepoint. The snapshot remains private to this
    /// transaction and is removed when the transaction completes.
    /// </summary>
    public override void Save(string savepointName)
    {
        Check();
        ValidateSavepointName(savepointName);
        EnsureStage();

        string stagedPath = _stagedPath
            ?? throw new InvalidOperationException("The transaction has no staged database.");
        string directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(stagedPath))
            ?? throw new InvalidOperationException("Could not determine the database directory.");
        string snapshotPath = System.IO.Path.Combine(
            directory,
            "." + System.IO.Path.GetFileNameWithoutExtension(stagedPath) +
            ".ucanaccess-savepoint-" + Guid.NewGuid().ToString("N") +
            System.IO.Path.GetExtension(stagedPath));
        System.IO.File.Copy(stagedPath, snapshotPath, true);

        for (int i = _savepoints.Count - 1; i >= 0; i--)
        {
            if (_savepoints[i].Name.Equals(savepointName, StringComparison.OrdinalIgnoreCase))
            {
                DeleteFile(_savepoints[i].Path);
                _savepoints.RemoveAt(i);
            }
        }
        _savepoints.Add(new SavepointState(savepointName, snapshotPath));
    }

    /// <summary>
    /// Restores the transaction staging database to a savepoint. Changes made
    /// after the savepoint are discarded; the savepoint itself remains active.
    /// </summary>
    public override void Rollback(string savepointName)
    {
        Check();
        ValidateSavepointName(savepointName);
        int savepointIndex = _savepoints.FindIndex(
            s => s.Name.Equals(savepointName, StringComparison.OrdinalIgnoreCase));
        if (savepointIndex < 0)
        {
            throw new InvalidOperationException($"Savepoint '{savepointName}' does not exist.");
        }

        string stagedPath = _stagedPath
            ?? throw new InvalidOperationException("The transaction has no staged database.");
        SavepointState savepoint = _savepoints[savepointIndex];
        DisposeStage(keepFile: true);
        try
        {
            System.IO.File.Copy(savepoint.Path, stagedPath, true);
            _stagedDatabase = _connection.OpenDatabaseFile(stagedPath, readOnly: false);
            _stagedMirror = _connection.CreateMirrorFor(_stagedDatabase);
            _stagedPath = stagedPath;
        }
        catch
        {
            _stagedMirror?.Dispose();
            _stagedMirror = null;
            _stagedDatabase?.Dispose();
            _stagedDatabase = null;
            _stagedPath = null;
            throw;
        }

        // SQL rollback-to preserves the selected savepoint and removes newer
        // savepoints because their state no longer exists in the restored file.
        for (int i = _savepoints.Count - 1; i > savepointIndex; i--)
        {
            DeleteFile(_savepoints[i].Path);
            _savepoints.RemoveAt(i);
        }
    }

    private void EnsureStage()
    {
        if (_stagedMirror != null)
        {
            return;
        }

        string srcPath = _connection.AccessDatabase.Path;
        if (string.IsNullOrEmpty(srcPath) || !System.IO.File.Exists(srcPath))
        {
            throw new InvalidOperationException("Transactions require a file-backed database so the commit can be prepared atomically.");
        }
        if (_connection.AccessDatabase.GetTableMetaData().Any(meta => meta.IsLinked))
        {
            throw new NotSupportedException(
                "Transactions involving native linked tables are not supported atomically; commit them in a separate connection.");
        }

        string sourceDirectory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(srcPath))
            ?? throw new InvalidOperationException("Could not determine the database directory.");
        string extension = System.IO.Path.GetExtension(srcPath);
        string copyPath = System.IO.Path.Combine(
            sourceDirectory,
            "." + System.IO.Path.GetFileNameWithoutExtension(srcPath) +
            ".ucanaccess-tx-" + Guid.NewGuid().ToString("N") + extension);
        System.IO.File.Copy(srcPath, copyPath, true);
        try
        {
            _stagedDatabase = _connection.OpenDatabaseFile(copyPath, readOnly: false);
            _stagedMirror = _connection.CreateMirrorFor(_stagedDatabase);
            _stagedPath = copyPath;
        }
        catch
        {
            _stagedMirror?.Dispose();
            _stagedMirror = null;
            _stagedDatabase?.Dispose();
            _stagedDatabase = null;
            try
            {
                System.IO.File.Delete(copyPath);
            }
            catch
            {
                // Preserve the original exception.
            }
            throw;
        }
    }

    private void DisposeStage(bool keepFile)
    {
        _stagedMirror?.Dispose();
        _stagedMirror = null;
        _stagedDatabase?.Dispose();
        _stagedDatabase = null;

        string? path = _stagedPath;
        _stagedPath = null;
        if (!keepFile && path != null)
        {
            try
            {
                System.IO.File.Delete(path);
            }
            catch
            {
                // Preserve the transaction exception; the staged file is harmless and
                // can be removed by the next cleanup pass.
            }
        }
    }

    private static string FirstWord(string sql)
    {
        string trimmed = sql.TrimStart();
        int end = 0;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]))
        {
            end++;
        }
        return trimmed[..end].ToUpperInvariant();
    }

    public override void Rollback()
    {
        Check();
        _pending.Clear();
        DisposeStage(keepFile: false);
        DeleteSavepoints();
        _completed = true;
        _connection.ClearTransaction(this);
    }

    private void Check()
    {
        if (_completed)
        {
            throw new InvalidOperationException("The transaction has already completed.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        // an uncommitted transaction is rolled back on dispose
        if (!_completed)
        {
            _pending.Clear();
            DisposeStage(keepFile: false);
            DeleteSavepoints();
            _completed = true;
            _connection.ClearTransaction(this);
        }
        base.Dispose(disposing);
    }

    private static void ValidateSavepointName(string savepointName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(savepointName);
    }

    private void DeleteSavepoints()
    {
        foreach (SavepointState savepoint in _savepoints)
        {
            DeleteFile(savepoint.Path);
        }
        _savepoints.Clear();
    }

    private static void DeleteFile(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch
        {
            // A stale snapshot is harmless and can be cleaned up by the caller.
        }
    }

    private sealed record SavepointState(string Name, string Path);
}
