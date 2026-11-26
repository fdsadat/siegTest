using MediatR;

namespace FiscalDocuments.Application;

public sealed record ReceiveDocument(byte[] Xml, string CorrelationId) : IRequest<Acceptance>, IValidatedRequest
{
    public void Validate()
    {
        RequestValidation.Xml(Xml);
    }
}
