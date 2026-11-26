using FiscalDocuments.Infrastructure.Messaging;
using FiscalDocuments.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FiscalDocuments.Infrastructure;

public static class LocalDataReset
{
    public static async Task<int> ResetAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FiscalDbContext>();
        var broker = services.GetRequiredService<RabbitBroker>();
        var sql = db.Database.GetDbConnection();
        if (string.IsNullOrWhiteSpace(sql.Database) || new[] { "master", "model", "msdb", "tempdb" }.Contains(sql.Database, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Informe um banco da aplicação; bancos de sistema não podem ser limpos.");
        }

        Console.WriteLine($"Destino SQL: {sql.DataSource}/{sql.Database}; filas: {broker.ProcessingQueue} e auxiliares.");
        await using var channel = await broker.ConsumerChannelAsync(ct);
        var queues = new[] { broker.ProcessingQueue, broker.ProcessingQueue + ".dead" }
            .Concat(Enumerable.Range(1, RabbitBroker.RetryDelays.Length).Select(i => broker.ProcessingQueue + ".retry." + i)).ToArray();
        foreach (var queue in queues)
        {
            var state = await channel.QueueDeclarePassiveAsync(queue, ct);
            if (state.ConsumerCount > 0)
            {
                throw new InvalidOperationException("Pare todos os workers antes de limpar os dados.");
            }
        }
        // Confere o banco antes de começar a esvaziar as filas.
        if (!await db.Database.CanConnectAsync(ct) || (await db.Database.GetPendingMigrationsAsync(ct)).Any())
        {
            throw new InvalidOperationException("Inicialize o banco e aplique as migrations antes da limpeza.");
        }

        foreach (var queue in queues)
        {
            await channel.QueuePurgeAsync(queue, ct);
        }

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var deleted = await db.Failures.ExecuteDeleteAsync(ct);
            deleted += await db.Summaries.ExecuteDeleteAsync(ct);
            deleted += await db.Receipts.ExecuteDeleteAsync(ct);
            deleted += await db.Processing.ExecuteDeleteAsync(ct);
            deleted += await db.Inbox.ExecuteDeleteAsync(ct);
            deleted += await db.Operations.ExecuteDeleteAsync(ct);
            deleted += await db.Outbox.ExecuteDeleteAsync(ct);
            deleted += await db.Documents.ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct);
            return deleted;
        });
    }
}
