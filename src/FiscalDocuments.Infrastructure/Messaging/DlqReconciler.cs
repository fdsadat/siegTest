using System.Text.Json;
using FiscalDocuments.Application;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace FiscalDocuments.Infrastructure.Messaging;

public static class DlqReconciler
{
    /// <summary>
    /// Consulta a fila de falhas (DLQ) e atualiza o banco com segurança. Só confirma a retirada da mensagem após concluir a operação no SQL.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static async Task<int> ReconcileAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var broker = services.GetRequiredService<RabbitBroker>();
        await using var channel = await broker.ConsumerChannelAsync(ct);
        var count = 0;
        for (var i = 0; i < 100; i++)
        {
            var delivery = await channel.BasicGetAsync(broker.ProcessingQueue + ".dead", false, ct);
            if (delivery is null)
            {
                break;
            }

            try
            {
                var message = JsonSerializer.Deserialize<IntegrationEvent>(delivery.Body.Span, JsonDefaults.Options) ?? throw new PermanentProcessingException("invalid_event");
                EventProcessor.Validate(message);
                await using var scope = services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<EventProcessor>().FailAsync(message, "reconciled_from_dlq", ct);
                await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
                count++;
            }
            catch
            {
                await channel.BasicNackAsync(delivery.DeliveryTag, false, true, ct);
                throw new InvalidOperationException("DLQ não conseguiu ser reconciliado; entrega retida.");
            }
        }
        return count;
    }
}
