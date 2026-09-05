using System.Runtime.CompilerServices;
using Laplace.Engine.Core;

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

        string lexiconId = LexiconFromPath(filePath) ?? "omw-unknown";
        string language = lexiconId.StartsWith("omw-", StringComparison.Ordinal)
            ? lexiconId[4..] : "und";

        await foreach (XmlRecordFrame frame in XmlRecordReader.ReadAsync(
            filePath, recordDepth: 2, ct: ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            XmlRecordNode node = frame.Node;
            if (frame.Kind == XmlRecordFrameKind.Text)
            {
                if (!string.IsNullOrWhiteSpace(node.Value))
                    throw new InvalidDataException(
                        $"{filePath}: unsupported text directly inside '{node.Name}'.");
                continue;
            }
            if (frame.Kind == XmlRecordFrameKind.ContainerHeader)
            {
                switch (node.Name)
                {
                    case "LexicalResource":
                        ValidateElement(node, [], []);
                        break;
                    case "Lexicon":
                        ValidateElement(node,
                            ["id", "label", "language", "email", "license",
                             "version", "url", "citation"], []);
                        lexiconId = node.Attribute("id", lexiconId);
                        language = node.Attribute("language", language);
                        yield return new OmwLmfLexicon(
                            lexiconId,
                            node.Attribute("label"),
                            language,
                            node.Attribute("version"),
                            node.Attribute("license"),
                            node.Attribute("url"),
                            node.Attribute("citation"),
                            node.Attribute("email"));
                        break;
                    default:
                        throw new InvalidDataException(
                            $"{filePath}: unsupported WN-LMF container '{node.Name}'.");
                }
                continue;
            }

            switch (node.Name)
            {
                case "Requires":
                    ValidateElement(node, ["ref", "version"], []);
                    yield return new OmwLmfRequires(
                        lexiconId, language, node.Attribute("ref"), node.Attribute("version"));
                    break;
                case "LexicalEntry":
                    yield return ParseEntry(node, lexiconId, language);
                    break;
                case "Synset":
                    yield return ParseSynset(node, lexiconId, language);
                    break;
                case "SyntacticBehaviour":
                    ValidateElement(node, ["id", "subcategorizationFrame"], []);
                    yield return new OmwLmfSyntacticBehaviour(
                        lexiconId, language, node.Attribute("id"),
                        node.Attribute("subcategorizationFrame"));
                    break;
                default:
                    throw new InvalidDataException(
                        $"{filePath}: unsupported WN-LMF element '{node.Name}'.");
            }
        }
    }

    private static OmwLmfLexicalEntry ParseEntry(
        XmlRecordNode element, string lexicon, string language)
    {
        ValidateElement(element, ["id", "index"], ["Lemma", "Form", "Sense"]);
        XmlRecordNode? lemma = Children(element, "Lemma").FirstOrDefault();
        if (lemma is not null)
            ValidateElement(lemma, ["writtenForm", "partOfSpeech", "type"], []);
        var forms = Children(element, "Form")
            .Select(static form =>
            {
                ValidateElement(form, ["writtenForm"], ["Tag"]);
                var tags = Children(form, "Tag").Select(static tag =>
                {
                    ValidateElement(tag, ["category"], []);
                    return new OmwLmfTag(tag.Attribute("category"), tag.Value.Trim());
                }).ToArray();
                return new OmwLmfForm(form.Attribute("writtenForm"), tags);
            })
            .Where(static form => form.WrittenForm.Length > 0)
            .ToArray();
        var senses = Children(element, "Sense").Select(sense =>
        {
            ValidateElement(sense,
                ["id", "synset", "n", "identifier", "subcat", "adjposition", "lexicalized"],
                ["Count", "SenseRelation"]);
            XmlRecordNode? count = Children(sense, "Count").FirstOrDefault();
            if (count is not null) ValidateElement(count, [], []);
            return new OmwLmfSense(
                sense.Attribute("id"),
                sense.Attribute("synset"),
                sense.Attribute("n"),
                sense.Attribute("identifier"),
                sense.Attribute("subcat"),
                sense.Attribute("adjposition"),
                sense.Attribute("lexicalized"),
                count?.Value.Trim() ?? "",
                Children(sense, "SenseRelation").Select(ParseRelation).ToArray());
        }).ToArray();
        return new OmwLmfLexicalEntry(
            lexicon,
            language,
            element.Attribute("id"),
            element.Attribute("index"),
            lemma?.Attribute("writtenForm") ?? "",
            lemma?.Attribute("partOfSpeech") ?? "",
            lemma?.Attribute("type") ?? "",
            forms,
            senses);
    }

    private static OmwLmfSynset ParseSynset(
        XmlRecordNode element, string lexicon, string language)
    {
        ValidateElement(element,
            ["id", "ili", "partOfSpeech", "members", "lexicalized", "lexfile", "identifier"],
            ["Definition", "Example", "SynsetRelation"]);
        foreach (XmlRecordNode child in Children(element, "Definition").Concat(Children(element, "Example")))
            ValidateElement(child, [], []);
        return new OmwLmfSynset(
            lexicon,
            language,
            element.Attribute("id"),
            element.Attribute("ili"),
            element.Attribute("partOfSpeech"),
            element.Attribute("lexicalized"),
            element.Attribute("lexfile"),
            element.Attribute("identifier"),
            element.Attribute("members").Split(' ', StringSplitOptions.RemoveEmptyEntries),
            Children(element, "Definition").Select(static e => e.Value.Trim())
                .Where(static value => value.Length > 0).ToArray(),
            Children(element, "Example").Select(static e => e.Value.Trim())
                .Where(static value => value.Length > 0).ToArray(),
            Children(element, "SynsetRelation").Select(ParseRelation).ToArray());
    }

    private static OmwLmfRelation ParseRelation(XmlRecordNode element)
    {
        ValidateElement(element, ["target", "relType", "confidence"], []);
        double? confidence = double.TryParse(
            element.Attribute("confidence"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double parsed) ? parsed : null;
        return new OmwLmfRelation(
            element.Attribute("target"), element.Attribute("relType"), confidence);
    }

    private static IEnumerable<XmlRecordNode> Children(
        XmlRecordNode element, string localName) => element.ChildrenNamed(localName);

    private static void ValidateElement(
        XmlRecordNode element,
        IReadOnlyCollection<string> allowedAttributes,
        IReadOnlyCollection<string> allowedChildren)
    {
        foreach (XmlRecordAttribute attribute in element.Attributes)
            if (!allowedAttributes.Contains(attribute.Name, StringComparer.Ordinal))
                throw new InvalidDataException(
                    $"unsupported WN-LMF attribute '{attribute.Name}' on '{element.Name}'.");
        foreach (XmlRecordNode child in element.Children)
            if (!allowedChildren.Contains(child.Name, StringComparer.Ordinal))
                throw new InvalidDataException(
                    $"unsupported WN-LMF child '{child.Name}' on '{element.Name}'.");
    }

    private static string? LexiconFromPath(string path)
    {
        for (DirectoryInfo? dir = new FileInfo(path).Directory; dir is not null; dir = dir.Parent)
            if (dir.Name.StartsWith("omw-", StringComparison.Ordinal) && dir.Name.Length > 4)
                return dir.Name;
        return null;
    }
}
