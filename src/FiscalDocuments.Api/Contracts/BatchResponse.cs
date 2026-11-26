namespace FiscalDocuments.Api;

public sealed record BatchResponse(IReadOnlyList<BatchItem> Results);
