namespace FiscalDocuments.Application;

public static class RequestValidation
{
    public const int MaxFileBytes = 2 * 1024 * 1024;
    public const int MaxRequestBytes = 21 * 1024 * 1024;
    public const long MaxBatchBytes = 20L * 1024 * 1024;
    public static void Xml(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            throw new AppException("empty_xml");
        }

        if (bytes.Length > MaxFileBytes)
        {
            throw new AppException("file_too_large", 413);
        }
    }
    public static void Key(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length is < 8 or > 128 || key.Any(c => c < 33 || c > 126))
        {
            throw new AppException("invalid_idempotency_key");
        }
    }
}
