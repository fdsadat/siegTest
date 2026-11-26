using MediatR;

namespace FiscalDocuments.Application;

public sealed record ListDocuments(DocumentFilter Filter) : IRequest<Page<DocumentItem>>;
