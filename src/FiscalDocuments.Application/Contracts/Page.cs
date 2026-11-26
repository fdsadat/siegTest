namespace FiscalDocuments.Application;

public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);
