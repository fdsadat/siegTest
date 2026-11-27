using FiscalDocuments.Domain;

namespace FiscalDocuments.Application;

/// <summary>
/// Processa uma mensagem do RabbitMQ, cria uma transação e salva no Inbox. Posteriormente é enviado um ACK para o RabbitMQ.
/// </summary>
/// <param name="store"></param>
/// <param name="xml"></param>
/// <param name="clock"></param>
public sealed class EventProcessor(IWriteStore store, IXmlDocuments xml, TimeProvider clock)
{
    public async Task<bool> ProcessAsync(IntegrationEvent message, CancellationToken ct)
    {
        Validate(message);
        return await store.TransactionAsync(async () =>
        {
            await store.LockAsync($"document:{message.DocumentId}", ct);
            if (await store.HasInboxAsync(message.EventId, ct))
            {
                return false;
            }

            var document = await store.DocumentAsync(message.DocumentId, ct);
            var now = clock.GetUtcNow();
            if (document is null || document.IsDeleted)
            {
                Inbox(message.EventId, now);
                return false;
            }
            var processing = await store.ProcessingAsync(document.Id, ct);
            if (!processing.Matches(message.ContentRevision, message.ProcessingId) || processing.Status == ProcessingStatus.Completed)
            {
                Inbox(message.EventId, now);
                return false;
            }
            if (processing.Status == ProcessingStatus.Failed)
            {
                throw new PermanentProcessingException(processing.ErrorCode ?? "processing_failed");
            }

            var extracted = xml.ExtractSanitized(document.SanitizedXml);
            store.Add(new DocumentSummary { DocumentId = document.Id, ProcessingId = processing.ProcessingId,
                Competence = extracted.Competence, TotalAmountInCents = checked((long)(extracted.TotalAmount * 100m)), ProcessedAt = now });
            processing.Complete(now);
            Inbox(message.EventId, now);
            return true;
        }, ct);
    }

    public Task<bool> FailAsync(IntegrationEvent message, string errorCode, CancellationToken ct)
    {
        Validate(message);
        return store.TransactionAsync(async () =>
        {
            await store.LockAsync($"document:{message.DocumentId}", ct);
            var document = await store.DocumentAsync(message.DocumentId, ct);
            if (document is null || document.IsDeleted)
            {
                return false;
            }

            var processing = await store.ProcessingAsync(document.Id, ct);
            if (!processing.Matches(message.ContentRevision, message.ProcessingId) || processing.Status == ProcessingStatus.Completed)
            {
                return false;
            }
            // Uma entrega pode chegar à DLQ após o commit da transação de negócio,
            // caso seu ACK se perca. A reconciliação não deve marcar como falha um processamento já concluído.
            if (await store.HasInboxAsync(message.EventId, ct))
            {
                return false;
            }

            if (processing.Status != ProcessingStatus.Failed)
            {
                processing.Fail(errorCode, clock.GetUtcNow());
            }

            if (!await store.HasFailureAsync(message.ProcessingId, ct))
            {
                store.Add(new ProcessingFailure
                {
                    DocumentId = document.Id,
                    ProcessingId = message.ProcessingId,
                    ContentRevision = message.ContentRevision,
                    ErrorCode = errorCode,
                    FailedAt = clock.GetUtcNow()
                });
            }

            return true;
        }, ct);
    }

    private void Inbox(Guid eventId, DateTimeOffset now)
    {
        store.Add(new InboxMessage
        { EventId = eventId, CompletedAt = now });
    }

    public static void Validate(IntegrationEvent message)
    {
        if (message.EventType != IntegrationEvent.Received)
        {
            throw new PermanentProcessingException("unsupported_event");
        }
        if (message.EventId == Guid.Empty || message.DocumentId == Guid.Empty || message.ProcessingId == Guid.Empty || message.ContentRevision < 1)
        {
            throw new PermanentProcessingException("invalid_event");
        }
    }
}
