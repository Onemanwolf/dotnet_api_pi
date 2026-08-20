using DotNetApiPi.Application.Common;
using DotNetApiPi.Domain.Events;
using DotNetApiPi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DotNetApiPi.Infrastructure.Tests;

/// <summary>
/// An <see cref="IDomainEventDispatcher"/> that records every event it is
/// asked to dispatch, for test assertions.
/// </summary>
public sealed class RecordingDomainEventDispatcher : IDomainEventDispatcher
{
    /// <summary>
    /// Gets the events dispatched so far, in dispatch order.
    /// </summary>
    public List<IDomainEvent> Dispatched { get; } = [];

    /// <inheritdoc />
    public Task DispatchAsync(
        IEnumerable<IDomainEvent> events,
        CancellationToken cancellationToken = default)
    {
        Dispatched.AddRange(events);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Hosts an <c>ApiDbContext</c> against a private in-memory SQLite database,
/// so EF Core behaviour (value conversions, unit of work, domain event
/// dispatch) can be exercised without a database file.
/// </summary>
public sealed class InMemoryDatabase : IAsyncDisposable
{
    /// <summary>
    /// The open SQLite connection backing the in-memory database.
    /// </summary>
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryDatabase"/> class.
    /// </summary>
    /// <param name="dispatcher">
    /// The dispatcher shared by every context created by this instance, so
    /// all units of work can be observed from a single recorder.
    /// </param>
    public InMemoryDatabase(RecordingDomainEventDispatcher dispatcher)
    {
        Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        _connection.Open();
        Context = CreateContext();

        // The schema is created up front; Migrate is what the
        // SqliteInfrastructureInitializer does in production (the migrations
        // live in this same assembly, so no MigrationsAssembly hint is needed).
        Context.Database.Migrate();
    }

    /// <summary>
    /// Gets the dispatcher shared by every context in this database.
    /// </summary>
    public RecordingDomainEventDispatcher Dispatcher { get; }

    /// <summary>
    /// Gets the primary database context.
    /// </summary>
    public ApiDbContext Context { get; }

    /// <summary>
    /// Gets the open SQLite connection backing the in-memory database.
    /// </summary>
    public SqliteConnection Connection => _connection;

    /// <summary>
    /// Creates a second <c>ApiDbContext</c> over the same in-memory
    /// database (shared <see cref="Dispatcher"/>), simulating a fresh unit of
    /// work — e.g. what happens across separate HTTP requests.
    /// </summary>
    /// <returns>A database context over the same in-memory database.</returns>
    public ApiDbContext NewContext() => CreateContext();

    /// <summary>
    /// Builds a context over the shared in-memory connection and dispatcher.
    /// </summary>
    private ApiDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApiDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new ApiDbContext(options, Dispatcher);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Context.Dispose();
        _connection.Dispose();
        return ValueTask.CompletedTask;
    }
}
