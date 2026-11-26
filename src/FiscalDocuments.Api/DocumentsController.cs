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
    /// <summary>
    /// Enpoint para recebimento de arquivo(s). Como estava no plural, fiz com multipart, para receber até 10 arquivos, 2 MiB por arquivo
    /// Aplica a política de rate limit "uploads" que permite quatro requisições simultâneas, sem fila de espera.
    /// O Bearer token deve conter a permissão documents.write. O endpoint de teste adiciona todas as claims.
    /// </summary>
    /// <param name="ct"></param>
    /// <returns></returns>
    /// <exception cref="AppException"></exception>
    [HttpPost]
    [DisableFormValueModelBinding]
    [Authorize(Policy = "documents.write")]
    [EnableRateLimiting("uploads")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(RequestValidation.MaxRequestBytes)]
    [ProducesResponseType(typeof(BatchResponse), 200)]
    public async Task<IActionResult> Upload(CancellationToken ct)
    {
        // Não usei o IFormFile/ReadFormAsync: o ASP.NET pode gravar o XML e acabar expondo dados sensiveis.
        if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var contentType) ||
            !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            // 415 Tipo de mídia não suportado
            throw new AppException("multipart_required", 415);
        }

        // Em requisições com multiplos arquivos, temos esse header "boundary", que define onde começa e termina cada arquivo.
        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 128)
        {
            throw new AppException("invalid_multipart_boundary");
        }
        // Define o MultipartReader, que lê os arquivos da requisição, com limites de cabeçalho e tamanho do corpo.
        var reader = new MultipartReader(boundary, Request.Body)
        {
            HeadersCountLimit = 16, 
            HeadersLengthLimit = 8192, 
            BodyLengthLimit = RequestValidation.MaxRequestBytes
        };
        var results = new List<BatchItem>();
        MultipartSection? section;
        var totalBytes = 0L;
        while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
        {
            // Se chegou a 10 arquivos, os proximos retornam erro.
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

            // Como foi pedido um teste de carga, eu decidi um limite máximo.
            if (totalBytes > RequestValidation.MaxBatchBytes)
            {
                throw new AppException("batch_too_large", 413);
            }

            if (size > RequestValidation.MaxFileBytes)
            {
                results.Add(new BatchItem(inputIndex, 413, null, "file_too_large"));
                continue;
            }
            try
            {
                // Envia o arquivo para o Handler, que valida e persiste. 
                var accepted = await sender.Send(new ReceiveDocument(bytes, HttpContext.TraceIdentifier), ct);
                results.Add(new BatchItem(inputIndex, accepted.Disposition == "Accepted" ? 202 : 200, accepted, null));
            }
            catch (AppException exception) 
            { 
                results.Add(new BatchItem(inputIndex, exception.StatusCode, null, exception.Code)); 
            }
            catch (DomainException exception) 
            {
                results.Add(new BatchItem(inputIndex, 409, null, exception.Code)); 
            }
        }
        // Envio sem arquivos.
        if (results.Count == 0)
        {
            throw new AppException("empty_batch");
        }
        // results é uma lista de resultados do processamento inicial de cada item.
        return Ok(new BatchResponse(results));
    }

    [HttpGet]
    [Authorize(Policy = "documents.read")]
    public async Task<ActionResult<Page<DocumentItem>>> List([FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] string? issuerCnpj, [FromQuery] string? state, [FromQuery] DocumentType? type,
        [FromQuery] ProcessingStatus? status, [FromQuery] int pageSize = 20, [FromQuery] string? cursor = null, CancellationToken ct = default)
    {
        return Ok(await sender.Send(new ListDocuments(new DocumentFilter(from, to, issuerCnpj, state, type, status, pageSize, cursor)), ct));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "documents.read")]
    public async Task<ActionResult<DocumentDetails>> Get(Guid id, CancellationToken ct)
    {
        var document = await sender.Send(new GetDocument(id), ct);
        if (document is null)
        {
            throw new AppException("document_not_found", 404);
        }

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
        return Mutation(await sender.Send(
            new ReplaceDocument(
                id, 
                bytes, 
                Request.Headers.IfMatch, 
                Request.Headers["Idempotency-Key"], 
                Subject(), 
                HttpContext.TraceIdentifier)
            , ct));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "documents.write")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        return Mutation(await sender.Send(new DeleteDocument(id,
            Request.Headers.IfMatch, Request.Headers["Idempotency-Key"], Subject()), ct));
    }

    [HttpPost("{id:guid}/reprocess")]
    [Authorize(Policy = "operations.replay")]
    public async Task<IActionResult> Reprocess(Guid id, CancellationToken ct)
    {
        return Mutation(await sender.Send(new ReprocessDocument(id, Request.Headers["Idempotency-Key"], Subject(), HttpContext.TraceIdentifier), ct));
    }

    private string Subject()
    {
        return User.FindFirstValue("sub") ?? throw new AppException("missing_subject", 401);
    }

    private IActionResult Mutation(MutationResult result)
    {
        if (result.ETag is not null) Response.Headers.ETag = result.ETag;
        if (result.Location is not null) Response.Headers.Location = result.Location;
        if (result.Body is null)
        {
            return StatusCode(result.StatusCode);
        }
        else
        {
            return StatusCode(result.StatusCode, result.Body);
        }
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
                if (size > RequestValidation.MaxRequestBytes) throw new AppException("request_too_large", 413);
            }
            return (size > RequestValidation.MaxFileBytes ? [] : output.ToArray(), size);
        }
        finally 
        { 
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true); 
        }
    }
}
