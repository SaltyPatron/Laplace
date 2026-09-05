using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;

namespace Laplace.Decomposers.OMW;

public abstract record OmwLmfRecord(string LexiconId, string Language);

public sealed record OmwLmfLexicon(
    string Id,
    string Label,
    string LanguageCode,
    string Version,
    string License,
    string Url,
    string Citation,
    string Email) : OmwLmfRecord(Id, LanguageCode);

public sealed record OmwLmfRequires(
    string Lexicon,
    string LanguageCode,
    string Reference,
    string Version) : OmwLmfRecord(Lexicon, LanguageCode);

public sealed record OmwLmfRelation(string Target, string Type, double? Confidence);
public sealed record OmwLmfTag(string Category, string Value);
public sealed record OmwLmfForm(string WrittenForm, IReadOnlyList<OmwLmfTag> Tags);

public sealed record OmwLmfSense(
    string Id,
    string Synset,
    string Number,
    string Identifier,
    string Subcategorization,
    string AdjectivePosition,
    string Lexicalized,
    string Count,
    IReadOnlyList<OmwLmfRelation> Relations);

public sealed record OmwLmfLexicalEntry(
    string Lexicon,
    string LanguageCode,
    string Id,
    string Index,
    string Lemma,
    string PartOfSpeech,
    string LemmaType,
    IReadOnlyList<OmwLmfForm> Forms,
    IReadOnlyList<OmwLmfSense> Senses) : OmwLmfRecord(Lexicon, LanguageCode);

public sealed record OmwLmfSynset(
    string Lexicon,
    string LanguageCode,
    string Id,
    string Ili,
    string PartOfSpeech,
    string Lexicalized,
    string Lexfile,
    string Identifier,
    IReadOnlyList<string> Members,
    IReadOnlyList<string> Definitions,
    IReadOnlyList<string> Examples,
    IReadOnlyList<OmwLmfRelation> Relations) : OmwLmfRecord(Lexicon, LanguageCode);

public sealed record OmwLmfSyntacticBehaviour(
    string Lexicon,
    string LanguageCode,
    string Id,
    string Frame) : OmwLmfRecord(Lexicon, LanguageCode);

public sealed record OmwLmfSidecar(
    string Lexicon,
    string LanguageCode,
    OmwLmfSidecarKind Kind,
    string Content) : OmwLmfRecord(Lexicon, LanguageCode);

public enum OmwLmfSidecarKind { License, Citation, Readme, ReleaseIndex }

/// <summary>Streaming WN-LMF 1.4 reader. Each lexical entry or synset is one bounded record.</summary>
internal static class OMWLmfParser
{
    internal static async IAsyncEnumerable<OmwLmfRecord> ReadAsync(
        string filePath,
        string fileLabel,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!fileLabel.Contains("/xml/", StringComparison.Ordinal))
        {
            string text = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            var kind = fileLabel.Contains("/license/", StringComparison.Ordinal)
                ? OmwLmfSidecarKind.License
                : fileLabel.Contains("/citation/", StringComparison.Ordinal)
                    ? OmwLmfSidecarKind.Citation
                    : fileLabel.Contains("/readme/", StringComparison.Ordinal)
                        ? OmwLmfSidecarKind.Readme
                        : OmwLmfSidecarKind.ReleaseIndex;
            string lexicon = LexiconFromPath(filePath) ?? "omw-2.0";
            string sidecarLanguage = lexicon.StartsWith("omw-", StringComparison.Ordinal)
                ? lexicon[4..] : "mul";
            yield return new OmwLmfSidecar(lexicon, sidecarLanguage, kind, text);
            yield break;
        }

        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true,
            // OMW 2.0 names the public WN-LMF DTD. The records are self-contained and
            // network resolution is forbidden; ignore the declaration rather than making
            // a current, valid release depend on network availability.
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        });

        string lexiconId = LexiconFromPath(filePath) ?? "omw-unknown";
        string language = lexiconId.StartsWith("omw-", StringComparison.Ordinal)
            ? lexiconId[4..] : "und";

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;
            switch (reader.LocalName)
            {
                case "Lexicon":
                    ValidateAttributes(reader, "id", "label", "language", "email", "license",
                        "version", "url", "citation");
                    lexiconId = Attr(reader, "id", lexiconId);
                    language = Attr(reader, "language", language);
                    yield return new OmwLmfLexicon(
                        lexiconId,
                        Attr(reader, "label"),
                        language,
                        Attr(reader, "version"),
                        Attr(reader, "license"),
                        Attr(reader, "url"),
                        Attr(reader, "citation"),
                        Attr(reader, "email"));
                    break;
                case "Requires":
                    ValidateAttributes(reader, "ref", "version");
                    yield return new OmwLmfRequires(
                        lexiconId, language, Attr(reader, "ref"), Attr(reader, "version"));
                    break;
                case "LexicalEntry":
                {
                    using var subtree = reader.ReadSubtree();
                    XElement element = await XElement.LoadAsync(
                        subtree, LoadOptions.None, ct).ConfigureAwait(false);
                    yield return ParseEntry(element, lexiconId, language);
                    break;
                }
                case "Synset":
                {
                    using var subtree = reader.ReadSubtree();
                    XElement element = await XElement.LoadAsync(
                        subtree, LoadOptions.None, ct).ConfigureAwait(false);
                    yield return ParseSynset(element, lexiconId, language);
                    break;
                }
                case "SyntacticBehaviour":
                    ValidateAttributes(reader, "id", "subcategorizationFrame");
                    yield return new OmwLmfSyntacticBehaviour(
                        lexiconId, language, Attr(reader, "id"),
                        Attr(reader, "subcategorizationFrame"));
                    break;
                case "LexicalResource":
                    break;
                default:
                    throw new InvalidDataException(
                        $"{filePath}: unsupported WN-LMF element '{reader.LocalName}'.");
            }
        }
    }

    private static OmwLmfLexicalEntry ParseEntry(
        XElement element, string lexicon, string language)
    {
        ValidateElement(element, ["id", "index"], ["Lemma", "Form", "Sense"]);
        XElement? lemma = Children(element, "Lemma").FirstOrDefault();
        if (lemma is not null)
            ValidateElement(lemma, ["writtenForm", "partOfSpeech", "type"], []);
        var forms = Children(element, "Form")
            .Select(static form =>
            {
                ValidateElement(form, ["writtenForm"], ["Tag"]);
                var tags = Children(form, "Tag").Select(static tag =>
                {
                    ValidateElement(tag, ["category"], []);
                    return new OmwLmfTag(Attr(tag, "category"), tag.Value.Trim());
                }).ToArray();
                return new OmwLmfForm(Attr(form, "writtenForm"), tags);
            })
            .Where(static form => form.WrittenForm.Length > 0)
            .ToArray();
        var senses = Children(element, "Sense").Select(sense =>
        {
            ValidateElement(sense,
                ["id", "synset", "n", "identifier", "subcat", "adjposition", "lexicalized"],
                ["Count", "SenseRelation"]);
            XElement? count = Children(sense, "Count").FirstOrDefault();
            if (count is not null) ValidateElement(count, [], []);
            return new OmwLmfSense(
                Attr(sense, "id"),
                Attr(sense, "synset"),
                Attr(sense, "n"),
                Attr(sense, "identifier"),
                Attr(sense, "subcat"),
                Attr(sense, "adjposition"),
                Attr(sense, "lexicalized"),
                count?.Value.Trim() ?? "",
                Children(sense, "SenseRelation").Select(ParseRelation).ToArray());
        }).ToArray();
        return new OmwLmfLexicalEntry(
            lexicon,
            language,
            Attr(element, "id"),
            Attr(element, "index"),
            lemma is null ? "" : Attr(lemma, "writtenForm"),
            lemma is null ? "" : Attr(lemma, "partOfSpeech"),
            lemma is null ? "" : Attr(lemma, "type"),
            forms,
            senses);
    }

    private static OmwLmfSynset ParseSynset(
        XElement element, string lexicon, string language)
    {
        ValidateElement(element,
            ["id", "ili", "partOfSpeech", "members", "lexicalized", "lexfile", "identifier"],
            ["Definition", "Example", "SynsetRelation"]);
        foreach (XElement child in Children(element, "Definition").Concat(Children(element, "Example")))
            ValidateElement(child, [], []);
        return new OmwLmfSynset(
            lexicon,
            language,
            Attr(element, "id"),
            Attr(element, "ili"),
            Attr(element, "partOfSpeech"),
            Attr(element, "lexicalized"),
            Attr(element, "lexfile"),
            Attr(element, "identifier"),
            Attr(element, "members").Split(' ', StringSplitOptions.RemoveEmptyEntries),
            Children(element, "Definition").Select(static e => e.Value.Trim())
                .Where(static value => value.Length > 0).ToArray(),
            Children(element, "Example").Select(static e => e.Value.Trim())
                .Where(static value => value.Length > 0).ToArray(),
            Children(element, "SynsetRelation").Select(ParseRelation).ToArray());
    }

    private static OmwLmfRelation ParseRelation(XElement element)
    {
        ValidateElement(element, ["target", "relType", "confidence"], []);
        double? confidence = double.TryParse(
            Attr(element, "confidence"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double parsed) ? parsed : null;
        return new OmwLmfRelation(
            Attr(element, "target"), Attr(element, "relType"), confidence);
    }

    private static IEnumerable<XElement> Children(XElement element, string localName) =>
        element.Elements().Where(child => child.Name.LocalName == localName);

    private static string Attr(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attr => attr.Name.LocalName == localName)?.Value ?? "";

    private static string Attr(XmlReader reader, string localName, string fallback = "")
    {
        if (!reader.HasAttributes) return fallback;
        for (int i = 0; i < reader.AttributeCount; i++)
        {
            reader.MoveToAttribute(i);
            if (reader.LocalName == localName)
            {
                string value = reader.Value;
                reader.MoveToElement();
                return value;
            }
        }
        reader.MoveToElement();
        return fallback;
    }

    private static void ValidateAttributes(XmlReader reader, params string[] allowed)
    {
        if (!reader.HasAttributes) return;
        string elementName = reader.LocalName;
        var set = allowed.ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < reader.AttributeCount; i++)
        {
            reader.MoveToAttribute(i);
            if (reader.Prefix == "xmlns" || reader.Name == "xmlns") continue;
            if (!set.Contains(reader.LocalName))
                throw new InvalidDataException(
                    $"unsupported WN-LMF attribute '{reader.LocalName}' on '{elementName}'.");
        }
        reader.MoveToElement();
    }

    private static void ValidateElement(
        XElement element,
        IReadOnlyCollection<string> allowedAttributes,
        IReadOnlyCollection<string> allowedChildren)
    {
        foreach (XAttribute attribute in element.Attributes())
            if (!attribute.IsNamespaceDeclaration
                && !allowedAttributes.Contains(attribute.Name.LocalName, StringComparer.Ordinal))
                throw new InvalidDataException(
                    $"unsupported WN-LMF attribute '{attribute.Name.LocalName}' on '{element.Name.LocalName}'.");
        foreach (XElement child in element.Elements())
            if (!allowedChildren.Contains(child.Name.LocalName, StringComparer.Ordinal))
                throw new InvalidDataException(
                    $"unsupported WN-LMF child '{child.Name.LocalName}' on '{element.Name.LocalName}'.");
    }

    private static string? LexiconFromPath(string path)
    {
        for (DirectoryInfo? dir = new FileInfo(path).Directory; dir is not null; dir = dir.Parent)
            if (dir.Name.StartsWith("omw-", StringComparison.Ordinal) && dir.Name.Length > 4)
                return dir.Name;
        return null;
    }
}
