using System.Text;
using FiscalDocuments.Application;
using FiscalDocuments.Domain;
using FiscalDocuments.Infrastructure.Xml;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace FiscalDocuments.UnitTests;

[TestFixture]
public sealed class XmlTests
{
    private FiscalXmlDocuments parser = null!;
    private Fingerprints fingerprints = null!;
    [SetUp]
    public void SetUp()
    {
        fingerprints = new Fingerprints(Options.Create(new PrivacyOptions { HmacKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()) }));
        parser = new FiscalXmlDocuments(fingerprints);
    }
    private static byte[] Sample(string name) => File.ReadAllBytes(Path.Combine(TestContext.CurrentContext.TestDirectory, "Samples", name));

    [TestCase("nfe.xml", DocumentType.NFe, "150.25")]
    [TestCase("cte.xml", DocumentType.CTe, "200.50")]
    [TestCase("nfse.xml", DocumentType.NFSe, "350.75")]
    public void Identifies_sanitizes_and_reprocesses_each_supported_layout(string name, DocumentType type, string amount)
    {
        var parsed = parser.ValidateAndSanitize(Sample(name));
        var extracted = parser.ExtractSanitized(parsed.SanitizedXml);
        Assert.Multiple(() =>
        {
            Assert.That(parsed.Type, Is.EqualTo(type));
            Assert.That(parsed.State, Is.EqualTo("SP"));
            Assert.That(parsed.IssuerMasked, Is.EqualTo("**************"));
            Assert.That(parsed.SanitizedXml, Does.Not.Contain("12345678000195").And.Not.Contain("12345678909").And.Not.Contain("123.456.789-09"));
            Assert.That(parsed.SanitizedXml, Does.Not.Contain("infCpl").And.Not.Contain("Id="));
            Assert.That(parsed.IdentityHmac, Has.Length.EqualTo(64));
            Assert.That(extracted.TotalAmount, Is.EqualTo(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));
            Assert.That(extracted.Competence, Is.EqualTo("2026-11"));
        });
    }

    [Test]
    public void Same_identity_and_changed_amount_have_distinct_content_fingerprints()
    {
        var original = Sample("nfe.xml");
        var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(original).Replace("150.25", "160.25", StringComparison.Ordinal));
        var a = parser.ValidateAndSanitize(original);
        var b = parser.ValidateAndSanitize(changed);
        Assert.That(a.IdentityHmac, Is.EqualTo(b.IdentityHmac));
        Assert.That(a.ContentHmac, Is.Not.EqualTo(b.ContentHmac));
        Assert.That(a.ContentHmac, Is.EqualTo(parser.ValidateAndSanitize(original).ContentHmac));
    }

    [Test]
    public void Content_fingerprint_is_calculated_before_masking()
    {
        var first = Sample("nfe.xml");
        var second = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(first).Replace("12345678909", "98765432100", StringComparison.Ordinal));
        var a = parser.ValidateAndSanitize(first);
        var b = parser.ValidateAndSanitize(second);
        Assert.That(a.SanitizedXml, Is.EqualTo(b.SanitizedXml));
        Assert.That(a.ContentHmac, Is.Not.EqualTo(b.ContentHmac));
    }

    [Test]
    public void Tax_id_lookup_is_normalized_and_keyed()
    {
        Assert.That(fingerprints.TaxId("12.345.678/0001-95"), Is.EqualTo(fingerprints.TaxId("12345678000195")));
        Assert.That(() => fingerprints.TaxId("abc12345678000195"), Throws.TypeOf<AppException>());
    }

    [Test]
    public void Rejects_external_entities_and_unsupported_layouts()
    {
        Assert.That(() => parser.ValidateAndSanitize(Sample("invalid.xml")), Throws.TypeOf<AppException>());
        Assert.That(() => parser.ValidateAndSanitize("<NFe/>"u8.ToArray()), Throws.TypeOf<AppException>());
    }

    [TestCase("150.25", "-1.00")]
    [TestCase("150.25", "1.001")]
    [TestCase("150.25", "NaN")]
    [TestCase("2026-11-25T10:00:00-03:00", "2026-11-25T10:00:00")]
    [TestCase("versao=\"4.00\"", "versao=\"3.10\"")]
    [TestCase("<UF>SP</UF>", "<UF>XX</UF>")]
    public void Rejects_invalid_required_fields(string oldValue, string newValue)
    {
        var input = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Sample("nfe.xml")).Replace(oldValue, newValue, StringComparison.Ordinal));
        Assert.That(() => parser.ValidateAndSanitize(input), Throws.TypeOf<AppException>());
    }

    [Test]
    public void Rejects_duplicate_fields_depth_and_oversized_inputs()
    {
        var duplicate = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Sample("nfe.xml")).Replace("<vNF>150.25</vNF>", "<vNF>150.25</vNF><vNF>1.00</vNF>", StringComparison.Ordinal));
        Assert.That(() => parser.ValidateAndSanitize(duplicate), Throws.TypeOf<AppException>());
        var deep = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<a>", 70)) + string.Concat(Enumerable.Repeat("</a>", 70)));
        Assert.That(() => parser.ValidateAndSanitize(deep), Throws.TypeOf<AppException>());
        Assert.That(() => parser.ValidateAndSanitize(new byte[RequestValidation.MaxFileBytes + 1]), Throws.TypeOf<AppException>());
    }

    [Test]
    public void Sanitized_corruption_is_a_permanent_processing_failure()
        => Assert.That(() => parser.ExtractSanitized("<invalid/>"), Throws.TypeOf<PermanentProcessingException>());
}
