using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using FiscalDocuments.Application;
using FiscalDocuments.Domain;

namespace FiscalDocuments.Infrastructure.Xml;

public sealed class FiscalXmlDocuments(IFingerprints fingerprints) : IXmlDocuments
{
    private const string NfeNamespace = "http://www.portalfiscal.inf.br/nfe";
    private const string CteNamespace = "http://www.portalfiscal.inf.br/cte";
    private const string NfseNamespace = "http://www.sped.fazenda.gov.br/nfse";
    private static readonly string[] States = ["AC", "AL", "AP", "AM", "BA", "CE", "DF", "ES", "GO", "MA", "MT", "MS", "MG", "PA", "PB", "PR", "PE", "PI", "RJ", "RN", "RS", "RO", "RR", "SC", "SP", "SE", "TO"];
    private static readonly Dictionary<string, string> IbgeStates = new()
    {
        ["11"] = "RO", ["12"] = "AC", ["13"] = "AM", ["14"] = "RR", ["15"] = "PA", ["16"] = "AP", ["17"] = "TO",
        ["21"] = "MA", ["22"] = "PI", ["23"] = "CE", ["24"] = "RN", ["25"] = "PB", ["26"] = "PE", ["27"] = "AL", ["28"] = "SE", ["29"] = "BA",
        ["31"] = "MG", ["32"] = "ES", ["33"] = "RJ", ["35"] = "SP", ["41"] = "PR", ["42"] = "SC", ["43"] = "RS",
        ["50"] = "MS", ["51"] = "MT", ["52"] = "GO", ["53"] = "DF"
    };
    public static bool IsState(string value) => States.Contains(value, StringComparer.Ordinal);

    public FiscalXml ValidateAndSanitize(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > RequestValidation.MaxFileBytes) throw new AppException("invalid_xml_size");
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            var root = Read(stream).Root ?? throw new AppException("invalid_xml");
            DocumentType type;
            string identity;
            string issuer;
            string state;
            string issued;
            string total;
            if (root.Name.NamespaceName == NfeNamespace && root.Name.LocalName is "NFe" or "nfeProc")
            {
                type = DocumentType.NFe;
                var body = root.Name.LocalName == "NFe" ? root : One(root, "NFe");
                var info = One(body, "infNFe");
                identity = AccessKey(info, "NFe", 44);
                var emit = One(info, "emit");
                issuer = TaxIdentifier(emit);
                state = Text(One(emit, "enderEmit"), "UF");
                var ide = One(info, "ide");
                issued = Text(ide, "dhEmi");
                total = Text(One(One(info, "total"), "ICMSTot"), "vNF");
            }
            else if (root.Name.NamespaceName == CteNamespace && root.Name.LocalName is "CTe" or "cteProc")
            {
                type = DocumentType.CTe;
                var body = root.Name.LocalName == "CTe" ? root : One(root, "CTe");
                var info = One(body, "infCte");
                identity = AccessKey(info, "CTe", 44);
                var emit = One(info, "emit");
                issuer = TaxIdentifier(emit);
                state = Text(One(emit, "enderEmit"), "UF");
                var ide = One(info, "ide");
                issued = Text(ide, "dhEmi");
                total = Text(One(info, "vPrest"), "vTPrest");
            }
            else if (root.Name.NamespaceName == NfseNamespace && root.Name.LocalName == "NFSe")
            {
                type = DocumentType.NFSe;
                var info = One(root, "infNFSe");
                identity = AccessKey(info, "NFS", 50);
                var dps = One(One(info, "DPS"), "infDPS");
                var provider = One(dps, "prest");
                issuer = TaxIdentifier(provider);
                var municipality = Text(One(One(provider, "end"), "endNac"), "cMun");
                if (municipality.Length != 7 || !municipality.All(char.IsAsciiDigit) || !IbgeStates.TryGetValue(municipality[..2], out state!))
                    throw new AppException("invalid_municipality");
                issued = Text(dps, "dhEmi");
                total = Text(One(One(dps, "valores"), "vServPrest"), "vServ");
            }
            else throw new AppException("unsupported_xml_layout");

            if (!IsState(state)) throw new AppException("invalid_state");
            // As datas e horas fiscais devem incluir um fuso horário explícito; nunca dependa do fuso do servidor.
            if (!(issued.EndsWith('Z') || issued.Length >= 6 && issued[^6] is '+' or '-' && issued[^3] == ':') ||
                !DateTimeOffset.TryParse(issued, CultureInfo.InvariantCulture, DateTimeStyles.None, out var issuedAt))
                throw new AppException("invalid_issue_date");
            var amount = Amount(total);
            var issuerHmac = fingerprints.TaxId(issuer);
            var maskedIssuer = new string('*', issuer.Length);
            // Projeção apenas dos campos permitidos: texto livre, chaves de acesso, referências, assinaturas,
            // certificados e atributos arbitrários da entrada nunca são persistidos.
            var sanitized = new XElement("FiscalDocument", new XAttribute("schema", "1"),
                new XElement("Type", type.ToString()), new XElement("Issuer", maskedIssuer),
                new XElement("IssuedAt", issuedAt.ToString("O", CultureInfo.InvariantCulture)),
                new XElement("State", state), new XElement("TotalAmount", amount.ToString("F2", CultureInfo.InvariantCulture)))
                .ToString(SaveOptions.DisableFormatting);
            return new FiscalXml(type, fingerprints.Hash($"identity:{type}:{identity}"), fingerprints.Hash(bytes.Span),
                maskedIssuer, issuerHmac, issuedAt, state, sanitized);
        }
        catch (XmlException) { throw new AppException("malformed_or_unsafe_xml"); }
        catch (InvalidOperationException) { throw new AppException("ambiguous_xml"); }
    }

    public ExtractedData ExtractSanitized(string xml)
    {
        try
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
            var root = Read(stream).Root;
            if (root?.Name != "FiscalDocument" || (string?)root.Attribute("schema") != "1")
                throw new AppException("invalid_sanitized_xml");
            var amount = Amount(Text(root, "TotalAmount"));
            var date = DateTimeOffset.ParseExact(Text(root, "IssuedAt"), "O", CultureInfo.InvariantCulture);
            return new ExtractedData(amount, date.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is XmlException or AppException or FormatException or InvalidOperationException)
        { throw new PermanentProcessingException("invalid_sanitized_xml"); }
    }

    private static XDocument Read(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = RequestValidation.MaxFileBytes,
            MaxCharactersFromEntities = 0, IgnoreComments = true, IgnoreProcessingInstructions = true
        });
        // Aplica um limite de profundidade antes de materializar a árvore XML de tamanho limitado.
        var document = new XDocument();
        using var writer = document.CreateWriter();
        while (reader.Read())
        {
            if (reader.Depth > 64) throw new AppException("xml_too_deep");
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    writer.WriteStartElement(reader.Prefix, reader.LocalName, reader.NamespaceURI);
                    if (reader.HasAttributes)
                    {
                        while (reader.MoveToNextAttribute()) writer.WriteAttributeString(reader.Prefix, reader.LocalName, reader.NamespaceURI, reader.Value);
                        reader.MoveToElement();
                    }
                    if (reader.IsEmptyElement) writer.WriteEndElement();
                    break;
                case XmlNodeType.EndElement: writer.WriteFullEndElement(); break;
                case XmlNodeType.Text: case XmlNodeType.CDATA: case XmlNodeType.SignificantWhitespace: writer.WriteString(reader.Value); break;
            }
        }
        writer.Flush();
        return document;
    }

    private static XElement One(XElement parent, string localName)
    {
        var elements = parent.Elements(parent.Name.Namespace + localName).Take(2).ToArray();
        if (elements.Length != 1) throw new AppException("missing_or_ambiguous_field");
        return elements[0];
    }
    private static string Text(XElement parent, string name)
    {
        var element = One(parent, name);
        if (element.HasElements || string.IsNullOrWhiteSpace(element.Value)) throw new AppException("invalid_field");
        return element.Value.Trim();
    }
    private static string TaxIdentifier(XElement parent)
    {
        var nodes = parent.Elements().Where(e => e.Name.Namespace == parent.Name.Namespace && e.Name.LocalName is "CPF" or "CNPJ").ToArray();
        if (nodes.Length != 1 || nodes[0].HasElements) throw new AppException("invalid_tax_id");
        var value = nodes[0].Value.Trim();
        if (value.Length != (nodes[0].Name.LocalName == "CPF" ? 11 : 14) || !value.All(char.IsAsciiDigit)) throw new AppException("invalid_tax_id");
        return value;
    }
    private static string AccessKey(XElement info, string prefix, int length)
    {
        var value = (string?)info.Attribute("Id") ?? "";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length != prefix.Length + length || !value[prefix.Length..].All(char.IsAsciiDigit))
            throw new AppException("invalid_fiscal_identity");
        return value[prefix.Length..];
    }
    private static decimal Amount(string value)
    {
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) ||
            amount < 0 || amount > 9999999999999999.99m || decimal.Round(amount, 2) != amount)
            throw new AppException("invalid_total_amount");
        return amount;
    }
}
