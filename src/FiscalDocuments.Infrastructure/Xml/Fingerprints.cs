using System.Security.Cryptography;
using System.Text;
using FiscalDocuments.Application;
using Microsoft.Extensions.Options;

namespace FiscalDocuments.Infrastructure.Xml;

public sealed class Fingerprints : IFingerprints
{
    private readonly byte[] key;
    public Fingerprints(IOptions<PrivacyOptions> options)
    {
        try { key = Convert.FromBase64String(options.Value.HmacKey); }
        catch (FormatException) { throw new InvalidOperationException("Privacy:HmacKey must be base64."); }
        if (key.Length < 32) throw new InvalidOperationException("Privacy:HmacKey requires at least 32 random bytes.");
    }
    public string Hash(ReadOnlySpan<byte> value) => Convert.ToHexString(HMACSHA256.HashData(key, value));
    public string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    public string TaxId(string value)
    {
        // Accept punctuation, reject arbitrary text rather than silently deleting it.
        if (value.Any(c => !char.IsAsciiDigit(c) && c is not '.' and not '/' and not '-' and not ' '))
            throw new AppException("invalid_tax_id");
        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length is not (11 or 14)) throw new AppException("invalid_tax_id");
        return Hash("tax-id:" + digits);
    }
}
