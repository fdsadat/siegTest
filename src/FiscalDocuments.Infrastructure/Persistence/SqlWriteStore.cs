using FiscalDocuments.Application;
using FiscalDocuments.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FiscalDocuments.Infrastructure.Persistence;

/// <summary>
/// Centraliza o acesso de escrita ao SQL Server.
/// </summary>
/// <param name="db"></param>
public sealed class SqlWriteStore(FiscalDbContext db) : IWriteStore
{
    /// <summary>
    /// executa uma operação dentro de uma transação, salvando suas alterações em conjunto.
    /// Confirma a transação quando tudo funciona e permite repetir a execução diante de falhas temporárias reconhecidas pelo EF Core.
    /// Isso evita gravações parciais, como salvar um documento sem registrar seu evento no outbox.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="action"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    /// <exception cref="AppException"></exception>
    /// <exception cref="DuplicateWriteException"></exception>
    public async Task<T> TransactionAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var result = await action();
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return result;
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw new AppException("etag_mismatch", 412);
            }
            catch (DbUpdateException exception) when (ExpectedDuplicate(exception))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw new DuplicateWriteException();
            }
        });
    }

    /// <summary>
    /// Alterações de documentos, commits dos consumidores e reprocessamentos usam um bloqueio de curta duração na transaction. O HTTP usa rowversion.
    /// </summary>
    /// <param name="resource"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public async Task LockAsync(string resource, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive',
                @LockOwner='Transaction', @LockTimeout=10000;
            IF @result < 0 THROW 51000, 'coordination_timeout', 1;
            """, ct);
    }

    public Task<FiscalDocument?> DocumentAsync(Guid id, CancellationToken ct)
    {
        return db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
    }

    public Task<FiscalDocument?> FindIdentityAsync(DocumentType type, string identityHmac, CancellationToken ct)
    {
        return db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Type == type && d.IdentityHmac == identityHmac, ct);
    }

    public Task<DocumentProcessing> ProcessingAsync(Guid documentId, CancellationToken ct)
    {
        return db.Processing.SingleAsync(p => p.DocumentId == documentId, ct);
    }

    public Task<UploadReceipt?> ReceiptAsync(Guid documentId, string contentHmac, CancellationToken ct)
    {
        return db.Receipts.AsNoTracking().SingleOrDefaultAsync(r => r.DocumentId == documentId && r.ContentHmac == contentHmac, ct);
    }

    /// <summary>
    /// Checa operações idempontentes.
    /// </summary>
    /// <param name="scope"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public Task<IdempotentOperation?> OperationAsync(string scope, CancellationToken ct)
    {
        return db.Operations.SingleOrDefaultAsync(o => o.Scope == scope, ct);
    }

    public Task<bool> HasInboxAsync(Guid eventId, CancellationToken ct)
    {
        return db.Inbox.AnyAsync(i => i.EventId == eventId, ct);
    }

    public Task<bool> HasFailureAsync(Guid processingId, CancellationToken ct)
    {
        return db.Failures.AnyAsync(f => f.ProcessingId == processingId, ct);
    }

    public async Task ClearSummaryAsync(Guid documentId, CancellationToken ct)
    {
        var summary = await db.Summaries.SingleOrDefaultAsync(s => s.DocumentId == documentId, ct);
        if (summary is not null)
        {
            db.Summaries.Remove(summary);
        }
    }
    public void Add<T>(T entity) where T : class
    {
        db.Add(entity);
    }

    private static bool ExpectedDuplicate(DbUpdateException exception)
    {
        return exception.InnerException is SqlException sql
        && sql.Number is 2601 or 2627
        && new[] { "UX_Documents_Identity", "UX_Receipts_Content", "UX_Outbox_RunEvent", "UX_Failures_Run" }
            .Any(name => sql.Message.Contains(name, StringComparison.Ordinal));
    }
}
