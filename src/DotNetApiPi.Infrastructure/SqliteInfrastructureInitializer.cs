using DotNetApiPi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DotNetApiPi.Infrastructure;

/// <summary>
/// SQLite/EF Core implementation of <see cref="IInfrastructureInitializer"/>.
/// Creates the database schema if it does not exist yet.
/// <para>
/// Note: <c>EnsureCreated</c> is a scaffold-friendly bootstrap that bypasses
/// migrations. Once the schema starts evolving, switch to EF Core migrations
/// (<c>dotnet ef migrations add ...</c> / <c>migrate</c>) and replace this
/// initializer's body with a <c>context.Database.MigrateAsync</c> call.
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
        _logger.LogInformation("Ensuring the SQLite database schema exists.");

        var created = await _context.Database
            .EnsureCreatedAsync(cancellationToken)
            .ConfigureAwait(false);

        if (created)
        {
            _logger.LogInformation("Created the SQLite database schema.");
        }
    }
}
