using DotNetApiPi.Domain.Events;
using DotNetApiPi.Infrastructure.Outbox;

namespace DotNetApiPi.Infrastructure.Tests.Outbox;

/// <summary>
/// Tests for <see cref="DomainEventWireTypes"/>: the stable <c>eventType</c>
/// wire names published on the Kafka topic. These are the contract — a
/// missing mapping must fail fast at the publish site, and renaming a CLR
/// type must not change the wire name.
/// </summary>
public sealed class DomainEventWireTypesTests
{
    [Theory]
    [InlineData(typeof(ResourceCreatedEvent), "resource.created.v1")]
    [InlineData(typeof(ResourceActivatedEvent), "resource.activated.v1")]
    [InlineData(typeof(ResourceArchivedEvent), "resource.archived.v1")]
    [InlineData(typeof(ResourceDeletedEvent), "resource.deleted.v1")]
    public void GetWireName_ReturnsRegisteredStableName(Type eventType, string expected)
    {
        Assert.Equal(expected, DomainEventWireTypes.GetWireName(eventType));
    }

    [Fact]
    public void GetWireName_Throws_ForUnregisteredEventType()
    {
        // A brand-new domain event type that nobody registered yet must fail
        // loudly instead of silently publishing a CLR type name to the
        // stream.
        var exception = Assert.Throws<InvalidOperationException>(
            () => DomainEventWireTypes.GetWireName(typeof(UndisclosedProbeEvent)));

        Assert.Contains("UndisclosedProbeEvent", exception.Message);
    }

    [Fact]
    public void EnvelopeSchemaVersion_IsStable()
    {
        // Consumers branch on this; changing it is a coordinated contract
        // change.
        Assert.Equal(1, DomainEventWireTypes.EnvelopeSchemaVersion);
    }

    /// <summary>
    /// A stand-in domain event type that intentionally has no wire name.
    /// </summary>
    private sealed class UndisclosedProbeEvent : IDomainEvent
    {
        public DateTime OccurredOn => DateTime.UtcNow;
    }
}
