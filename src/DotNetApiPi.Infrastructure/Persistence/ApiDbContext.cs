using System.Collections.Immutable;
using System.Text.Json;
using DotNetApiPi.Application.Common;
using DotNetApiPi.Domain.Common;
using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.Enums;
using DotNetApiPi.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace DotNetApiPi.Infrastructure.Persistence;

/// <summary>
/// The application's EF Core <see cref="DbContext"/>. It configures the
/// persistence model and, after a successful save, dispatches the domain
/// events raised by the tracked aggregates.
/// </summary>
public class ApiDbContext : DbContext
{
    /// <summary>
    /// Shared, immutable JSON options for the value-conversion (de)serializers.
    /// <see cref="JsonSerializerOptions"/> is thread-safe once configured, so a
    /// single instance is reused for every serialization instead of allocating
    /// a new one per field access.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiDbContext"/> class.
    /// </summary>
    /// <param name="options">The options for the context.</param>
    /// <param name="dispatcher">The domain event dispatcher.</param>
    public ApiDbContext(
        DbContextOptions<ApiDbContext> options,
        IDomainEventDispatcher dispatcher)
        : base(options)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiDbContext"/> class for
    /// design-time tooling (migrations). The dispatcher is not required in that
    /// context.
    /// <para>
    /// Intentionally <c>internal</c> so that the runtime composition root can
    /// never resolve this overload and silently lose event dispatch; if EF
    /// Core design-time tooling is introduced later, pair it with an
    /// <c>IDesignTimeDbContextFactory</c> instead.
    /// </para>
    /// </summary>
    /// <param name="options">The options for the context.</param>
    internal ApiDbContext(DbContextOptions<ApiDbContext> options)
        : base(options)
    {
    }

    private readonly IDomainEventDispatcher? _dispatcher;

    /// <summary>
    /// Gets or sets the set of resources.
    /// </summary>
    public DbSet<Resource> Resources { get; set; } = null!;

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var resource = modelBuilder.Entity<Resource>();
        resource.ToTable("Resources");
        resource.HasKey(r => r.Id);

        resource.Property(r => r.Name)
            .HasConversion(
                name => name.Value,
                value => new ResourceName(value))
            .IsRequired()
            .HasMaxLength(256);

        resource.Property(r => r.Description)
            .HasMaxLength(2048);

        resource.Property(r => r.Status)
            .HasConversion(
                status => status.ToString(),
                value => ParseStatus(value))
            .HasMaxLength(32);

        resource.Property(r => r.Tags)
            .HasConversion(
                tags => SerializeTags(tags),
                value => DeserializeTags(value))
            .IsRequired()
            // 4,096 covers the domain's worst-case tag blob (50 tags × 64
            // characters = 3,200 plus JSON punctuation/escaping, ≈3,400) with
            // headroom — see Resource.MaxTagCount and ResourceTag.MaxLength.
            .HasMaxLength(4096);

        resource.Property(r => r.Version)
            // Optimistic concurrency: EF Core includes the version loaded in
            // this unit of work in the WHERE clause of every UPDATE, so a
            // write based on a stale aggregate fails with
            // DbUpdateConcurrencyException. ResourceRepository translates
            // that into ResourceConcurrencyException (HTTP 412).
            .IsConcurrencyToken();
    }

    /// <inheritdoc />
    public override async Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await DispatchDomainEventsAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Serializes the tag set into a JSON array of strings.
    /// </summary>
    private static string SerializeTags(ImmutableArray<ResourceTag> tags)
    {
        return JsonSerializer.Serialize(
            tags.Select(static tag => tag.Value).ToArray(),
            JsonOptions);
    }

    /// <summary>
    /// Deserializes a stored JSON string back into an immutable set of tags.
    /// </summary>
    private static ImmutableArray<ResourceTag> DeserializeTags(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ImmutableArray<ResourceTag>.Empty;
        }

        var values = JsonSerializer.Deserialize<string[]>(value, JsonOptions)
            ?? [];

        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => new ResourceTag(value))
            .ToImmutableArray();
    }

    /// <summary>
    /// Parses a stored string value back into a <see cref="ResourceStatus"/>.
    /// </summary>
    private static ResourceStatus ParseStatus(string? value)
    {
        return value is null
            ? ResourceStatus.Draft
            : (ResourceStatus)Enum.Parse(typeof(ResourceStatus), value, ignoreCase: true);
    }

    /// <summary>
    /// Collects the domain events from the tracked aggregates, dispatches them
    /// and clears them so they are not dispatched twice.
    /// <para>
    /// Aggregates are discovered through the non-generic
    /// <see cref="IHasDomainEvents"/> marker, so an aggregate with any identity
    /// type is handled (previously only <c>AggregateRoot{Guid}</c> was matched).
    /// </para>
    /// <para>
    /// Known trade-off: events are dispatched after the transaction commits,
    /// so a handler failure cannot roll back the write (and a crash between the
    /// two loses the event). For at-least-once semantics an outbox pattern
    /// should be introduced when real event consumers appear.
    /// </para>
    /// </summary>
    private async Task DispatchDomainEventsAsync(CancellationToken cancellationToken)
    {
        if (_dispatcher is null)
        {
            return;
        }

        var aggregateRoots = ChangeTracker.Entries()
            .Where(entry => entry.Entity is IHasDomainEvents aggregate && aggregate.DomainEvents.Any())
            .Select(static entry => (IHasDomainEvents)entry.Entity)
            .ToList();

        var events = aggregateRoots
            .SelectMany(aggregate => aggregate.DomainEvents)
            .ToList();

        if (events.Count == 0)
        {
            return;
        }

        await _dispatcher.DispatchAsync(events, cancellationToken).ConfigureAwait(false);

        foreach (var aggregate in aggregateRoots)
        {
            ((IClearableDomainEvents)aggregate).ClearDomainEvents();
        }
    }
}
