using System.Buffers;
using System.Security.Claims;
using FiscalDocuments.Application;
using FiscalDocuments.Domain;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace FiscalDocuments.Api;

[ApiController]
[Route("api/v1/documents")]
public sealed class DocumentsController(ISender sender) : ControllerBase
{
    [HttpPost]
    [DisableFormValueModelBinding]
    [Authorize(Policy = "documents.write")]
    [EnableRateLimiting("uploads")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(21 * 1024 * 1024)]
    [ProducesResponseType(typeof(BatchResponse), 200)]
    public async Task<IActionResult> Upload(CancellationToken ct)
    {
        // Never use IFormFile/ReadFormAsync: ASP.NET may spool original fiscal XML to disk.
        if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var contentType) ||
            !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new AppException("multipart_required", 415);
        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 128) throw new AppException("invalid_multipart_boundary");
        var reader = new MultipartReader(boundary, Request.Body)
        {
            HeadersCountLimit = 16, HeadersLengthLimit = 8192, BodyLengthLimit = 21 * 1024 * 1024
        };
        var results = new List<BatchItem>();
        MultipartSection? section;
        var totalBytes = 0L;
        while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
        {
            if (results.Count == 10)
            {
                results.Add(new BatchItem(10, 400, null, "too_many_files_remaining_items_not_accepted"));
                break;
            }
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase) ||
                !(disposition.FileName.HasValue || disposition.FileNameStar.HasValue))
            {
                throw new AppException("multipart_files_only");
            }
            var inputIndex = results.Count;
            var (bytes, size) = await ReadBoundedAsync(section.Body, ct);
            totalBytes += size;
            if (totalBytes > 20L * 1024 * 1024) throw new AppException("batch_too_large", 413);
            if (size > RequestValidation.MaxFileBytes)
            {
                results.Add(new BatchItem(inputIndex, 413, null, "file_too_large"));
                continue;
            }
            try
            {
                var accepted = await sender.Send(new ReceiveDocument(bytes, HttpContext.TraceIdentifier), ct);
                results.Add(new BatchItem(inputIndex, accepted.Disposition == "Accepted" ? 202 : 200, accepted, null));
            }
            catch (AppException exception) { results.Add(new BatchItem(inputIndex, exception.StatusCode, null, exception.Code)); }
            catch (DomainException exception) { results.Add(new BatchItem(inputIndex, 409, null, exception.Code)); }
        }
        if (results.Count == 0) throw new AppException("empty_batch");
        return Ok(new BatchResponse(results));
    }

    [HttpGet]
    [Authorize(Policy = "documents.read")]
    public async Task<ActionResult<Page<DocumentItem>>> List([FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] string? issuerCnpj, [FromQuery] string? state, [FromQuery] DocumentType? type,
        [FromQuery] ProcessingStatus? status, [FromQuery] int pageSize = 20, [FromQuery] string? cursor = null, CancellationToken ct = default)
        => Ok(await sender.Send(new ListDocuments(new DocumentFilter(from, to, issuerCnpj, state, type, status, pageSize, cursor)), ct));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "documents.read")]
    public async Task<ActionResult<DocumentDetails>> Get(Guid id, CancellationToken ct)
    {
        var document = await sender.Send(new GetDocument(id), ct);
        if (document is null) throw new AppException("document_not_found", 404);
        Response.Headers.ETag = document.Document.ETag;
        return Ok(document);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "documents.write")]
    [EnableRateLimiting("uploads")]
    [Consumes("application/xml", "text/xml")]
    [RequestSizeLimit(RequestValidation.MaxFileBytes)]
    public async Task<IActionResult> Replace(Guid id, CancellationToken ct)
    {
        var (bytes, size) = await ReadBoundedAsync(Request.Body, ct);
        if (size > RequestValidation.MaxFileBytes) throw new AppException("file_too_large", 413);
        return Mutation(await sender.Send(new ReplaceDocument(id, bytes, Request.Headers.IfMatch,
            Request.Headers["Idempotency-Key"], Subject(), HttpContext.TraceIdentifier), ct));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "documents.write")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => Mutation(await sender.Send(new DeleteDocument(id,
        Request.Headers.IfMatch, Request.Headers["Idempotency-Key"], Subject()), ct));

    [HttpPost("{id:guid}/reprocess")]
    [Authorize(Policy = "operations.replay")]
    public async Task<IActionResult> Reprocess(Guid id, CancellationToken ct) => Mutation(await sender.Send(new ReprocessDocument(id,
        Request.Headers["Idempotency-Key"], Subject(), HttpContext.TraceIdentifier), ct));

    private string Subject() => User.FindFirstValue("sub") ?? throw new AppException("missing_subject", 401);
    private IActionResult Mutation(MutationResult result)
    {
        if (result.ETag is not null) Response.Headers.ETag = result.ETag;
        if (result.Location is not null) Response.Headers.Location = result.Location;
        return result.Body is null ? StatusCode(result.StatusCode) : StatusCode(result.StatusCode, result.Body);
    }

    private static async Task<(byte[] Bytes, long Size)> ReadBoundedAsync(Stream stream, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            using var output = new MemoryStream();
            long size = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                size += read;
                if (size <= RequestValidation.MaxFileBytes) output.Write(buffer, 0, read);
                if (size > 21L * 1024 * 1024) throw new AppException("request_too_large", 413);
            }
            return (size > RequestValidation.MaxFileBytes ? [] : output.ToArray(), size);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
