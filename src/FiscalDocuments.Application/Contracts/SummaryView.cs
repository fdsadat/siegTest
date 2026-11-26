namespace FiscalDocuments.Application;

public sealed record SummaryView(string Competence, long TotalAmountInCents, DateTimeOffset ProcessedAt);
