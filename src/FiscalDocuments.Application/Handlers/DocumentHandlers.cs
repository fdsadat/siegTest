using System.Text.Json;
using FiscalDocuments.Domain;
using MediatR;

namespace FiscalDocuments.Application;

/// <summary>
/// Classe com Handlers para o DocumentsController.
/// </summary>
/// <param name="store"></param>
/// <param name="xml"></param>
/// <param name="fingerprints"></param>
/// <param name="clock"></param>
/// <param name="queries"></param>
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
                        // Relê o documento após obter o bloqueio de coordenação com exclusão, substituição e reprocessamento.
                        existing = (await store.DocumentAsync(existing.Id, ct))!;
                        if (existing.IsDeleted)
                        {
                            throw new AppException("document_deleted", 409);
                        }

                        if (await store.ReceiptAsync(existing.Id, parsed.ContentHmac, ct) is null)
                        {
                            throw new AppException("document_exists_use_put", 409);
                        }

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
                    store.Add(IntegrationEvent.Create(document, processing, now, request.CorrelationId));
                    return new Acceptance(document.Id, "Accepted", document.ContentRevision, processing.Status, true);
                }, ct);
            }
            catch (DuplicateWriteException) when (attempt < 2) { /* Resolve uma inserção simultânea que foi concluída com sucesso. */ }
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
                {
                    throw new AppException("fiscal_identity_mismatch", 409);
                }

                if (document.ContentHmac == parsed.ContentHmac)
                {
                    return new MutationResult(200, new { document.Id, document.ContentRevision, Disposition = "Unchanged" }, ETags.Format(document.RowVersion));
                }

                if (await store.ReceiptAsync(document.Id, parsed.ContentHmac, ct) is not null)
                {
                    throw new AppException("previous_content_already_received", 409);
                }

                var processing = await store.ProcessingAsync(document.Id, ct);
                document.Replace(parsed, processing, clock.GetUtcNow());
                await store.ClearSummaryAsync(document.Id, ct);
                store.Add(new UploadReceipt { DocumentId = document.Id, ContentHmac = parsed.ContentHmac, ReceivedAt = clock.GetUtcNow() });
                store.Add(IntegrationEvent.Create(document, processing, clock.GetUtcNow(), request.CorrelationId));
                // O próximo ETag é gerado pelo SQL Server; consulte o endereço de Location via GET após a aceitação.
                return new MutationResult(202, new { document.Id, document.ContentRevision, processing.ProcessingId }, Location: $"/api/v1/documents/{document.Id}");
            }, ct);
    }

    public Task<MutationResult> Handle(DeleteDocument request, CancellationToken ct)
    {
        return Idempotent("delete", request.Id, request.Subject, request.IdempotencyKey!, fingerprints.Hash(request.IfMatch!), async () =>
        {
            var document = await store.DocumentAsync(request.Id, ct);
            if (document is null || document.IsDeleted)
            {
                return new MutationResult(204);
            }

            CheckETag(document, request.IfMatch);
            document.Delete(clock.GetUtcNow());
            return new MutationResult(200, new DeleteDocumentResponse(request.Id, "Documento excluído com sucesso."));
        }, ct);
    }

    public Task<MutationResult> Handle(ReprocessDocument request, CancellationToken ct)
    {
        return Idempotent("reprocess", request.Id, request.Subject, request.IdempotencyKey!, fingerprints.Hash($"reprocess:{request.Id}"), async () =>
        {
            var document = await ActiveDocument(request.Id, ct);
            var processing = await store.ProcessingAsync(document.Id, ct);
            if (processing.Status != ProcessingStatus.Failed)
            {
                throw new AppException("only_failed_processing_can_replay", 409);
            }

            processing.Restart(document.ContentRevision, clock.GetUtcNow());
            await store.ClearSummaryAsync(document.Id, ct);
            store.Add(IntegrationEvent.Create(document, processing, clock.GetUtcNow(), request.CorrelationId));
            return new MutationResult(202, new { document.Id, processing.ProcessingId }, Location: $"/api/v1/documents/{document.Id}");
        }, ct);
    }

    private Task<MutationResult> Idempotent(string action, Guid id, string subject, string key, string fingerprint,
        Func<Task<MutationResult>> execute, CancellationToken ct)
    {
        return store.TransactionAsync(async () =>
    {
        var scope = fingerprints.Hash(JsonSerializer.Serialize(new { subject, action, id, key }));
        await store.LockAsync($"operation:{scope}", ct);
        var previous = await store.OperationAsync(scope, ct);
        var now = clock.GetUtcNow();
        if (previous is not null && previous.ExpiresAt > now)
        {
            if (previous.Fingerprint != fingerprint)
            {
                throw new AppException("idempotency_key_reused", 409);
            }

            if (action == "delete")
            {
                return new MutationResult(204);
            }

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
        if (previous is null)
        {
            store.Add(operation);
        }

        return result;
    }, ct);
    }

    private async Task<FiscalDocument> ActiveDocument(Guid id, CancellationToken ct)
    {
        var document = await store.DocumentAsync(id, ct);
        if (document is null || document.IsDeleted)
        {
            throw new AppException("document_not_found", 404);
        }

        return document;
    }
    private static void CheckETag(FiscalDocument document, string? etag)
    {
        if (!document.RowVersion.AsSpan().SequenceEqual(ETags.Parse(etag)))
        {
            throw new AppException("etag_mismatch", 412);
        }
    }
    public Task<Page<DocumentItem>> Handle(ListDocuments request, CancellationToken ct)
    {
        return queries.ListAsync(request.Filter, ct);
    }

    public Task<DocumentDetails?> Handle(GetDocument request, CancellationToken ct)
    {
        return queries.DetailsAsync(request.Id, ct);
    }
}
