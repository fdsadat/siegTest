using FiscalDocuments.Application;
using FiscalDocuments.Domain;
using NUnit.Framework;

namespace FiscalDocuments.UnitTests;

[TestFixture]
public sealed class DomainTests
{
    private static FiscalXml Xml(string content = "A", string identity = "identity") => new(DocumentType.NFe, identity,
        content, "**************", "issuer-hash", DateTimeOffset.Parse("2026-11-25T10:00:00-03:00"), "SP", "<sanitized/>");

    [Test]
    public void Replacement_is_blocked_until_entire_pipeline_finishes()
    {
        var now = DateTimeOffset.UtcNow;
        var document = FiscalDocument.Receive(Xml(), now);
        var processing = DocumentProcessing.Start(document, now);
        Assert.That(() => document.Replace(Xml("B"), processing, now), Throws.TypeOf<DomainException>());
        processing.Extract(10.25m, "2026-11", now);
        Assert.That(processing.IsTerminal, Is.False);
        Assert.That(() => document.Replace(Xml("B"), processing, now), Throws.TypeOf<DomainException>());
        processing.Complete(now);
        var oldRun = processing.ProcessingId;
        document.Replace(Xml("B"), processing, now);
        Assert.Multiple(() =>
        {
            Assert.That(document.ContentHmac, Is.EqualTo("B"));
            Assert.That(document.ContentRevision, Is.EqualTo(2));
            Assert.That(processing.Status, Is.EqualTo(ProcessingStatus.PendingExtraction));
            Assert.That(processing.ProcessingId, Is.Not.EqualTo(oldRun));
            Assert.That(processing.TotalAmount, Is.Null);
            Assert.That(processing.Matches(1, oldRun), Is.False);
        });
    }

    [Test]
    public void Failed_processing_allows_replacement_but_identity_is_immutable()
    {
        var now = DateTimeOffset.UtcNow;
        var document = FiscalDocument.Receive(Xml(), now);
        var processing = DocumentProcessing.Start(document, now);
        processing.Fail("test_failure", now);
        Assert.That(() => document.Replace(Xml("B", "other"), processing, now), Throws.TypeOf<DomainException>());
        document.Replace(Xml("B"), processing, now);
        Assert.That(document.ContentRevision, Is.EqualTo(2));
    }

    [Test]
    public void Deleted_document_cannot_be_replaced()
    {
        var now = DateTimeOffset.UtcNow;
        var document = FiscalDocument.Receive(Xml(), now);
        var processing = DocumentProcessing.Start(document, now);
        processing.Fail("test_failure", now);
        document.Delete(now);
        Assert.That(() => document.Replace(Xml("B"), processing, now), Throws.TypeOf<DomainException>());
    }

    [Test]
    public void Etags_require_a_quoted_eight_byte_version()
    {
        byte[] version = [1, 2, 3, 4, 5, 6, 7, 8];
        Assert.That(ETags.Parse(ETags.Format(version)), Is.EqualTo(version));
        Assert.That(() => ETags.Parse(null), Throws.TypeOf<AppException>().With.Property(nameof(AppException.StatusCode)).EqualTo(428));
        foreach (var invalid in new[] { "*", "W/\"AAAAAAAAAAA=\"", "unquoted", "\"AQ==\"" })
        {
            Assert.That(() => ETags.Parse(invalid), Throws.TypeOf<AppException>());
        }
    }
}
