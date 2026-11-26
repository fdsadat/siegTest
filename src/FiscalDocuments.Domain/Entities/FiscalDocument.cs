namespace FiscalDocuments.Domain;

public sealed class FiscalDocument
{
    private FiscalDocument() { }

    public Guid Id { get; private set; }
    public DocumentType Type { get; private set; }
    public string IdentityHmac { get; private set; } = "";
    public string ContentHmac { get; private set; } = "";
    public string IssuerMasked { get; private set; } = "";
    public string IssuerHmac { get; private set; } = "";
    public DateTimeOffset IssuedAt { get; private set; }
    public string State { get; private set; } = "";
    public string SanitizedXml { get; private set; } = "";
    public int ContentRevision { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static FiscalDocument Receive(FiscalXml xml, DateTimeOffset now)
    {
        var document = new FiscalDocument { Id = Guid.NewGuid(), ContentRevision = 1, CreatedAt = now };
        document.Apply(xml, now);
        return document;
    }

    public void Replace(FiscalXml xml, DocumentProcessing processing, DateTimeOffset now)
    {
        EnsureActive();
        if (Type != xml.Type || IdentityHmac != xml.IdentityHmac)
        {
            throw new DomainException("fiscal_identity_mismatch");
        }

        if (!processing.IsTerminal)
        {
            throw new DomainException("processing_in_progress");
        }

        if (processing.DocumentId != Id)
        {
            throw new DomainException("processing_mismatch");
        }

        ContentRevision = checked(ContentRevision + 1);
        Apply(xml, now);
        processing.Restart(ContentRevision, now);
    }

    public void Delete(DateTimeOffset now)
    {
        EnsureActive();
        IsDeleted = true;
        UpdatedAt = now;
    }

    private void Apply(FiscalXml xml, DateTimeOffset now)
    {
        Type = xml.Type;
        IdentityHmac = xml.IdentityHmac;
        ContentHmac = xml.ContentHmac;
        IssuerMasked = xml.IssuerMasked;
        IssuerHmac = xml.IssuerHmac;
        IssuedAt = xml.IssuedAt.ToUniversalTime();
        State = xml.State;
        SanitizedXml = xml.SanitizedXml;
        UpdatedAt = now;
    }

    private void EnsureActive()
    {
        if (IsDeleted)
        {
            throw new DomainException("document_deleted");
        }
    }
}
