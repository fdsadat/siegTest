using FiscalDocuments.Domain;

namespace FiscalDocuments.Application;

public sealed record DocumentFilter(DateTimeOffset? From, DateTimeOffset? To, string? IssuerCnpj,
    string? State, DocumentType? Type, ProcessingStatus? Status, int PageSize = 20, string? Cursor = null);
