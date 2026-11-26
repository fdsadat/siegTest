using MediatR;

namespace FiscalDocuments.Application;

public sealed record ReplaceDocument(Guid Id, byte[] Xml, string? IfMatch, string? IdempotencyKey,
    string Subject, string CorrelationId) : IRequest<MutationResult>, IValidatedRequest
{
    public void Validate() 
    { 
        RequestValidation.Xml(Xml); 
        _ = ETags.Parse(IfMatch); 
        RequestValidation.Key(IdempotencyKey); 
    }
}
