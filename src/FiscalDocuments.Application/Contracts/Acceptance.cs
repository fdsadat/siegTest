using FiscalDocuments.Domain;

namespace FiscalDocuments.Application;

public sealed record Acceptance(Guid DocumentId, string Disposition, int CurrentContentRevision,
    ProcessingStatus CurrentProcessingStatus, bool IsCurrentContent);
