using MediatR;

namespace FiscalDocuments.Application;

public sealed record DeleteDocument(Guid Id, string? IfMatch, string? IdempotencyKey, string Subject)
    : IRequest<MutationResult>, IValidatedRequest
{
    public void Validate() { _ = ETags.Parse(IfMatch); RequestValidation.Key(IdempotencyKey); }
}
