namespace FiscalDocuments.Application;

public interface IDocumentQueries
{
    Task<Page<DocumentItem>> ListAsync(DocumentFilter filter, CancellationToken ct);
    Task<DocumentDetails?> DetailsAsync(Guid id, CancellationToken ct);
}
