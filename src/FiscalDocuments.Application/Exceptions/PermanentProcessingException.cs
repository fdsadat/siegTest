namespace FiscalDocuments.Application;

public sealed class PermanentProcessingException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
