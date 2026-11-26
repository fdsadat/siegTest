namespace FiscalDocuments.Application;

public sealed record MutationResult(int StatusCode, object? Body = null, string? ETag = null, string? Location = null);
