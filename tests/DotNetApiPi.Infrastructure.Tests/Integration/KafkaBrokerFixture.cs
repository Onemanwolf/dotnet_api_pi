using DotNet.Testcontainers.Containers;
using Testcontainers.Kafka;

namespace DotNetApiPi.Infrastructure.Tests.Integration;

/// <summary>
/// One Kafka broker per test class (Testcontainers, KRaft mode).
/// <para>
/// The Testcontainers Kafka module leaves <c>KAFKA_ADVERTISED_LISTENERS</c>
/// unset, so a host-side client cannot learn where to connect (the broker
/// advertises the container's internal address). The fix: pin a fixed host
/// port and set the advertised listener explicitly to
/// <c>PLAINTEXT://localhost:{hostPort}</c>. Candidate ports are tried in
/// turn so a locally occupied port does not fail the suite.
/// </para>
/// </summary>
public sealed class KafkaBrokerFixture : IAsyncLifetime
{
    private const int ContainerPort = 9092;
    private const int FirstCandidateHostPort = 59092;
    private const int CandidateHostPortCount = 8;

    private readonly List<DockerContainer> _containers = [];

    /// <summary>
    /// The bootstrap servers a host-side (this machine's) client should
    /// use, e.g. <c>localhost:59092</c>.
    /// </summary>
    public string BootstrapServers { get; private set; } = string.Empty;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        Exception? last = null;

        for (var hostPort = FirstCandidateHostPort;
             hostPort < FirstCandidateHostPort + CandidateHostPortCount;
             hostPort++)
        {
            try
            {
                var bootstrap = $"localhost:{hostPort}";

                var container = new KafkaBuilder("confluentinc/cp-kafka:7.8.10")
                    .WithKRaft()
                    .WithVendor(KafkaVendor.Confluent)
                    .WithPortBinding(hostPort, ContainerPort)
                    .WithEnvironment("KAFKA_ADVERTISED_LISTENERS", $"PLAINTEXT://{bootstrap}")
                    .Build();

                _containers.Add(container);
                await container.StartAsync().ConfigureAwait(false);

                BootstrapServers = bootstrap;
                return;
            }
            catch (Exception exception)
            {
                last = exception;

                await DisposeContainersAsync().ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            "Could not start a Kafka test container on any candidate port.",
            last);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
        => await DisposeContainersAsync().ConfigureAwait(false);

    private async Task DisposeContainersAsync()
    {
        var containers = _containers.ToArray();
        _containers.Clear();

        foreach (var container in containers)
        {
            try
            {
                await container.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best effort: the container is being removed either way.
            }
        }
    }
}
