namespace FiscalDocuments.Application;

public sealed class DocumentSummary
{
    public Guid DocumentId { get; set; }
    public Guid ProcessingId { get; set; }
    public string Competence { get; set; } = "";
    public long TotalAmountInCents { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}
