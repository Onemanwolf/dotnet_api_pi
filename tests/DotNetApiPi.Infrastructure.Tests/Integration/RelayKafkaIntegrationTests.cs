using System.Text.Json;
using Confluent.Kafka;
using DotNetApiPi.Infrastructure.Kafka;
using DotNetApiPi.Infrastructure.Outbox;
using MongoDB.Driver;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace DotNetApiPi.Infrastructure.Tests.Integration;

/// <summary>
/// End-to-end integration test: an outbox row is published to a real Kafka
/// broker (Testcontainers, KRaft) by the production
/// <see cref="ConfluentKafkaEventPublisher"/> and consumed back — the
/// envelope must carry the stable wire contract (<c>schemaVersion</c>,
/// <c>eventType</c>) and the <c>x-event-id</c> header. Tagged
/// <c>Category=Integration</c>.
/// </summary>
public sealed class RelayKafkaIntegrationTests :
    IClassFixture<MongoReplicaSetFixture>,
    IClassFixture<KafkaBrokerFixture>
{
    private const string Topic = "it-resource-events";

    private readonly MongoReplicaSetFixture _mongo;
    private readonly KafkaBrokerFixture _kafka;
    private readonly ITestOutputHelper _output;

    public RelayKafkaIntegrationTests(
        MongoReplicaSetFixture mongo,
        KafkaBrokerFixture kafka,
        ITestOutputHelper output)
    {
        _mongo = mongo;
        _kafka = kafka;
        _output = output;
    }

    [Trait("Category", "Integration")]
    [Fact(Timeout = 120_000)]
    public async Task RelayPath_PublishesStableEnvelope_WithEventIdHeader_ToRealBroker()
    {
        // One outbox row, exactly as the repository would have written it
        // inside a committed unit of work (written through the store in its
        // own transaction, so the row shape is the production one).
        var database = _mongo.CreateDatabase();
        var store = new MongoOutboxEventStore(database);

        var now = DateTime.UtcNow;
        var eventId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var record = new OutboxEventRecord(
            eventId,
            "resource.created.v1",
            resourceId,
            now,
            "{\"name\":\"e2e-probe\"}",
            OutboxEventStatus.Pending,
            0,
            now,
            now,
            Guid.Empty,
            null,
            null,
            null,
            null);

        using (var session = _mongo.Client.StartSession())
        {
            await session
                .WithTransactionAsync(
                    async (transaction, cancellationToken) =>
                    {
                        await store
                            .AppendWithinTransactionAsync(
                                [record],
                                transaction,
                                cancellationToken);
                        return 0;
                    },
                    new TransactionOptions(),
                    CancellationToken.None);
        }

        // The production publisher, pointed at the test broker.
        var options = new KafkaOptions
        {
            BootstrapServers = _kafka.BootstrapServers,
            Topic = Topic,
            MessageTimeoutMs = 5_000
        };

        await using var publisher = new ConfluentKafkaEventPublisher(
            Options.Create(options),
            NullLogger<ConfluentKafkaEventPublisher>.Instance);

        // Exactly what the relay sends: the resource id as message key and
        // the envelope serialized from the row.
        var envelope = OutboxEventEnvelope.FromRecord(record);
        var result = await publisher
            .PublishAsync(
                resourceId.ToString("D"),
                envelope.Serialize(),
                new Dictionary<string, string>
                {
                    ["x-event-id"] = eventId.ToString("D")
                },
                CancellationToken.None);

        _output.WriteLine(
            $"Published to {result.Topic}/{result.Partition}@{result.Offset}");
        Assert.Equal(Topic, result.Topic);

        // Consume the record back from the broker and verify the wire
        // contract end to end.
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = _kafka.BootstrapServers,
            GroupId = $"it-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();
        consumer.Subscribe(Topic);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        ConsumeResult<string, string>? consumed = null;

        while (consumed is null && DateTime.UtcNow < deadline)
        {
            var poll = consumer.Consume(TimeSpan.FromSeconds(1));

            if (poll is not null
                && poll.Message.Key?.ToString() == resourceId.ToString("D"))
            {
                consumed = poll;
            }
        }

        Assert.NotNull(consumed);

        using var document = JsonDocument.Parse(consumed!.Message.Value);
        var root = document.RootElement;

        // The stable wire contract: schema version + stable event type name
        // (never a CLR type name) + the event/resource identities.
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("resource.created.v1", root.GetProperty("eventType").GetString());
        Assert.Equal(eventId.ToString("D"), root.GetProperty("eventId").GetString());
        Assert.Equal(resourceId.ToString("D"), root.GetProperty("resourceId").GetString());

        // The consumer-facing de-duplication header round-trips.
        Assert.True(consumed.Message.Headers.TryGetLastBytes("x-event-id", out var headerBytes));
        Assert.Equal(eventId.ToString("D"), System.Text.Encoding.UTF8.GetString(headerBytes));
    }
}
