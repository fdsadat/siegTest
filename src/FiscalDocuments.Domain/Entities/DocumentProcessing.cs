namespace FiscalDocuments.Domain;

public sealed class DocumentProcessing
{
    private DocumentProcessing() { }
    public Guid DocumentId { get; private set; }
    public Guid ProcessingId { get; private set; }
    public int ContentRevision { get; private set; }
    public ProcessingStatus Status { get; private set; }
    public decimal? TotalAmount { get; private set; }
    public string? Competence { get; private set; }
    public string? ErrorCode { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public bool IsTerminal => Status is ProcessingStatus.Completed or ProcessingStatus.Failed;

    public static DocumentProcessing Start(FiscalDocument document, DateTimeOffset now)
    {
        var processing = new DocumentProcessing { DocumentId = document.Id };
        processing.Restart(document.ContentRevision, now);
        return processing;
    }

    public bool Matches(int revision, Guid processingId) => ContentRevision == revision && ProcessingId == processingId;

    public void Restart(int revision, DateTimeOffset now)
    {
        ProcessingId = Guid.NewGuid();
        ContentRevision = revision;
        Status = ProcessingStatus.PendingExtraction;
        TotalAmount = null;
        Competence = null;
        ErrorCode = null;
        UpdatedAt = now;
    }

    public void Extract(decimal totalAmount, string competence, DateTimeOffset now)
    {
        if (Status != ProcessingStatus.PendingExtraction)
        {
            throw new DomainException("invalid_processing_transition");
        }

        TotalAmount = totalAmount;
        Competence = competence;
        Status = ProcessingStatus.PendingSummary;
        UpdatedAt = now;
    }

    public void Complete(DateTimeOffset now)
    {
        if (Status != ProcessingStatus.PendingSummary)
        {
            throw new DomainException("invalid_processing_transition");
        }

        Status = ProcessingStatus.Completed;
        UpdatedAt = now;
    }

    public void Fail(string errorCode, DateTimeOffset now)
    {
        if (Status == ProcessingStatus.Completed)
        {
            return;
        }

        Status = ProcessingStatus.Failed;
        ErrorCode = errorCode;
        UpdatedAt = now;
    }
}
