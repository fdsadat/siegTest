using System.Text.Json;
using FiscalDocuments.Application;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace FiscalDocuments.Infrastructure.Messaging;

public static class DlqReconciler
{
    // Explicit operator command, not an always-on collector. SQL commit precedes ACK.
    public static async Task<int> ReconcileAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var broker = services.GetRequiredService<RabbitBroker>();
        await using var channel = await broker.ConsumerChannelAsync(ct);
        var count = 0;
        foreach (var eventType in new[] { IntegrationEvent.Received, IntegrationEvent.Processed })
        {
            for (var i = 0; i < 100; i++)
            {
                var delivery = await channel.BasicGetAsync(broker.Queue(eventType) + ".dead", false, ct);
                if (delivery is null) break;
                try
                {
                    var message = JsonSerializer.Deserialize<IntegrationEvent>(delivery.Body.Span, JsonDefaults.Options)
                        ?? throw new PermanentProcessingException("invalid_event");
                    EventProcessor.Validate(message);
                    await using var scope = services.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<EventProcessor>().FailAsync(message, "reconciled_from_dlq", ct);
                    await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
                    count++;
                }
                catch
                {
                    await channel.BasicNackAsync(delivery.DeliveryTag, false, true, ct);
                    throw new InvalidOperationException("DLQ item could not be reconciled; delivery retained. Inspect safely using broker administration.");
                }
            }
        }
        return count;
    }
}
