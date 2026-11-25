using FiscalDocuments.Application;
using FiscalDocuments.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FiscalDocuments.Infrastructure.Persistence;

public sealed class SqlWriteStore(FiscalDbContext db) : IWriteStore
{
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

    // All document mutations, consumer commits and replays take this same short,
    // transaction-owned coordination lock. HTTP concurrency still uses rowversion.
    public async Task LockAsync(string resource, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive',
                @LockOwner='Transaction', @LockTimeout=10000;
            IF @result < 0 THROW 51000, 'coordination_timeout', 1;
            """, ct);
    }

    public Task<FiscalDocument?> DocumentAsync(Guid id, CancellationToken ct) => db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
    public Task<FiscalDocument?> FindIdentityAsync(DocumentType type, string identityHmac, CancellationToken ct) =>
        db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Type == type && d.IdentityHmac == identityHmac, ct);
    public Task<DocumentProcessing> ProcessingAsync(Guid documentId, CancellationToken ct) => db.Processing.SingleAsync(p => p.DocumentId == documentId, ct);
    public Task<UploadReceipt?> ReceiptAsync(Guid documentId, string contentHmac, CancellationToken ct) =>
        db.Receipts.AsNoTracking().SingleOrDefaultAsync(r => r.DocumentId == documentId && r.ContentHmac == contentHmac, ct);
    public Task<IdempotentOperation?> OperationAsync(string scope, CancellationToken ct) => db.Operations.SingleOrDefaultAsync(o => o.Scope == scope, ct);
    public Task<bool> HasInboxAsync(string consumer, Guid eventId, CancellationToken ct) => db.Inbox.AnyAsync(i => i.Consumer == consumer && i.EventId == eventId, ct);
    public Task<bool> HasFailureAsync(Guid processingId, string stage, CancellationToken ct) => db.Failures.AnyAsync(f => f.ProcessingId == processingId && f.Stage == stage, ct);
    public async Task ClearSummaryAsync(Guid documentId, CancellationToken ct)
    {
        var summary = await db.Summaries.SingleOrDefaultAsync(s => s.DocumentId == documentId, ct);
        if (summary is not null) db.Summaries.Remove(summary);
    }
    public void Add<T>(T entity) where T : class => db.Add(entity);

    private static bool ExpectedDuplicate(DbUpdateException exception) => exception.InnerException is SqlException sql
        && sql.Number is 2601 or 2627
        && new[] { "UX_Documents_Identity", "UX_Receipts_Content", "UX_Outbox_RunEvent", "UX_Failures_RunStage" }
            .Any(name => sql.Message.Contains(name, StringComparison.Ordinal));
}
