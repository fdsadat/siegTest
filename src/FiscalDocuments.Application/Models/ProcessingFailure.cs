namespace FiscalDocuments.Application;

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
