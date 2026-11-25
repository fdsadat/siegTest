using System.Text.Json;
using FiscalDocuments.Domain;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace FiscalDocuments.Application;

public interface IValidatedRequest { void Validate(); }

public sealed record ReceiveDocument(byte[] Xml, string CorrelationId) : IRequest<Acceptance>, IValidatedRequest
{
    public void Validate() => RequestValidation.Xml(Xml);
}
public sealed record ReplaceDocument(Guid Id, byte[] Xml, string? IfMatch, string? IdempotencyKey,
    string Subject, string CorrelationId) : IRequest<MutationResult>, IValidatedRequest
{
    public void Validate() { RequestValidation.Xml(Xml); _ = ETags.Parse(IfMatch); RequestValidation.Key(IdempotencyKey); }
}
public sealed record DeleteDocument(Guid Id, string? IfMatch, string? IdempotencyKey, string Subject)
    : IRequest<MutationResult>, IValidatedRequest
{
    public void Validate() { _ = ETags.Parse(IfMatch); RequestValidation.Key(IdempotencyKey); }
}
public sealed record ReprocessDocument(Guid Id, string? IdempotencyKey, string Subject, string CorrelationId)
    : IRequest<MutationResult>, IValidatedRequest
{
    public void Validate() => RequestValidation.Key(IdempotencyKey);
}
public sealed record ListDocuments(DocumentFilter Filter) : IRequest<Page<DocumentItem>>;
public sealed record GetDocument(Guid Id) : IRequest<DocumentDetails?>;

public static class RequestValidation
{
    public const int MaxFileBytes = 2 * 1024 * 1024;
    public static void Xml(byte[] bytes)
    {
        if (bytes.Length == 0) throw new AppException("empty_xml");
        if (bytes.Length > MaxFileBytes) throw new AppException("file_too_large", 413);
    }
    public static void Key(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length is < 8 or > 128 || key.Any(c => c < 33 || c > 126))
            throw new AppException("invalid_idempotency_key");
    }
}

public sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is IValidatedRequest validated) validated.Validate();
        return next();
    }
}

public sealed class DocumentHandlers(IWriteStore store, IXmlDocuments xml, IFingerprints fingerprints, TimeProvider clock,
    IDocumentQueries queries) : IRequestHandler<ReceiveDocument, Acceptance>, IRequestHandler<ReplaceDocument, MutationResult>,
    IRequestHandler<DeleteDocument, MutationResult>, IRequestHandler<ReprocessDocument, MutationResult>,
    IRequestHandler<ListDocuments, Page<DocumentItem>>, IRequestHandler<GetDocument, DocumentDetails?>
{
    public async Task<Acceptance> Handle(ReceiveDocument request, CancellationToken ct)
    {
        var parsed = xml.ValidateAndSanitize(request.Xml);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await store.TransactionAsync(async () =>
                {
                    var existing = await store.FindIdentityAsync(parsed.Type, parsed.IdentityHmac, ct);
                    if (existing is not null)
                    {
                        await store.LockAsync($"document:{existing.Id}", ct);
                        // Re-read after acquiring coordination with delete/replace/replay.
                        existing = (await store.DocumentAsync(existing.Id, ct))!;
                        if (existing.IsDeleted) throw new AppException("document_deleted", 409);
                        if (await store.ReceiptAsync(existing.Id, parsed.ContentHmac, ct) is null)
                            throw new AppException("document_exists_use_put", 409);
                        var current = await store.ProcessingAsync(existing.Id, ct);
                        return new Acceptance(existing.Id, "AlreadyReceived", existing.ContentRevision,
                            current.Status, existing.ContentHmac == parsed.ContentHmac);
                    }
                    var now = clock.GetUtcNow();
                    var document = FiscalDocument.Receive(parsed, now);
                    var processing = DocumentProcessing.Start(document, now);
                    store.Add(document);
                    store.Add(processing);
                    store.Add(new UploadReceipt { DocumentId = document.Id, ContentHmac = parsed.ContentHmac, ReceivedAt = now });
                    store.Add(IntegrationEvent.Create(IntegrationEvent.Received, document, processing, now, request.CorrelationId));
                    return new Acceptance(document.Id, "Accepted", document.ContentRevision, processing.Status, true);
                }, ct);
            }
            catch (DuplicateWriteException) when (attempt < 2) { /* Resolve a simultaneous successful insertion. */ }
        }
    }

    public Task<MutationResult> Handle(ReplaceDocument request, CancellationToken ct)
    {
        var parsed = xml.ValidateAndSanitize(request.Xml);
        return Idempotent("replace", request.Id, request.Subject, request.IdempotencyKey!,
            fingerprints.Hash($"{request.IfMatch}:{parsed.ContentHmac}"), async () =>
            {
                var document = await ActiveDocument(request.Id, ct);
                CheckETag(document, request.IfMatch);
                if (document.Type != parsed.Type || document.IdentityHmac != parsed.IdentityHmac)
                    throw new AppException("fiscal_identity_mismatch", 409);
                if (document.ContentHmac == parsed.ContentHmac)
                    return new MutationResult(200, new { document.Id, document.ContentRevision, Disposition = "Unchanged" }, ETags.Format(document.RowVersion));
                if (await store.ReceiptAsync(document.Id, parsed.ContentHmac, ct) is not null)
                    throw new AppException("previous_content_already_received", 409);
                var processing = await store.ProcessingAsync(document.Id, ct);
                document.Replace(parsed, processing, clock.GetUtcNow());
                await store.ClearSummaryAsync(document.Id, ct);
                store.Add(new UploadReceipt { DocumentId = document.Id, ContentHmac = parsed.ContentHmac, ReceivedAt = clock.GetUtcNow() });
                store.Add(IntegrationEvent.Create(IntegrationEvent.Received, document, processing, clock.GetUtcNow(), request.CorrelationId));
                // The next ETag is SQL-generated; GET the Location to obtain it after acceptance.
                return new MutationResult(202, new { document.Id, document.ContentRevision, processing.ProcessingId }, Location: $"/api/v1/documents/{document.Id}");
            }, ct);
    }

    public Task<MutationResult> Handle(DeleteDocument request, CancellationToken ct) =>
        Idempotent("delete", request.Id, request.Subject, request.IdempotencyKey!, fingerprints.Hash(request.IfMatch!), async () =>
        {
            var document = await store.DocumentAsync(request.Id, ct);
            if (document is null || document.IsDeleted) return new MutationResult(204);
            CheckETag(document, request.IfMatch);
            document.Delete(clock.GetUtcNow());
            return new MutationResult(204);
        }, ct);

    public Task<MutationResult> Handle(ReprocessDocument request, CancellationToken ct) =>
        Idempotent("reprocess", request.Id, request.Subject, request.IdempotencyKey!, fingerprints.Hash($"reprocess:{request.Id}"), async () =>
        {
            var document = await ActiveDocument(request.Id, ct);
            var processing = await store.ProcessingAsync(document.Id, ct);
            if (processing.Status != ProcessingStatus.Failed) throw new AppException("only_failed_processing_can_replay", 409);
            processing.Restart(document.ContentRevision, clock.GetUtcNow());
            await store.ClearSummaryAsync(document.Id, ct);
            store.Add(IntegrationEvent.Create(IntegrationEvent.Received, document, processing, clock.GetUtcNow(), request.CorrelationId));
            return new MutationResult(202, new { document.Id, processing.ProcessingId }, Location: $"/api/v1/documents/{document.Id}");
        }, ct);

    private Task<MutationResult> Idempotent(string action, Guid id, string subject, string key, string fingerprint,
        Func<Task<MutationResult>> execute, CancellationToken ct) => store.TransactionAsync(async () =>
    {
        var scope = fingerprints.Hash(JsonSerializer.Serialize(new { subject, action, id, key }));
        await store.LockAsync($"operation:{scope}", ct);
        var previous = await store.OperationAsync(scope, ct);
        var now = clock.GetUtcNow();
        if (previous is not null && previous.ExpiresAt > now)
        {
            if (previous.Fingerprint != fingerprint) throw new AppException("idempotency_key_reused", 409);
            return new MutationResult(previous.StatusCode,
                previous.ResponseJson is null ? null : JsonSerializer.Deserialize<JsonElement>(previous.ResponseJson), previous.ETag, previous.Location);
        }
        await store.LockAsync($"document:{id}", ct);
        var result = await execute();
        var operation = previous ?? new IdempotentOperation { Scope = scope };
        operation.Fingerprint = fingerprint;
        operation.StatusCode = result.StatusCode;
        operation.ResponseJson = result.Body is null ? null : JsonSerializer.Serialize(result.Body, JsonDefaults.Options);
        operation.ETag = result.ETag;
        operation.Location = result.Location;
        operation.ExpiresAt = now.AddHours(24);
        if (previous is null) store.Add(operation);
        return result;
    }, ct);

    private async Task<FiscalDocument> ActiveDocument(Guid id, CancellationToken ct)
    {
        var document = await store.DocumentAsync(id, ct);
        if (document is null || document.IsDeleted) throw new AppException("document_not_found", 404);
        return document;
    }
    private static void CheckETag(FiscalDocument document, string? etag)
    {
        if (!document.RowVersion.AsSpan().SequenceEqual(ETags.Parse(etag))) throw new AppException("etag_mismatch", 412);
    }
    public Task<Page<DocumentItem>> Handle(ListDocuments request, CancellationToken ct) => queries.ListAsync(request.Filter, ct);
    public Task<DocumentDetails?> Handle(GetDocument request, CancellationToken ct) => queries.DetailsAsync(request.Id, ct);
}

public static class ApplicationServices
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssemblyContaining<ReceiveDocument>();
            configuration.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });
        services.AddScoped<EventProcessor>();
        return services;
    }
}
