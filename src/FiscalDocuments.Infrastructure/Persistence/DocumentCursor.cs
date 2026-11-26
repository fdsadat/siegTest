namespace FiscalDocuments.Infrastructure.Persistence;

internal sealed record DocumentCursor(DateTimeOffset IssuedAt, Guid Id, string FilterHash);
