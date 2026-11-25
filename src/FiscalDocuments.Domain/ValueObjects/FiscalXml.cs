namespace FiscalDocuments.Domain;

public sealed record FiscalXml(DocumentType Type, string IdentityHmac, string ContentHmac,
    string IssuerMasked, string IssuerHmac, DateTimeOffset IssuedAt, string State, string SanitizedXml);
