namespace DotNetApiPi.Infrastructure.Persistence;

/// <summary>
/// Selects the persistence technology used by the infrastructure layer.
/// </summary>
public enum StorageProvider
{
    /// <summary>
    /// A SQLite database file, accessed through EF Core. This is the default
    /// so the API can run without any external service.
    /// </summary>
    Sqlite = 0,

    /// <summary>
    /// A MongoDB document database, accessed directly through the MongoDB
    /// driver (no EF Core).
    /// </summary>
    Mongo = 1
}
