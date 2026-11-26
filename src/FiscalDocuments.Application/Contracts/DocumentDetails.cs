namespace FiscalDocuments.Application;

public sealed record DocumentDetails(DocumentItem Document, string SanitizedXml, Guid ProcessingId,
    string? ErrorCode, SummaryView? Summary);
