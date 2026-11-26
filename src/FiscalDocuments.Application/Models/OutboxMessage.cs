namespace FiscalDocuments.Application;

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
