using System.Text;
using System.Text.Json;
using FiscalDocuments.Application;
using FiscalDocuments.Domain;
using Microsoft.EntityFrameworkCore;

namespace FiscalDocuments.Infrastructure.Persistence;

public sealed class SqlDocumentQueries(FiscalDbContext db, IFingerprints fingerprints) : IDocumentQueries
{
    public async Task<Page<DocumentItem>> ListAsync(DocumentFilter filter, CancellationToken ct)
    {
        if (filter.PageSize is < 1 or > 100) throw new AppException("invalid_page_size");
        if (filter.From.HasValue && filter.To.HasValue && filter.From >= filter.To) throw new AppException("invalid_date_range");
        if (filter.Type.HasValue && !Enum.IsDefined(filter.Type.Value) || filter.Status.HasValue && !Enum.IsDefined(filter.Status.Value))
            throw new AppException("invalid_filter");
        if (filter.State is not null && !Xml.FiscalXmlDocuments.IsState(filter.State)) throw new AppException("invalid_state");
        var issuerHmac = filter.IssuerCnpj is null ? null : fingerprints.TaxId(filter.IssuerCnpj);
        var filterHash = fingerprints.Hash(JsonSerializer.Serialize(new { filter.From, filter.To, issuerHmac, filter.State, filter.Type, filter.Status }));
        var query = from document in db.Documents.AsNoTracking()
                    join processing in db.Processing.AsNoTracking() on document.Id equals processing.DocumentId
                    where !document.IsDeleted
                    select new { Document = document, Processing = processing };
        if (filter.From.HasValue) query = query.Where(x => x.Document.IssuedAt >= filter.From);
        if (filter.To.HasValue) query = query.Where(x => x.Document.IssuedAt < filter.To);
        if (issuerHmac is not null) query = query.Where(x => x.Document.IssuerHmac == issuerHmac);
        if (filter.State is not null) query = query.Where(x => x.Document.State == filter.State);
        if (filter.Type.HasValue) query = query.Where(x => x.Document.Type == filter.Type);
        if (filter.Status.HasValue) query = query.Where(x => x.Processing.Status == filter.Status);
        if (filter.Cursor is not null)
        {
            DocumentCursor cursor;
            try
            {
                if (filter.Cursor.Length > 2048) throw new FormatException();
                cursor = JsonSerializer.Deserialize<DocumentCursor>(Encoding.UTF8.GetString(Convert.FromBase64String(filter.Cursor))) ?? throw new FormatException();
                if (cursor.FilterHash != filterHash) throw new FormatException();
            }
            catch (Exception exception) when (exception is FormatException or JsonException)
            { throw new AppException("invalid_cursor"); }
            query = query.Where(x => x.Document.IssuedAt < cursor.IssuedAt ||
                x.Document.IssuedAt == cursor.IssuedAt && x.Document.Id.CompareTo(cursor.Id) < 0);
        }
        var rows = await query.OrderByDescending(x => x.Document.IssuedAt).ThenByDescending(x => x.Document.Id)
            .Select(x => new { x.Document.Id, x.Document.Type, x.Document.IssuerMasked, x.Document.IssuedAt,
                x.Document.State, x.Processing.Status, x.Document.ContentRevision, x.Document.RowVersion })
            .Take(filter.PageSize + 1).ToListAsync(ct);
        var more = rows.Count > filter.PageSize;
        var items = rows.Take(filter.PageSize).Select(x => new DocumentItem(x.Id, x.Type, x.IssuerMasked,
            x.IssuedAt, x.State, x.Status, x.ContentRevision, ETags.Format(x.RowVersion))).ToList();
        var last = items.LastOrDefault();
        var next = more && last is not null ? Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new DocumentCursor(last.IssuedAt, last.Id, filterHash)))) : null;
        return new Page<DocumentItem>(items, next);
    }

    public async Task<DocumentDetails?> DetailsAsync(Guid id, CancellationToken ct)
    {
        // One query gives a coherent view of payload, processing state and summary.
        var row = await (from document in db.Documents.AsNoTracking()
                         join processing in db.Processing.AsNoTracking() on document.Id equals processing.DocumentId
                         join summary in db.Summaries.AsNoTracking() on document.Id equals summary.DocumentId into summaries
                         from summary in summaries.DefaultIfEmpty()
                         where document.Id == id && !document.IsDeleted
                         select new { Document = document, Processing = processing, Summary = summary }).SingleOrDefaultAsync(ct);
        if (row is null) return null;
        var d = row.Document;
        var p = row.Processing;
        return new DocumentDetails(new DocumentItem(d.Id, d.Type, d.IssuerMasked, d.IssuedAt, d.State, p.Status,
            d.ContentRevision, ETags.Format(d.RowVersion)), d.SanitizedXml, p.ProcessingId, p.ErrorCode,
            row.Summary is null ? null : new SummaryView(row.Summary.Competence, row.Summary.TotalAmountInCents, row.Summary.ProcessedAt));
    }
}
