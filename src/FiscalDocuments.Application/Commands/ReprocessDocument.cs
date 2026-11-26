using MediatR;

namespace FiscalDocuments.Application;

public sealed record ReprocessDocument(Guid Id, string? IdempotencyKey, string Subject, string CorrelationId)
    : IRequest<MutationResult>, IValidatedRequest
{
    public void Validate() => RequestValidation.Key(IdempotencyKey);
}
