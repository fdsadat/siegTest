using FiscalDocuments.Domain;

namespace FiscalDocuments.Application;

// A transaction-scoped port. Application code owns use cases and domain transitions;
// Infrastructure supplies SQL transactions, coordination locks and persistence.
public interface IWriteStore
{
    Task<T> TransactionAsync<T>(Func<Task<T>> action, CancellationToken ct);
    Task LockAsync(string resource, CancellationToken ct);
    Task<FiscalDocument?> DocumentAsync(Guid id, CancellationToken ct);
    Task<FiscalDocument?> FindIdentityAsync(DocumentType type, string identityHmac, CancellationToken ct);
    Task<DocumentProcessing> ProcessingAsync(Guid documentId, CancellationToken ct);
    Task<UploadReceipt?> ReceiptAsync(Guid documentId, string contentHmac, CancellationToken ct);
    Task<IdempotentOperation?> OperationAsync(string scope, CancellationToken ct);
    Task<bool> HasInboxAsync(string consumer, Guid eventId, CancellationToken ct);
    Task<bool> HasFailureAsync(Guid processingId, string stage, CancellationToken ct);
    Task ClearSummaryAsync(Guid documentId, CancellationToken ct);
    void Add<T>(T entity) where T : class;
}
