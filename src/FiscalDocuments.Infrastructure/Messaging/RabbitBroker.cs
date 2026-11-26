using System.Text;
using System.Text.Json;
using FiscalDocuments.Application;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace FiscalDocuments.Infrastructure.Messaging;

public sealed class RabbitBroker(IOptions<RabbitOptions> options) : IAsyncDisposable
{
    private readonly string topologyPrefix = ValidatePrefix(options.Value.TopologyPrefix);
    public string EventsExchange => topologyPrefix + ".events";
    public string RetryExchange => topologyPrefix + ".retry";
    public string DeadExchange => topologyPrefix + ".dead";
    public static readonly int[] RetryDelays = [5000, 30000, 120000];
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private readonly SemaphoreSlim publishGate = new(1, 1);
    private IConnection? connection;
    private IChannel? publisher;
    public bool IsConnected => connection?.IsOpen == true;
    public string Queue(string type)
    {
        return topologyPrefix + "." + EventProcessor.Consumer(type);
    }

    private static string ValidatePrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length > 100 ||
            prefix.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
        {
            throw new InvalidOperationException("Rabbit:TopologyPrefix requires 1 to 100 ASCII letters, digits, dots, underscores or hyphens.");
        }

        return prefix;
    }

    public async Task<IConnection> ConnectionAsync(CancellationToken ct)
    {
        await connectionGate.WaitAsync(ct);
        try
        {
            if (connection is { IsOpen: true })
            {
                return connection;
            }

            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            var settings = options.Value;
            if (string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrWhiteSpace(settings.Password))
            {
                throw new InvalidOperationException("Rabbit credentials are required.");
            }

            connection = await new ConnectionFactory
            {
                HostName = settings.Host, Port = settings.Port, UserName = settings.Username, Password = settings.Password,
                VirtualHost = settings.VirtualHost, AutomaticRecoveryEnabled = true, TopologyRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5), RequestedHeartbeat = TimeSpan.FromSeconds(30),
                ConsumerDispatchConcurrency = 1
            }.CreateConnectionAsync(ct);
            return connection;
        }
        finally { connectionGate.Release(); }
    }

    public async Task<IChannel> ConsumerChannelAsync(CancellationToken ct)
    {
        var current = await ConnectionAsync(ct);
        var channel = await current.CreateChannelAsync(cancellationToken: ct);
        await DeclareAsync(channel, ct);
        await channel.BasicQosAsync(0, 4, false, ct);
        return channel;
    }

    public async Task PublishAsync(string payload, string routingKey, string exchange, int attempt, string? errorCode, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await publishGate.WaitAsync(timeout.Token);
        try
        {
            if (publisher is not { IsOpen: true })
            {
                if (publisher is not null)
                {
                    await publisher.DisposeAsync();
                }

                var current = await ConnectionAsync(timeout.Token);
                publisher = await current.CreateChannelAsync(new CreateChannelOptions(
                    publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), timeout.Token);
                await DeclareAsync(publisher, timeout.Token);
            }
            var headers = new Dictionary<string, object?> { ["x-retry-attempt"] = attempt };
            if (errorCode is not null)
            {
                headers["x-error-code"] = errorCode;
            }

            var properties = new BasicProperties
            {
                Persistent = true, ContentType = "application/json", Headers = headers
            };
            // Com o rastreamento de confirmações, este await rejeita NACKs do RabbitMQ e devoluções de mensagens publicadas com mandatory.
            await publisher.BasicPublishAsync(exchange, routingKey, true, properties, Encoding.UTF8.GetBytes(payload), timeout.Token);
        }
        catch
        {
            if (publisher is not null)
            {
                await publisher.DisposeAsync();
            }

            publisher = null;
            throw;
        }
        finally { publishGate.Release(); }
    }

    public Task PublishEventAsync(IntegrationEvent message, CancellationToken ct)
    {
        return PublishAsync(JsonSerializer.Serialize(message, JsonDefaults.Options), message.EventType, EventsExchange, 0, null, ct);
    }

    public async Task DeclareAsync(IChannel channel, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(EventsExchange, ExchangeType.Direct, true, false, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(RetryExchange, ExchangeType.Direct, true, false, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(DeadExchange, ExchangeType.Direct, true, false, cancellationToken: ct);
        foreach (var type in new[] { IntegrationEvent.Received, IntegrationEvent.Processed })
        {
            var queue = Queue(type);
            var args = QuorumArgs(DeadExchange, queue);
            args["x-delivery-limit"] = 20;
            await channel.QueueDeclareAsync(queue, true, false, false, args, cancellationToken: ct);
            await channel.QueueBindAsync(queue, EventsExchange, type, cancellationToken: ct);
            await channel.QueueDeclareAsync(queue + ".dead", true, false, false,
                new Dictionary<string, object?> { ["x-queue-type"] = "quorum", ["x-max-length"] = 10000, ["x-overflow"] = "reject-publish" }, cancellationToken: ct);
            await channel.QueueBindAsync(queue + ".dead", DeadExchange, queue, cancellationToken: ct);
            for (var i = 0; i < RetryDelays.Length; i++)
            {
                var retry = queue + ".retry." + (i + 1);
                var retryArgs = QuorumArgs(EventsExchange, type);
                retryArgs["x-message-ttl"] = RetryDelays[i];
                await channel.QueueDeclareAsync(retry, true, false, false, retryArgs, cancellationToken: ct);
                await channel.QueueBindAsync(retry, RetryExchange, retry, cancellationToken: ct);
            }
        }
    }

    private static Dictionary<string, object?> QuorumArgs(string deadExchange, string routingKey)
    {
        return new()
        {
            ["x-queue-type"] = "quorum",
            ["x-dead-letter-exchange"] = deadExchange,
            ["x-dead-letter-routing-key"] = routingKey,
            ["x-dead-letter-strategy"] = "at-least-once",
            ["x-overflow"] = "reject-publish",
            ["x-max-length"] = 10000
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (publisher is not null)
        {
            await publisher.DisposeAsync();
        }

        if (connection is not null)
        {
            await connection.DisposeAsync();
        }

        connectionGate.Dispose();
        publishGate.Dispose();
    }
}
