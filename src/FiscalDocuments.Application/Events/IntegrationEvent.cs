using System.Text.Json;
using FiscalDocuments.Domain;

namespace FiscalDocuments.Application;

public sealed record IntegrationEvent(Guid EventId, string EventType, Guid DocumentId,
    int ContentRevision, Guid ProcessingId, DateTimeOffset OccurredAt, string CorrelationId)
{
    public const string Received = "document.received.v1";
    public static OutboxMessage Create(FiscalDocument document, DocumentProcessing processing,
        DateTimeOffset now, string correlationId)
    {
        var message = new IntegrationEvent(Guid.NewGuid(), Received, document.Id, document.ContentRevision,
            processing.ProcessingId, now, correlationId);
        return new OutboxMessage { Id = message.EventId, EventType = Received, ProcessingId = message.ProcessingId,
            Payload = JsonSerializer.Serialize(message, JsonDefaults.Options), CreatedAt = now, AvailableAt = now };
    }
}
