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
        if (filter.PageSize is < 1 or > 100)
        {
            throw new AppException("invalid_page_size");
        }

        if (filter.From.HasValue && filter.To.HasValue && filter.From >= filter.To)
        {
            throw new AppException("invalid_date_range");
        }

        if (filter.Type.HasValue && !Enum.IsDefined(filter.Type.Value) || filter.Status.HasValue && !Enum.IsDefined(filter.Status.Value))
        {
            throw new AppException("invalid_filter");
        }

        if (filter.State is { Length: > 2 })
        {
            throw new AppException("invalid_state");
        }

        var issuerHmac = filter.IssuerCnpj is null ? null : fingerprints.TaxId(filter.IssuerCnpj);
        var filterHash = fingerprints.Hash(JsonSerializer.Serialize(new { filter.From, filter.To, issuerHmac, filter.State, filter.Type, filter.Status }));
        var query = db.Documents.AsNoTracking()
            .Join(db.Processing.AsNoTracking(),
                document => document.Id,
                processing => processing.DocumentId,
                (document, processing) => new { Document = document, Processing = processing })
            .Where(x => !x.Document.IsDeleted);
        if (filter.From.HasValue)
        {
            query = query.Where(x => x.Document.IssuedAt >= filter.From);
        }

        if (filter.To.HasValue)
        {
            query = query.Where(x => x.Document.IssuedAt < filter.To);
        }

        if (issuerHmac is not null)
        {
            query = query.Where(x => x.Document.IssuerHmac == issuerHmac);
        }

        if (filter.State is not null)
        {
            query = query.Where(x => x.Document.State == filter.State);
        }

        if (filter.Type.HasValue)
        {
            query = query.Where(x => x.Document.Type == filter.Type);
        }

        if (filter.Status.HasValue)
        {
            query = query.Where(x => x.Processing.Status == filter.Status);
        }

        // Decidi usar Cursor ao invés de Skip/Take para evitar problemas de performance e inconsistência em grandes volumes de dados.
        if (filter.Cursor is not null)
        {
            DocumentCursor cursor;
            try
            {
                if (filter.Cursor.Length > 2048)
                {
                    throw new FormatException();
                }

                cursor = JsonSerializer.Deserialize<DocumentCursor>(Encoding.UTF8.GetString(Convert.FromBase64String(filter.Cursor))) ?? throw new FormatException();
                if (cursor.FilterHash != filterHash)
                {
                    throw new FormatException();
                }
            }
            catch (Exception exception) when (exception is FormatException or JsonException)
            {
                throw new AppException("invalid_cursor");
            }

            query = query.Where(x => x.Document.IssuedAt < cursor.IssuedAt || x.Document.IssuedAt == cursor.IssuedAt && x.Document.Id.CompareTo(cursor.Id) < 0);
        }
        var rows = await query.OrderByDescending(x => x.Document.IssuedAt).ThenByDescending(x => x.Document.Id)
            .Select(x => new
            {
                x.Document.Id,
                x.Document.Type,
                x.Document.IssuerMasked,
                x.Document.IssuedAt,
                x.Document.State,
                x.Processing.Status,
                x.Document.ContentRevision,
                x.Document.RowVersion
            })
            .Take(filter.PageSize + 1).ToListAsync(ct);
        var more = rows.Count > filter.PageSize;
        var items = rows.Take(filter.PageSize).Select(x => new DocumentItem(x.Id, x.Type, x.IssuerMasked,
            x.IssuedAt, x.State, x.Status, x.ContentRevision, ETags.Format(x.RowVersion))).ToList();
        var last = items.LastOrDefault();
        return new Page<DocumentItem>(items, GetNextCursor(filterHash, more, last));
    }

    private static string? GetNextCursor(string filterHash, bool more, DocumentItem? last)
    {
        string? next;
        if (more && last is not null)
        {
            next = (string?)Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new DocumentCursor(last.IssuedAt, last.Id, filterHash))));
        }
        else
        {
            next = null;
        }

        return next;
    }

    public async Task<DocumentDetails?> DetailsAsync(Guid id, CancellationToken ct)
    {
        // Uma única consulta fornece uma visão consistente do conteúdo, do estado de processamento e do resumo.
        var row = await db.Documents.AsNoTracking()
            .Join(db.Processing.AsNoTracking(),
                document => document.Id,
                processing => processing.DocumentId,
                (document, processing) => new { Document = document, Processing = processing })
            .GroupJoin(db.Summaries.AsNoTracking(),
                x => x.Document.Id,
                summary => summary.DocumentId,
                (x, summaries) => new { x.Document, x.Processing, Summaries = summaries })
            .SelectMany(x => x.Summaries.DefaultIfEmpty(),
                (x, summary) => new { x.Document, x.Processing, Summary = summary })
            .Where(x => x.Document.Id == id && !x.Document.IsDeleted)
            .SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }

        var d = row.Document;
        var p = row.Processing;
        return new DocumentDetails(new DocumentItem(d.Id, d.Type, d.IssuerMasked, d.IssuedAt, d.State, p.Status,
            d.ContentRevision, ETags.Format(d.RowVersion)), d.SanitizedXml, p.ProcessingId, p.ErrorCode,
            row.Summary is null ? null : new SummaryView(row.Summary.Competence, row.Summary.TotalAmountInCents, row.Summary.ProcessedAt));
    }
}
