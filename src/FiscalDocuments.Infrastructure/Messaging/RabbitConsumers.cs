using System.Text;
using System.Text.Json;
using FiscalDocuments.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace FiscalDocuments.Infrastructure.Messaging;

public sealed class RabbitConsumers(IServiceScopeFactory scopes, RabbitBroker broker, ILogger<RabbitConsumers> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        RunAsync(IntegrationEvent.Received, stoppingToken), RunAsync(IntegrationEvent.Processed, stoppingToken));

    private async Task RunAsync(string eventType, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var channel = await broker.ConsumerChannelAsync(ct);
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, delivery) => await ConsumeAsync(eventType, channel, delivery, ct);
                await channel.BasicConsumeAsync(broker.Queue(eventType), false, consumer, ct);
                while (channel.IsOpen && !ct.IsCancellationRequested) await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception)
            {
                logger.LogWarning("Consumer {Stage} unavailable; reconnecting", EventProcessor.Consumer(eventType));
                await Task.Delay(5000, ct);
            }
        }
    }

    private async Task ConsumeAsync(string eventType, IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        // RabbitMQ owns delivery memory; copy it before any asynchronous continuation.
        var payload = Encoding.UTF8.GetString(delivery.Body.ToArray());
        IntegrationEvent? message = null;
        try
        {
            try
            {
                message = JsonSerializer.Deserialize<IntegrationEvent>(payload, JsonDefaults.Options)
                    ?? throw new PermanentProcessingException("invalid_event");
                EventProcessor.Validate(message);
                if (message.EventType != eventType) throw new PermanentProcessingException("misrouted_event");
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<EventProcessor>().ProcessAsync(message, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                var attempt = ReadAttempt(delivery.BasicProperties.Headers);
                var permanent = exception is PermanentProcessingException or JsonException;
                var code = exception is PermanentProcessingException known ? known.Code : permanent ? "invalid_event" : "transient_processing_failure";
                if (!permanent && attempt < RabbitBroker.RetryDelays.Length)
                {
                    var retryQueue = broker.Queue(eventType) + ".retry." + (attempt + 1);
                    await broker.PublishAsync(payload, retryQueue, broker.RetryExchange, attempt + 1, code, ct);
                }
                else
                {
                    if (message is not null && message.EventType == eventType)
                    {
                        await using var failureScope = scopes.CreateAsyncScope();
                        await failureScope.ServiceProvider.GetRequiredService<EventProcessor>().FailAsync(message, code, ct);
                    }
                    await broker.PublishAsync(payload, broker.Queue(eventType), broker.DeadExchange, attempt, code, ct);
                    logger.LogWarning("Event {EventId} moved to DLQ; code {ErrorCode}", message?.EventId, code);
                }
            }
            // Also safe after a confirmed durable transfer to retry/DLQ, even though
            // business processing has not succeeded. Never ACK an unconfirmed handoff.
            await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* Close channel => redelivery. */ }
        catch (Exception)
        {
            logger.LogWarning("Delivery handoff/commit unconfirmed; returning message to broker");
            if (channel.IsOpen) await channel.BasicNackAsync(delivery.DeliveryTag, false, true, ct);
            await Task.Delay(1000, ct);
        }
    }

    public static int ReadAttempt(IDictionary<string, object?>? headers)
    {
        if (headers is null || !headers.TryGetValue("x-retry-attempt", out var value)) return 0;
        return value switch { int number => Math.Max(0, number), long number => (int)Math.Clamp(number, 0, int.MaxValue), _ => 0 };
    }
}
