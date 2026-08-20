using DotNetApiPi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DotNetApiPi.Infrastructure;

/// <summary>
/// SQLite/EF Core implementation of <see cref="IInfrastructureInitializer"/>.
/// Brings the database schema up to date by applying any pending EF Core
/// migrations (see the <c>Migrations</c> folder of this project).
/// <para>
/// <b>Existing development databases:</b> databases created before the
/// migration switch used <c>EnsureCreated</c> and therefore lack the
/// <c>__EFMigrationsHistory</c> bookkeeping table (and any schema changes the
/// migrations introduce, such as the <c>Version</c> column). <c>Migrate</c>
/// cannot reconcile such a database — delete the file (<c>dotnet_api_pi.db</c>
/// plus its <c>-wal</c>/<c>-shm</c> side files) once; the initializer recreates
/// it from the migrations on next start.
/// </para>
/// </summary>
public sealed class SqliteInfrastructureInitializer : IInfrastructureInitializer
{
    private readonly ApiDbContext _context;
    private readonly ILogger<SqliteInfrastructureInitializer> _logger;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="SqliteInfrastructureInitializer"/> class.
    /// </summary>
    /// <param name="context">The application's database context.</param>
    /// <param name="logger">The logger.</param>
    public SqliteInfrastructureInitializer(
        ApiDbContext context,
        ILogger<SqliteInfrastructureInitializer> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Applying pending EF Core migrations to the SQLite database.");

        await _context.Database
            .MigrateAsync(cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("The SQLite database schema is up to date.");
    }
}
