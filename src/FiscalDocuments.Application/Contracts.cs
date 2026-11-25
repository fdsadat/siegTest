using System.Text.Json;
using System.Text.Json.Serialization;
using FiscalDocuments.Domain;

namespace FiscalDocuments.Application;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed class AppException(string code, int statusCode = 400) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
public sealed class DuplicateWriteException : Exception;
public sealed class PermanentProcessingException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record Acceptance(Guid DocumentId, string Disposition, int CurrentContentRevision,
    ProcessingStatus CurrentProcessingStatus, bool IsCurrentContent);
public sealed record MutationResult(int StatusCode, object? Body = null, string? ETag = null, string? Location = null);
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);
public sealed record DocumentItem(Guid Id, DocumentType Type, string IssuerMasked, DateTimeOffset IssuedAt,
    string State, ProcessingStatus ProcessingStatus, int ContentRevision, string ETag);
public sealed record DocumentDetails(DocumentItem Document, string SanitizedXml, Guid ProcessingId,
    string? ErrorCode, SummaryView? Summary);
public sealed record SummaryView(string Competence, long TotalAmountInCents, DateTimeOffset ProcessedAt);
public sealed record DocumentFilter(DateTimeOffset? From, DateTimeOffset? To, string? IssuerCnpj,
    string? State, DocumentType? Type, ProcessingStatus? Status, int PageSize = 20, string? Cursor = null);
public sealed record ExtractedData(decimal TotalAmount, string Competence);

public interface IXmlDocuments
{
    FiscalXml ValidateAndSanitize(ReadOnlyMemory<byte> bytes);
    ExtractedData ExtractSanitized(string xml);
}
public interface IFingerprints
{
    string Hash(ReadOnlySpan<byte> value);
    string Hash(string value);
    string TaxId(string value);
}

public static class ETags
{
    public static string Format(byte[] version) => $"\"{Convert.ToBase64String(version)}\"";
    public static byte[] Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new AppException("if_match_required", 428);
        try
        {
            if (value.Length < 3 || value[0] != '"' || value[^1] != '"') throw new FormatException();
            var bytes = Convert.FromBase64String(value[1..^1]);
            if (bytes.Length != 8) throw new FormatException();
            return bytes;
        }
        catch (FormatException) { throw new AppException("invalid_etag"); }
    }
}

public interface IDocumentQueries
{
    Task<Page<DocumentItem>> ListAsync(DocumentFilter filter, CancellationToken ct);
    Task<DocumentDetails?> DetailsAsync(Guid id, CancellationToken ct);
}

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

public sealed class UploadReceipt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public string ContentHmac { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
}
public sealed class IdempotentOperation
{
    public string Scope { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public int StatusCode { get; set; }
    public string? ResponseJson { get; set; }
    public string? ETag { get; set; }
    public string? Location { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = "";
    public Guid ProcessingId { get; set; }
    public string Payload { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public int Attempts { get; set; }
}
public sealed class InboxMessage
{
    public string Consumer { get; set; } = "";
    public Guid EventId { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
}
public sealed class ProcessingFailure
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public Guid ProcessingId { get; set; }
    public int ContentRevision { get; set; }
    public string Stage { get; set; } = "";
    public string ErrorCode { get; set; } = "";
    public DateTimeOffset FailedAt { get; set; }
}
public sealed class DocumentSummary
{
    public Guid DocumentId { get; set; }
    public Guid ProcessingId { get; set; }
    public string Competence { get; set; } = "";
    public long TotalAmountInCents { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}

public sealed record IntegrationEvent(Guid EventId, string EventType, Guid DocumentId,
    int ContentRevision, Guid ProcessingId, DateTimeOffset OccurredAt, string CorrelationId)
{
    public const string Received = "document.received.v1";
    public const string Processed = "document.processed.v1";
    public static OutboxMessage Create(string type, FiscalDocument document, DocumentProcessing processing,
        DateTimeOffset now, string correlationId)
    {
        var message = new IntegrationEvent(Guid.NewGuid(), type, document.Id, document.ContentRevision,
            processing.ProcessingId, now, correlationId);
        return new OutboxMessage { Id = message.EventId, EventType = type, ProcessingId = message.ProcessingId,
            Payload = JsonSerializer.Serialize(message, JsonDefaults.Options), CreatedAt = now, AvailableAt = now };
    }
}
