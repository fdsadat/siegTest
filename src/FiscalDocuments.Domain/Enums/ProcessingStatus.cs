namespace FiscalDocuments.Domain;

public enum ProcessingStatus
{
    PendingExtraction = 1,
    PendingSummary = 2,
    Completed = 3,
    Failed = 4
}
