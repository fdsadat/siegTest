using MediatR;

namespace FiscalDocuments.Application;

public sealed record GetDocument(Guid Id) : IRequest<DocumentDetails?>;
