using FiscalDocuments.Domain;

namespace FiscalDocuments.Application;

public sealed class EventProcessor(IWriteStore store, IXmlDocuments xml, TimeProvider clock)
{
    public async Task<bool> ProcessAsync(IntegrationEvent message, CancellationToken ct)
    {
        Validate(message);
        ExtractedData? extracted = null;
        if (message.EventType == IntegrationEvent.Received)
        {
            var snapshot = await store.DocumentAsync(message.DocumentId, ct);
            if (snapshot is not null && !snapshot.IsDeleted && snapshot.ContentRevision == message.ContentRevision)
                extracted = xml.ExtractSanitized(snapshot.SanitizedXml);
        }
        return await store.TransactionAsync(async () =>
        {
            var consumer = Consumer(message.EventType);
            await store.LockAsync($"document:{message.DocumentId}", ct);
            if (await store.HasInboxAsync(consumer, message.EventId, ct)) return false;
            var document = await store.DocumentAsync(message.DocumentId, ct);
            var now = clock.GetUtcNow();
            if (document is null || document.IsDeleted)
            {
                Inbox(consumer, message.EventId, now);
                return false;
            }
            var processing = await store.ProcessingAsync(document.Id, ct);
            if (!processing.Matches(message.ContentRevision, message.ProcessingId))
            {
                Inbox(consumer, message.EventId, now);
                return false;
            }
            if (processing.Status == ProcessingStatus.Failed)
                throw new PermanentProcessingException(processing.ErrorCode ?? "processing_failed");
            if (message.EventType == IntegrationEvent.Received && processing.Status == ProcessingStatus.PendingExtraction)
            {
                if (extracted is null) throw new PermanentProcessingException("missing_extraction_snapshot");
                processing.Extract(extracted.TotalAmount, extracted.Competence, now);
                store.Add(IntegrationEvent.Create(IntegrationEvent.Processed, document, processing, now, message.CorrelationId));
            }
            else if (message.EventType == IntegrationEvent.Processed && processing.Status == ProcessingStatus.PendingSummary)
            {
                if (processing.TotalAmount is null || processing.Competence is null)
                    throw new PermanentProcessingException("missing_extraction");
                store.Add(new DocumentSummary { DocumentId = document.Id, ProcessingId = processing.ProcessingId,
                    Competence = processing.Competence, TotalAmountInCents = checked((long)(processing.TotalAmount.Value * 100m)), ProcessedAt = now });
                processing.Complete(now);
            }
            else if (message.EventType == IntegrationEvent.Processed && processing.Status == ProcessingStatus.PendingExtraction)
                throw new InvalidOperationException("extraction_not_ready");
            Inbox(consumer, message.EventId, now);
            return true;
        }, ct);
    }

    public Task<bool> FailAsync(IntegrationEvent message, string errorCode, CancellationToken ct) =>
        store.TransactionAsync(async () =>
        {
            await store.LockAsync($"document:{message.DocumentId}", ct);
            var document = await store.DocumentAsync(message.DocumentId, ct);
            if (document is null || document.IsDeleted) return false;
            var processing = await store.ProcessingAsync(document.Id, ct);
            if (!processing.Matches(message.ContentRevision, message.ProcessingId) || processing.Status == ProcessingStatus.Completed)
                return false;
            // Uma entrega pode chegar à DLQ após o commit da transação de negócio,
            // caso seu ACK se perca. A reconciliação não deve marcar como falha uma etapa já concluída.
            if (await store.HasInboxAsync(Consumer(message.EventType), message.EventId, ct)) return false;
            if (message.EventType == IntegrationEvent.Received && processing.Status == ProcessingStatus.PendingSummary) return false;
            if (processing.Status != ProcessingStatus.Failed) processing.Fail(errorCode, clock.GetUtcNow());
            if (!await store.HasFailureAsync(message.ProcessingId, message.EventType, ct))
                store.Add(new ProcessingFailure { DocumentId = document.Id, ProcessingId = message.ProcessingId,
                    ContentRevision = message.ContentRevision, Stage = message.EventType, ErrorCode = errorCode, FailedAt = clock.GetUtcNow() });
            return true;
        }, ct);

    private void Inbox(string consumer, Guid eventId, DateTimeOffset now) => store.Add(new InboxMessage
        { Consumer = consumer, EventId = eventId, CompletedAt = now });

    public static string Consumer(string eventType) => eventType switch
    {
        IntegrationEvent.Received => "extraction",
        IntegrationEvent.Processed => "summary",
        _ => throw new PermanentProcessingException("unsupported_event")
    };
    public static void Validate(IntegrationEvent message)
    {
        _ = Consumer(message.EventType);
        if (message.EventId == Guid.Empty || message.DocumentId == Guid.Empty || message.ProcessingId == Guid.Empty || message.ContentRevision < 1)
            throw new PermanentProcessingException("invalid_event");
    }
}
