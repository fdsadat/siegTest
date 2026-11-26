namespace FiscalDocuments.Application;

public sealed class InboxMessage
{
    public string Consumer { get; set; } = "";
    public Guid EventId { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
}
