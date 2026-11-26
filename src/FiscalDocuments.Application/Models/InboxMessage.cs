namespace FiscalDocuments.Application;

public sealed class InboxMessage
{
    public Guid EventId { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
}
