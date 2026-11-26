using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FiscalDocuments.Api;
using FiscalDocuments.Application;
using FiscalDocuments.Domain;
using FiscalDocuments.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace FiscalDocuments.IntegrationTests;

[TestFixture, NonParallelizable, Category("Sql")]
public sealed class DocumentTests : SqlFixture
{
    private async Task<Acceptance> ReceiveAsync(byte[]? bytes = null)
    {
        return await InScopeAsync(services =>
        services.GetRequiredService<ISender>().Send(new ReceiveDocument(bytes ?? Sample(), "integration")));
    }

    private async Task<DocumentDetails> DetailsAsync(Guid id)
    {
        return (await Client.GetFromJsonAsync<DocumentDetails>($"/api/v1/documents/{id}", JsonDefaults.Options))!;
    }

    private async Task<IntegrationEvent> EventAsync(string type)
    {
        return await InScopeAsync(async services =>
        {
            var record = await services.GetRequiredService<FiscalDbContext>().Outbox.AsNoTracking().SingleAsync(o => o.EventType == type);
            return JsonSerializer.Deserialize<IntegrationEvent>(record.Payload, JsonDefaults.Options)!;
        });
    }

    private Task<bool> ProcessAsync(IntegrationEvent message)
    {
        return InScopeAsync(services => services.GetRequiredService<EventProcessor>().ProcessAsync(message, CancellationToken.None));
    }

    private async Task CompleteAsync()
    {
        await ProcessAsync(await EventAsync(IntegrationEvent.Received));
    }
    private async Task<HttpResponseMessage> ReplaceAsync(Guid id, string etag, byte[] bytes, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/documents/{id}");
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        request.Headers.Add("Idempotency-Key", key);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        return await Client.SendAsync(request);
    }

    [Test]
    public async Task Simultaneous_uploads_create_one_document_receipt_and_event()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ReceiveAsync()));
        Assert.That(responses.Select(r => r.DocumentId).Distinct().Count(), Is.EqualTo(1));
        Assert.That(responses.Count(r => r.Disposition == "Accepted"), Is.EqualTo(1));
        await InScopeAsync(async services =>
        {
            var db = services.GetRequiredService<FiscalDbContext>();
            Assert.Multiple(() =>
            {
                Assert.That(db.Documents.Count(), Is.EqualTo(1));
                Assert.That(db.Receipts.Count(), Is.EqualTo(1));
                Assert.That(db.Outbox.Count(), Is.EqualTo(1));
            });
            await Task.CompletedTask;
            return true;
        });
    }

    [Test]
    public async Task Batch_accepts_valid_files_independently_and_recognizes_individual_resend()
    {
        using var multipart = new MultipartFormDataContent();
        foreach (var name in new[] { "nfe.xml", "cte.xml", "nfse.xml", "invalid.xml", "nfe.xml" })
        {
            multipart.Add(new ByteArrayContent(Sample(name)), "files", name);
        }

        using var response = await Client.PostAsync("/api/v1/documents", multipart);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = (await response.Content.ReadFromJsonAsync<BatchResponse>(JsonDefaults.Options))!;
        Assert.That(body.Results.Select(r => r.StatusCode), Is.EqualTo(new[] { 202, 202, 202, 400, 200 }));
        var again = await ReceiveAsync();
        Assert.That(again.DocumentId, Is.EqualTo(body.Results[0].Document!.DocumentId));
        Assert.That(again.Disposition, Is.EqualTo("AlreadyReceived"));
    }

    [Test]
    public async Task Relaxed_fiscal_metadata_preserves_resend_idempotency_and_summary_processing()
    {
        var input = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Sample())
            .Replace("versao=\"4.00\"", "versao=\"9.99\"", StringComparison.Ordinal)
            .Replace("<mod>55</mod>", "", StringComparison.Ordinal)
            .Replace("<CNPJ>12345678000195</CNPJ>", "<CNPJ>98765432000199</CNPJ>", StringComparison.Ordinal));
        var accepted = await ReceiveAsync(input);
        var repeated = await ReceiveAsync(input);
        await CompleteAsync();
        var details = await DetailsAsync(accepted.DocumentId);
        Assert.Multiple(() =>
        {
            Assert.That(accepted.Disposition, Is.EqualTo("Accepted"));
            Assert.That(repeated.DocumentId, Is.EqualTo(accepted.DocumentId));
            Assert.That(repeated.Disposition, Is.EqualTo("AlreadyReceived"));
            Assert.That(details.Document.ProcessingStatus, Is.EqualTo(ProcessingStatus.Completed));
            Assert.That(details.Summary!.TotalAmountInCents, Is.EqualTo(15025));
            Assert.That(details.SanitizedXml, Does.Not.Contain("98765432000199"));
        });
    }

    [Test]
    public async Task Pipeline_is_idempotent_and_does_not_change_document_etag()
    {
        var accepted = await ReceiveAsync();
        var before = await DetailsAsync(accepted.DocumentId);
        var received = await EventAsync(IntegrationEvent.Received);
        await ProcessAsync(received);
        await ProcessAsync(received); // Simula uma queda após o commit SQL, antes do ACK.
        var falselyFailed = await InScopeAsync(s => s.GetRequiredService<EventProcessor>().FailAsync(received, "reconciled_from_dlq", CancellationToken.None));
        Assert.That(falselyFailed, Is.False);
        var after = await DetailsAsync(accepted.DocumentId);
        Assert.Multiple(() =>
        {
            Assert.That(after.Document.ProcessingStatus, Is.EqualTo(ProcessingStatus.Completed));
            Assert.That(after.Document.ETag, Is.EqualTo(before.Document.ETag));
            Assert.That(after.Summary!.TotalAmountInCents, Is.EqualTo(15025));
            Assert.That(after.Summary.Competence, Is.EqualTo("2026-11"));
        });
        var counts = await InScopeAsync(async services =>
        {
            var db = services.GetRequiredService<FiscalDbContext>();
            return (await db.Summaries.CountAsync(), await db.Inbox.CountAsync(), await db.Outbox.CountAsync());
        });
        Assert.That(counts, Is.EqualTo((1, 1, 1)));
        Assert.That(await ProcessAsync(received with { EventId = Guid.NewGuid() }), Is.False);
    }

    [Test]
    public async Task Concurrent_deliveries_complete_the_document_once()
    {
        var accepted = await ReceiveAsync();
        var message = await EventAsync(IntegrationEvent.Received);
        var applied = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ProcessAsync(message)));
        Assert.That(applied.Count(result => result), Is.EqualTo(1));
        var details = await DetailsAsync(accepted.DocumentId);
        Assert.That(details.Document.ProcessingStatus, Is.EqualTo(ProcessingStatus.Completed));
        await InScopeAsync(async services =>
        {
            var db = services.GetRequiredService<FiscalDbContext>();
            Assert.That(await db.Summaries.CountAsync(), Is.EqualTo(1));
            Assert.That(await db.Inbox.CountAsync(), Is.EqualTo(1));
            return true;
        });
    }

    [Test]
    public async Task Summary_failure_rolls_back_completion_and_inbox_then_can_retry()
    {
        var accepted = await ReceiveAsync();
        var message = await EventAsync(IntegrationEvent.Received);
        await InScopeAsync(async services =>
        {
            var db = services.GetRequiredService<FiscalDbContext>();
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE DocumentSummaries ADD CONSTRAINT RejectTestSummary
                CHECK (TotalAmountInCents < 0);
                """);
            return true;
        });
        try
        {
            Assert.ThrowsAsync<DbUpdateException>(async () => await ProcessAsync(message));
            await InScopeAsync(async services =>
            {
                var db = services.GetRequiredService<FiscalDbContext>();
                Assert.That(await db.Summaries.CountAsync(), Is.Zero);
                Assert.That(await db.Inbox.CountAsync(), Is.Zero);
                Assert.That((await db.Processing.SingleAsync()).Status, Is.EqualTo(ProcessingStatus.PendingExtraction));
                return true;
            });
        }
        finally
        {
            await InScopeAsync(async services =>
            {
                await services.GetRequiredService<FiscalDbContext>().Database.ExecuteSqlRawAsync("ALTER TABLE DocumentSummaries DROP CONSTRAINT RejectTestSummary");
                return true;
            });
        }
        Assert.That(await ProcessAsync(message), Is.True);
        Assert.That((await DetailsAsync(accepted.DocumentId)).Document.ProcessingStatus, Is.EqualTo(ProcessingStatus.Completed));
    }

    [Test]
    public async Task Replacement_requires_terminal_state_and_concurrent_writes_use_etag()
    {
        var accepted = await ReceiveAsync();
        var before = await DetailsAsync(accepted.DocumentId);
        var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Sample()).Replace("150.25", "175.00", StringComparison.Ordinal));
        using var blocked = await ReplaceAsync(accepted.DocumentId, before.Document.ETag, changed, "blocked-0001");
        Assert.That(blocked.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        await CompleteAsync();
        var other = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Sample()).Replace("150.25", "180.00", StringComparison.Ordinal));
        var responses = await Task.WhenAll(ReplaceAsync(accepted.DocumentId, before.Document.ETag, changed, "replace-0001"),
            ReplaceAsync(accepted.DocumentId, before.Document.ETag, other, "replace-0002"));
        Assert.That(responses.Select(r => (int)r.StatusCode).Order(), Is.EqualTo(new[] { 202, 412 }));
        var winner = responses[0].StatusCode == HttpStatusCode.Accepted ? 0 : 1;
        using var replay = await ReplaceAsync(accepted.DocumentId, before.Document.ETag, winner == 0 ? changed : other, winner == 0 ? "replace-0001" : "replace-0002");
        Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(await replay.Content.ReadAsStringAsync(), Is.EqualTo(await responses[winner].Content.ReadAsStringAsync()));
        using var reused = await ReplaceAsync(accepted.DocumentId, before.Document.ETag, winner == 0 ? other : changed, winner == 0 ? "replace-0001" : "replace-0002");
        Assert.That(reused.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        foreach (var response in responses)
        {
            response.Dispose();
        }

        var current = await DetailsAsync(accepted.DocumentId);
        Assert.That(current.Document.ContentRevision, Is.EqualTo(2));
        Assert.That(current.Summary, Is.Null);
    }

    [Test]
    public async Task Previous_xml_and_old_messages_never_restore_overwritten_content()
    {
        var accepted = await ReceiveAsync();
        var oldEvent = await EventAsync(IntegrationEvent.Received);
        await CompleteAsync();
        var before = await DetailsAsync(accepted.DocumentId);
        var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Sample()).Replace("150.25", "175.00", StringComparison.Ordinal));
        using var response = await ReplaceAsync(accepted.DocumentId, before.Document.ETag, changed, "replace-0001");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        await ProcessAsync(oldEvent with { EventId = Guid.NewGuid() });
        var duplicate = await ReceiveAsync();
        Assert.That(duplicate.IsCurrentContent, Is.False);
        var after = await DetailsAsync(accepted.DocumentId);
        Assert.That(after.SanitizedXml, Does.Contain("175.00").And.Not.Contain("150.25"));
        Assert.That(after.Document.ProcessingStatus, Is.EqualTo(ProcessingStatus.PendingExtraction));
    }

    [Test]
    public async Task Delete_is_idempotent_and_inflight_messages_do_not_create_results()
    {
        var accepted = await ReceiveAsync();
        var details = await DetailsAsync(accepted.DocumentId);
        var oldEvent = await EventAsync(IntegrationEvent.Received);
        async Task<HttpResponseMessage> Delete()
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/documents/{accepted.DocumentId}");
            request.Headers.TryAddWithoutValidation("If-Match", details.Document.ETag);
            request.Headers.Add("Idempotency-Key", "delete-00001");
            return await Client.SendAsync(request);
        }
        using var first = await Delete();
        using var second = await Delete();
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(second.StatusCode, Is.EqualTo(first.StatusCode));
        await ProcessAsync(oldEvent);
        using var get = await Client.GetAsync($"/api/v1/documents/{accepted.DocumentId}");
        Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await InScopeAsync(s => s.GetRequiredService<FiscalDbContext>().Summaries.CountAsync()), Is.Zero);
        Assert.ThrowsAsync<AppException>(async () => await ReceiveAsync());
    }

    [Test]
    public async Task Terminal_failure_can_replay_with_new_run_and_old_run_is_fenced()
    {
        var accepted = await ReceiveAsync();
        var old = await EventAsync(IntegrationEvent.Received);
        await InScopeAsync(s => s.GetRequiredService<EventProcessor>().FailAsync(old, "test_failure", CancellationToken.None));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/documents/{accepted.DocumentId}/reprocess");
        request.Headers.Add("Idempotency-Key", "replay-00001");
        using var response = await Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        await ProcessAsync(old);
        var details = await DetailsAsync(accepted.DocumentId);
        Assert.That(details.ProcessingId, Is.Not.EqualTo(old.ProcessingId));
        Assert.That(details.Document.ContentRevision, Is.EqualTo(1));
        Assert.That(details.Document.ProcessingStatus, Is.EqualTo(ProcessingStatus.PendingExtraction));
    }

    [Test]
    public async Task Receipt_document_and_outbox_rollback_together()
    {
        await InScopeAsync(async services =>
        {
            var store = services.GetRequiredService<IWriteStore>();
            var db = services.GetRequiredService<FiscalDbContext>();
            var parsed = services.GetRequiredService<IXmlDocuments>().ValidateAndSanitize(Sample());
            Assert.ThrowsAsync<InvalidOperationException>(async () => await store.TransactionAsync<bool>(async () =>
            {
                var document = FiscalDocument.Receive(parsed, DateTimeOffset.UtcNow);
                var processing = DocumentProcessing.Start(document, DateTimeOffset.UtcNow);
                store.Add(document);
                store.Add(processing);
                store.Add(new UploadReceipt { DocumentId = document.Id, ContentHmac = parsed.ContentHmac, ReceivedAt = DateTimeOffset.UtcNow });
                store.Add(IntegrationEvent.Create(document, processing, DateTimeOffset.UtcNow, "rollback-test"));
                await db.SaveChangesAsync();
                Assert.That(await db.Documents.AsNoTracking().CountAsync(), Is.EqualTo(1));
                throw new InvalidOperationException("simulated_failure_after_writes_before_commit");
            }, CancellationToken.None));
            Assert.That(await db.Documents.AsNoTracking().CountAsync(), Is.Zero);
            Assert.That(await db.Receipts.AsNoTracking().CountAsync(), Is.Zero);
            Assert.That(await db.Outbox.AsNoTracking().CountAsync(), Is.Zero);
            return true;
        });
    }

    [Test]
    public async Task Unrecognised_state_can_be_stored_and_filtered()
    {
        var input = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Sample())
            .Replace("<UF>SP</UF>", "<UF>XX</UF>", StringComparison.Ordinal));
        var accepted = await ReceiveAsync(input);
        var page = (await Client.GetFromJsonAsync<Page<DocumentItem>>("/api/v1/documents?state=XX", JsonDefaults.Options))!;
        var missing = (await Client.GetFromJsonAsync<Page<DocumentItem>>("/api/v1/documents?state=YY", JsonDefaults.Options))!;
        using var tooLong = await Client.GetAsync("/api/v1/documents?state=ABC");
        Assert.Multiple(() =>
        {
            Assert.That(page.Items, Has.Count.EqualTo(1));
            Assert.That(page.Items[0].Id, Is.EqualTo(accepted.DocumentId));
            Assert.That(page.Items[0].State, Is.EqualTo("XX"));
            Assert.That(missing.Items, Is.Empty);
            Assert.That(tooLong.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        });
    }

    [Test]
    public async Task Nfse_with_unknown_municipality_prefix_can_be_stored_with_empty_state()
    {
        var input = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Sample("nfse.xml"))
            .Replace("<cMun>3550308</cMun>", "<cMun>99</cMun>", StringComparison.Ordinal));
        var accepted = await ReceiveAsync(input);
        var details = await DetailsAsync(accepted.DocumentId);
        Assert.That(details.Document.State, Is.Empty);
    }

    [Test]
    public async Task Filters_pagination_and_sensitive_data_are_safe()
    {
        await ReceiveAsync();
        await ReceiveAsync(Sample("cte.xml"));
        await ReceiveAsync(Sample("nfse.xml"));
        var page = (await Client.GetFromJsonAsync<Page<DocumentItem>>("/api/v1/documents?issuerCnpj=12345678000195&state=SP&pageSize=2", JsonDefaults.Options))!;
        Assert.That(page.Items, Has.Count.EqualTo(2));
        Assert.That(page.NextCursor, Is.Not.Null);
        var next = (await Client.GetFromJsonAsync<Page<DocumentItem>>("/api/v1/documents?issuerCnpj=12345678000195&state=SP&pageSize=2&cursor=" + Uri.EscapeDataString(page.NextCursor!), JsonDefaults.Options))!;
        Assert.That(next.Items, Has.Count.EqualTo(1));
        Assert.That(page.Items.Select(d => d.Id).Intersect(next.Items.Select(d => d.Id)), Is.Empty);
        var stored = await InScopeAsync(async services =>
        {
            var db = services.GetRequiredService<FiscalDbContext>();
            var payloads = await db.Documents.AsNoTracking().Select(d => d.SanitizedXml).ToListAsync();
            payloads.AddRange(await db.Outbox.AsNoTracking().Select(o => o.Payload).ToListAsync());
            return string.Join('\n', payloads);
        });
        Assert.That(stored, Does.Not.Contain("12345678000195").And.Not.Contain("12345678909"));
    }

    [Test]
    public async Task Native_sql_rowversion_rejects_a_stale_write_without_application_locks()
    {
        var accepted = await ReceiveAsync();
        await using var firstScope = Factory.Services.CreateAsyncScope();
        await using var secondScope = Factory.Services.CreateAsyncScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<FiscalDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<FiscalDbContext>();
        var first = await firstDb.Documents.SingleAsync(d => d.Id == accepted.DocumentId);
        var second = await secondDb.Documents.SingleAsync(d => d.Id == accepted.DocumentId);
        first.Delete(DateTimeOffset.UtcNow);
        second.Delete(DateTimeOffset.UtcNow);
        await firstDb.SaveChangesAsync();
        Assert.ThrowsAsync<DbUpdateConcurrencyException>(async () => await secondDb.SaveChangesAsync());
    }

    [Test]
    public async Task Outbox_claim_is_retained_when_broker_is_unavailable()
    {
        await ReceiveAsync();
        await using var broker = new FiscalDocuments.Infrastructure.Messaging.RabbitBroker(
            Microsoft.Extensions.Options.Options.Create(new FiscalDocuments.Infrastructure.Messaging.RabbitOptions
            { Host = "127.0.0.1", Port = 1, Username = "test", Password = "test-only" }));
        using var dispatcher = new FiscalDocuments.Infrastructure.Messaging.OutboxDispatcher(
            Factory.Services.GetRequiredService<IServiceScopeFactory>(), broker, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FiscalDocuments.Infrastructure.Messaging.OutboxDispatcher>.Instance);
        await dispatcher.DispatchOneAsync(CancellationToken.None);
        var message = await InScopeAsync(s => s.GetRequiredService<FiscalDbContext>().Outbox.AsNoTracking().SingleAsync());
        Assert.Multiple(() =>
        {
            Assert.That(message.PublishedAt, Is.Null);
            Assert.That(message.Attempts, Is.EqualTo(1));
            Assert.That(message.LeaseId, Is.Null);
            Assert.That(message.AvailableAt, Is.GreaterThan(message.CreatedAt));
        });
    }

    [Test]
    public async Task Authentication_permissions_preconditions_and_swagger_are_available()
    {
        using var anonymous = Factory.CreateClient();
        using var unauthorized = await anonymous.GetAsync("/api/v1/documents");
        Assert.That(unauthorized.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("documents.read"));
        using var forbidden = await anonymous.PostAsync("/api/v1/documents", new MultipartFormDataContent());
        Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        using var missing = await Client.DeleteAsync($"/api/v1/documents/{Guid.NewGuid()}");
        Assert.That((int)missing.StatusCode, Is.EqualTo(428));
        using var swagger = await Client.GetAsync("/swagger/v1/swagger.json");
        Assert.That(swagger.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }
}
