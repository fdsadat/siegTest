using FiscalDocuments.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FiscalDocuments.Infrastructure.Messaging;

public sealed class OutboxDispatcher(IServiceScopeFactory scopes, RabbitBroker broker, TimeProvider clock,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await DispatchOneAsync(stoppingToken)) await Task.Delay(1000, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                logger.LogWarning("Outbox unavailable; retrying without discarding pending events");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    public async Task<bool> DispatchOneAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FiscalDbContext>();
        // Cria uma coleira temporaria no SQL Server para garantir que apenas uma instancia do serviço publique a mesma mensagem.
        var lease = Guid.NewGuid();
        var now = clock.GetUtcNow();
        // Tempo da coleira, que pode ser adiado se necessario.
        var leaseUntil = now.AddMinutes(2);
        // The captured lease makes an ambiguous command retry idempotent.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            IF NOT EXISTS (SELECT 1 FROM OutboxMessages WHERE LeaseId={lease})
            BEGIN
                ;WITH candidate AS (
                    SELECT TOP (1) * FROM OutboxMessages WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK)
                    WHERE PublishedAt IS NULL AND AvailableAt <= {now}
                        AND (LeaseUntil IS NULL OR LeaseUntil < {now})
                    ORDER BY CreatedAt, Id
                )
                UPDATE candidate SET LeaseId={lease}, LeaseUntil={leaseUntil};
            END
            """, ct);
        var message = await db.Outbox.AsNoTracking().SingleOrDefaultAsync(m => m.LeaseId == lease, ct);
        if (message is null) return false;
        try
        {
            await broker.PublishAsync(message.Payload, message.EventType, broker.EventsExchange, 0, null, ct);
            await db.Outbox.Where(m => m.Id == message.Id && m.LeaseId == lease).ExecuteUpdateAsync(setters => setters
                .SetProperty(m => m.PublishedAt, clock.GetUtcNow()).SetProperty(m => m.LeaseId, (Guid?)null)
                .SetProperty(m => m.LeaseUntil, (DateTimeOffset?)null), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            logger.LogWarning("Publish not confirmed for event {EventId}; kept in outbox", message.Id);
            var next = clock.GetUtcNow().AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(message.Attempts, 8))));
            await db.Outbox.Where(m => m.Id == message.Id && m.LeaseId == lease).ExecuteUpdateAsync(setters => setters
                .SetProperty(m => m.Attempts, m => m.Attempts + 1).SetProperty(m => m.AvailableAt, next)
                .SetProperty(m => m.LeaseId, (Guid?)null).SetProperty(m => m.LeaseUntil, (DateTimeOffset?)null), ct);
        }
        return true;
    }
}
