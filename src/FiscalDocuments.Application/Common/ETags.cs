namespace FiscalDocuments.Application;

public static class ETags
{
    public static string Format(byte[] version) => $"\"{Convert.ToBase64String(version)}\"";
    public static byte[] Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AppException("if_match_required", 428);
        }

        try
        {
            if (value.Length < 3 || value[0] != '"' || value[^1] != '"')
            {
                throw new FormatException();
            }

            var bytes = Convert.FromBase64String(value[1..^1]);
            if (bytes.Length != 8)
            {
                throw new FormatException();
            }

            return bytes;
        }
        catch (FormatException) { throw new AppException("invalid_etag"); }
    }
}
