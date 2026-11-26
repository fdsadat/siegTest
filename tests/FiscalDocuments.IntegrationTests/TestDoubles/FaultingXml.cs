using FiscalDocuments.Application;
using FiscalDocuments.Domain;

namespace FiscalDocuments.IntegrationTests;

internal sealed class FaultingXml(IXmlDocuments inner, bool permanent) : IXmlDocuments
{
    public int Calls;
    public FiscalXml ValidateAndSanitize(ReadOnlyMemory<byte> bytes)
    {
        return inner.ValidateAndSanitize(bytes);
    }

    public ExtractedData ExtractSanitized(string xml)
    {
        var call = Interlocked.Increment(ref Calls);
        if (permanent)
        {
            throw new PermanentProcessingException("simulated_permanent_failure");
        }

        if (call == 1)
        {
            throw new InvalidOperationException("simulated_transient_failure");
        }

        return inner.ExtractSanitized(xml);
    }
}
