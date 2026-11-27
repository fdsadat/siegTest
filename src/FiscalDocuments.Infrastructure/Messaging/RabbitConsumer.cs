using System.Text;
using System.Text.Json;
using FiscalDocuments.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace FiscalDocuments.Infrastructure.Messaging;

/// <summary>
/// HostedService que é responsavel por consumir as mensagens do RabbitMQ.
/// </summary>
/// <param name="scopes"></param>
/// <param name="broker"></param>
/// <param name="logger"></param>
public sealed class RabbitConsumer(IServiceScopeFactory scopes, RabbitBroker broker, ILogger<RabbitConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return RunAsync(stoppingToken);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var channel = await broker.ConsumerChannelAsync(ct);
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, delivery) => await ConsumeAsync(channel, delivery, ct);
                await channel.BasicConsumeAsync(broker.ProcessingQueue, false, consumer, ct);
                while (channel.IsOpen && !ct.IsCancellationRequested)
                {
                    await Task.Delay(1000, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception)
            {
                logger.LogWarning("Consumer unavailable; reconnecting");
                await Task.Delay(5000, ct);
            }
        }
    }

    private async Task ConsumeAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        // A memória da entrega pertence ao RabbitMQ; copie os dados antes de qualquer continuação assíncrona.
        var payload = Encoding.UTF8.GetString(delivery.Body.ToArray());
        IntegrationEvent? message = null;
        try
        {
            try
            {
                var candidate = JsonSerializer.Deserialize<IntegrationEvent>(payload, JsonDefaults.Options)
                    ?? throw new PermanentProcessingException("invalid_event");
                EventProcessor.Validate(candidate);
                message = candidate;
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
                    var retryQueue = broker.ProcessingQueue + ".retry." + (attempt + 1);
                    await broker.PublishAsync(payload, retryQueue, broker.RetryExchange, attempt + 1, code, ct);
                }
                else
                {
                    if (message is not null)
                    {
                        await using var failureScope = scopes.CreateAsyncScope();
                        await failureScope.ServiceProvider.GetRequiredService<EventProcessor>().FailAsync(message, code, ct);
                    }
                    await broker.PublishAsync(payload, broker.ProcessingQueue, broker.DeadExchange, attempt, code, ct);
                    logger.LogWarning("Event {EventId} moved to DLQ; code {ErrorCode}", message?.EventId, code);
                }
            }
            // Também é seguro após a transferência durável confirmada para uma fila de nova tentativa ou para a DLQ,
            // mesmo sem sucesso no processamento de negócio. Nunca envie ACK antes de confirmar a transferência.
            await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* Fechar o canal provoca a reentrega. */ }
        catch (Exception)
        {
            logger.LogWarning("Delivery handoff/commit unconfirmed; returning message to broker");
            if (channel.IsOpen)
            {
                await channel.BasicNackAsync(delivery.DeliveryTag, false, true, ct);
            }

            await Task.Delay(1000, ct);
        }
    }

    public static int ReadAttempt(IDictionary<string, object?>? headers)
    {
        if (headers is null || !headers.TryGetValue("x-retry-attempt", out var value))
        {
            return 0;
        }

        return value switch { int number => Math.Max(0, number), long number => (int)Math.Clamp(number, 0, int.MaxValue), _ => 0 };
    }
}
