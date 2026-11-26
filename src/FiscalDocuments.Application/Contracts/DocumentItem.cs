using FiscalDocuments.Domain;

namespace FiscalDocuments.Application;

public sealed record DocumentItem(Guid Id, DocumentType Type, string IssuerMasked, DateTimeOffset IssuedAt,
    string State, ProcessingStatus ProcessingStatus, int ContentRevision, string ETag);
