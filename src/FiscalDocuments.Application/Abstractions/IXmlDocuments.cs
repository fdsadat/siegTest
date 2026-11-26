using FiscalDocuments.Domain;

namespace FiscalDocuments.Application;

public interface IXmlDocuments
{
    FiscalXml ValidateAndSanitize(ReadOnlyMemory<byte> bytes);
    ExtractedData ExtractSanitized(string xml);
}
