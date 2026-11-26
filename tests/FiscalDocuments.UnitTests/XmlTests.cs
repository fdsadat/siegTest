using System.Text;
using System.Xml.Linq;
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

    [TestCase("nfe.xml", null)]
    [TestCase("nfe.xml", "9.99")]
    [TestCase("cte.xml", null)]
    [TestCase("cte.xml", "9.99")]
    [TestCase("nfse.xml", null)]
    [TestCase("nfse.xml", "9.99")]
    public void Accepts_missing_or_different_versions_when_required_structure_is_preserved(string name, string? version)
    {
        var original = parser.ValidateAndSanitize(Sample(name));
        var document = XDocument.Parse(Encoding.UTF8.GetString(Sample(name)));
        foreach (var attribute in document.Descendants().Attributes("versao").ToList())
        {
            if (version is null)
            {
                attribute.Remove();
            }
            else
            {
                attribute.Value = version;
            }
        }
        var parsed = parser.ValidateAndSanitize(Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting)));
        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.EqualTo(original with { ContentHmac = parsed.ContentHmac }));
            Assert.That(parsed.ContentHmac, Is.Not.EqualTo(original.ContentHmac));
        });
    }

    [TestCase("nfe.xml", null)]
    [TestCase("nfe.xml", "99")]
    [TestCase("cte.xml", null)]
    [TestCase("cte.xml", "99")]
    public void Accepts_missing_or_different_models_when_required_structure_is_preserved(string name, string? model)
    {
        var original = parser.ValidateAndSanitize(Sample(name));
        var document = XDocument.Parse(Encoding.UTF8.GetString(Sample(name)));
        var field = document.Descendants().Single(e => e.Name.LocalName == "mod");
        if (model is null)
        {
            field.Remove();
        }
        else
        {
            field.Value = model;
        }

        var parsed = parser.ValidateAndSanitize(Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting)));
        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.EqualTo(original with { ContentHmac = parsed.ContentHmac }));
            Assert.That(parsed.ContentHmac, Is.Not.EqualTo(original.ContentHmac));
        });
    }

    [TestCase("nfe.xml")]
    [TestCase("cte.xml")]
    public void Accepts_issuer_different_from_access_key_and_uses_declared_issuer(string name)
    {
        var original = parser.ValidateAndSanitize(Sample(name));
        var input = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Sample(name))
            .Replace("<CNPJ>12345678000195</CNPJ>", "<CNPJ>98765432000199</CNPJ>", StringComparison.Ordinal));
        var parsed = parser.ValidateAndSanitize(input);
        Assert.Multiple(() =>
        {
            Assert.That(parsed.IdentityHmac, Is.EqualTo(original.IdentityHmac));
            Assert.That(parsed.ContentHmac, Is.Not.EqualTo(original.ContentHmac));
            Assert.That(parsed.IssuerHmac, Is.EqualTo(fingerprints.TaxId("98765432000199")));
            Assert.That(parsed.SanitizedXml, Is.EqualTo(original.SanitizedXml));
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

    [TestCase("35")]
    [TestCase("35abc")]
    [TestCase("3550308123")]
    public void Nfse_uses_state_prefix_without_validating_municipality_format(string municipality)
    {
        var input = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Sample("nfse.xml"))
            .Replace("<cMun>3550308</cMun>", $"<cMun>{municipality}</cMun>", StringComparison.Ordinal));
        var parsed = parser.ValidateAndSanitize(input);
        Assert.That(parsed.State, Is.EqualTo("SP"));
    }

    [TestCase("3")]
    [TestCase("99")]
    public void Nfse_keeps_state_empty_when_municipality_prefix_is_unknown(string municipality)
    {
        var input = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Sample("nfse.xml"))
            .Replace("<cMun>3550308</cMun>", $"<cMun>{municipality}</cMun>", StringComparison.Ordinal));
        var parsed = parser.ValidateAndSanitize(input);
        Assert.That(parsed.State, Is.Empty);
    }

    [TestCase("nfe.xml")]
    [TestCase("cte.xml")]
    public void Accepts_unrecognised_state_values_that_fit_storage(string name)
    {
        var input = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Sample(name))
            .Replace("<UF>SP</UF>", "<UF>XX</UF>", StringComparison.Ordinal));
        var parsed = parser.ValidateAndSanitize(input);
        Assert.Multiple(() =>
        {
            Assert.That(parsed.State, Is.EqualTo("XX"));
            Assert.That(parsed.SanitizedXml, Does.Contain("<State>XX</State>"));
        });
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
    [TestCase("<UF>SP</UF>", "<UF>ABC</UF>")]
    [TestCase("<vNF>150.25</vNF>", "")]
    [TestCase("<vNF>150.25</vNF>", "<vNF> </vNF>")]
    [TestCase("150.25", "10000000000000000.00")]
    [TestCase("<CNPJ>12345678000195</CNPJ>", "<CNPJ>123</CNPJ>")]
    [TestCase("Id=\"NFe35261112345678000195550010000000011000000010\"", "")]
    [TestCase("http://www.portalfiscal.inf.br/nfe", "urn:unsupported")]
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
