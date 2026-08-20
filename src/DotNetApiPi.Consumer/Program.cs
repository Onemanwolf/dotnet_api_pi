using Confluent.Kafka;
using DotNetApiPi.Consumer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// The event consumer for the "resource-events" topic: a long-running
// container that reads every domain event published by the API's outbox
// relay and logs its content and metadata to stdout (=> docker logs).
//
// Configuration (environment variables, compose-style double-underscore
// sections):
//   Kafka__BootstrapServers  comma-separated brokers (default localhost:29092)
//   Kafka__Topic             topic to consume (default resource-events)
//   Consumer__GroupId        consumer group (default dotnet-api-pi-logger)
var builder = Host.CreateApplicationBuilder(args);

var bootstrapServers = builder.Configuration["Kafka:BootstrapServers"];
var topic = builder.Configuration["Kafka:Topic"] ?? "resource-events";
var groupId = builder.Configuration["Consumer:GroupId"] ?? "dotnet-api-pi-logger";

if (string.IsNullOrWhiteSpace(bootstrapServers))
{
    // A compose stack sets Kafka__BootstrapServers=kafka:19092; a manual
    // local run falls back to the host-facing listener default.
    bootstrapServers = "localhost:29092";
}

// Single-line console logs so `docker logs` stays greppable (one line per
// event). The "simple" formatter (not the JSON one) is used on purpose:
// with the JSON formatter a structured argument is emitted twice (escaped
// inside "Message" and raw inside "State"), which doubled every event.
// Minimum level is Information: the per-message line is Information,
// librdkafka internals are Debug (visible with --verbose).
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz";
    options.IncludeScopes = false;
});

var consumerConfig = new ConsumerConfig
{
    BootstrapServers = bootstrapServers,
    GroupId = groupId,
    // Replay from the beginning when the group has no committed offset
    // yet (a fresh logger wants the whole history).
    AutoOffsetReset = AutoOffsetReset.Earliest,
    // Commit manually, only after a message is processed (see
    // EventConsumerService): at-least-once, no silent offset jumps.
    EnableAutoCommit = false,
    // The group rebalances if this consumer dies: the group protocol
    // detects it via the session heartbeat/timeout (defaults are fine).
    SessionTimeoutMs = 30_000,
    MaxPollIntervalMs = 120_000
};

// Resolve the logger from the service provider (the console provider above
// is applied to the host's logging pipeline).
builder.Services.AddHostedService(sp => new EventConsumerService(
    consumerConfig,
    topic,
    sp.GetRequiredService<ILogger<EventConsumerService>>()));
var host = builder.Build();
await host.RunAsync();
