namespace FiscalDocuments.Application;

public interface IFingerprints
{
    string Hash(ReadOnlySpan<byte> value);
    string Hash(string value);
    string TaxId(string value);
}
