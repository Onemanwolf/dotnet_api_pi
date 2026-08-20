namespace DotNetApiPi.Infrastructure.Persistence;

/// <summary>
/// Options that select and configure the persistence provider used by the
/// infrastructure layer. Bound from the <c>"Storage"</c> configuration
/// section, e.g. via the <c>Storage__Provider=mongo</c> environment variable.
/// </summary>
public sealed record PersistenceOptions
{
    /// <summary>
    /// The configuration section name these options are bound to.
    /// </summary>
    public const string SectionName = "Storage";

    /// <summary>
    /// Gets or sets the persistence provider. Defaults to
    /// <see cref="StorageProvider.Sqlite"/>.
    /// </summary>
    public StorageProvider Provider { get; init; } = StorageProvider.Sqlite;

    /// <summary>
    /// Gets or sets the SQLite connection string.
    /// </summary>
    public string? SqliteConnectionString { get; init; } = "Data Source=dotnet_api_pi.db";

    /// <summary>
    /// Gets or sets the MongoDB connection string.
    /// </summary>
    public string? MongoConnectionString { get; init; } = "mongodb://localhost:27017";

    /// <summary>
    /// Gets or sets the name of the MongoDB database.
    /// </summary>
    public string MongoDatabaseName { get; init; } = "dotnet_api_pi";
}
