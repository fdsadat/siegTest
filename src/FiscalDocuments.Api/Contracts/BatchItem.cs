using FiscalDocuments.Application;

namespace FiscalDocuments.Api;

public sealed record BatchItem(int InputIndex, int StatusCode, Acceptance? Document, string? ErrorCode);
