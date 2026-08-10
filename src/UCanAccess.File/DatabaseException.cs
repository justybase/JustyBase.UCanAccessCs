namespace UCanAccess.File;

/// <summary>
/// Exception thrown for errors reading/writing MS Access database files.
/// </summary>
public class DatabaseException : Exception
{
    public DatabaseException(string message) : base(message)
    {
    }

    public DatabaseException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
