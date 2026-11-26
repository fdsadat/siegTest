namespace FiscalDocuments.Application;

public sealed class IdempotentOperation
{
    public string Scope { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public int StatusCode { get; set; }
    public string? ResponseJson { get; set; }
    public string? ETag { get; set; }
    public string? Location { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
