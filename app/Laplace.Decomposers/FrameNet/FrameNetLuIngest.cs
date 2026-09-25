using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.FrameNet;

public static class FrameNetLuIngest
{
    private static readonly Hash128 LuTypeId = EntityTypeRegistry.FrameNetLu;
    private const string Ns = "http://framenet.icsi.berkeley.edu";

    internal static async IAsyncEnumerable<LuDocument> ParseAllLusAsync(
        string luDir, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!Directory.Exists(luDir)) yield break;
        foreach (var path in Directory.EnumerateFiles(luDir, "lu*.xml").OrderBy(p => p, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (ParseLu(path) is { } lu) yield return lu;
        }
    }

    internal static LuDocument? ParseLu(string path, string? fileLabel = null)
    {
        XDocument doc;
        try { doc = XDocument.Load(path); }
        catch (XmlException) { return null; }
        return ParseLu(doc, fileLabel ?? $"framenet/lu/{Path.GetFileName(path)}");
    }

    internal static LuDocument? ParseLu(XDocument doc, string? fileLabel = null)
    {
        XNamespace ns = Ns;
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "lexUnit") return null;
        if (!int.TryParse((string?)root.Attribute("ID"), out int id)) return null;

        // FrameNet states how many instances back this lexical unit as
        // totalAnnotated="N" on every <lexUnit> -- 13,572 of them -- and it was
        // never read, so an LU with 116 annotated instances entered the fold at the
        // same strength as one with a single annotation. Absent or unparseable
        // means the corpus did not say, which is one observation, not zero.
        long totalAnnotated =
            long.TryParse((string?)root.Attribute("totalAnnotated"), out long ta) && ta > 0 ? ta : 1;

        string? frameName = (string?)root.Attribute("frame");
        string? luName = (string?)root.Attribute("name");
        string pos = (string?)root.Attribute("POS") ?? (string?)root.Element(ns + "lexeme")?.Attribute("POS") ?? "";
        if (string.IsNullOrEmpty(frameName) || string.IsNullOrEmpty(luName) || string.IsNullOrEmpty(pos))
            return null;

        string lemma = FrameNetLemmaHelper.LemmaOf(luName);
        if (lemma.Length == 0) return null;

        string definition = FrameNetLemmaHelper.CollapseWs((string?)root.Element(ns + "definition") ?? "");

        // Every <pattern> (single-unit under FERealization, multi-unit under
        // FEGroupRealization) is one realization FrameNet counted: total="N".
        var patterns = new List<ValencePattern>();
        static long PatternTotal(XElement el)
            => long.TryParse((string?)el.Attribute("total"), out long t) && t > 0 ? t : 1;
        foreach (var patEl in root.Descendants(ns + "pattern"))
        {
            var units = new List<ValenceUnit>();
            foreach (var vu in patEl.Elements(ns + "valenceUnit"))
            {
                string fe = ((string?)vu.Attribute("FE") ?? "").Trim();
                if (fe.Length == 0) continue;
                units.Add(new ValenceUnit(fe,
                    ((string?)vu.Attribute("GF") ?? "").Trim(),
                    ((string?)vu.Attribute("PT") ?? "").Trim()));
            }
            if (units.Count > 0) patterns.Add(new ValencePattern(units, PatternTotal(patEl)));
        }

        var sentences = new List<LuSentence>();
        foreach (var sent in root.Descendants(ns + "sentence"))
        {
            // Annotation coordinates refer to the original XML text. Collapsing
            // spaces before slicing moves every later target and role boundary.
            string text = (string?)sent.Element(ns + "text") ?? "";
            if (text.Length == 0) continue;
            var annotations = new List<FrameNetDecomposer.FulltextAnno>();
            foreach (var anno in sent.Elements(ns + "annotationSet"))
            {
                var layers = anno.Elements(ns + "layer").Select(layer =>
                    new FrameNetDecomposer.AnnotationLayer(
                        (string?)layer.Attribute("name") ?? "", (string?)layer.Attribute("rank"),
                        layer.Elements(ns + "label").Select(label =>
                            FrameNetDecomposer.ReadAnnotationLabel(
                                (string?)label.Attribute("name"), (string?)label.Attribute("start"),
                                (string?)label.Attribute("end"), (string?)label.Attribute("itype")))
                            .ToList())).ToArray();
                if (FrameNetDecomposer.CreateAnnotation(
                        text, (string?)anno.Attribute("frameName") ?? frameName, layers,
                        fileLabel ?? $"framenet/lu/lu{id}.xml",
                        (string?)sent.Attribute("ID") ?? "", (string?)anno.Attribute("ID") ?? "",
                        (string?)anno.Attribute("status")) is { } annotation)
                    annotations.Add(annotation);
            }
            sentences.Add(new LuSentence(text, annotations));
        }

        return new LuDocument(id, frameName, luName, lemma, pos, definition, totalAnnotated, patterns, sentences);
    }

    internal static void EmitLu(SubstrateChangeBuilder b, LuDocument lu, Hash128 source)
    {
        // A lexical unit is the ordered composition [frame, lemma, UPOS]: the word, as
        // the part of speech it has, evoking the frame. No joined "Frame/lemma.pos" key.
        Hash128? luAnchor = DeclareLexicalUnit(b, lu.FrameName, lu.LuName, source);
        Hash128? frameAnchor = CategoryAnchor.Emit(b, lu.FrameName, source);
        if (luAnchor is null || frameAnchor is null) return;
        Hash128 luId = luAnchor.Value;
        Hash128 frameId = frameAnchor.Value;

        var lemmaId = ContentEmitter.Emit(b, lu.Lemma, source);
        if (lemmaId is not null)
        {
            PosReference.Attest(b, lemmaId.Value, lu.Pos, PosReference.PosTagset.FrameNet,
                source, null, SourceTrust.AcademicCurated, FrameNetDecomposer.VocabularyNames);
            b.AddAttestation(NativeAttestation.Categorical(
                lemmaId.Value, "EVOKES_FRAME", frameId, source, SourceTrust.AcademicCurated,
                contextId: luId, observationCount: lu.TotalAnnotated));
        }

        if (lu.Definition.Length > 0)
        {
            var defId = ContentEmitter.Emit(b, lu.Definition, source);
            if (defId is not null)
                b.AddAttestation(NativeAttestation.Categorical(
                    luId, "HAS_DEFINITION", defId.Value, source, SourceTrust.AcademicCurated,
                    contextId: frameId));
        }

        // A valence unit is [frame element, grammatical function, phrase type
        // (, preposition)]; a pattern is the ordered composition of its units. Every
        // part stays reachable: the frame element under its frame, "Ext", "NP", "in".
        // FrameNet's total= (annotated instances realizing the pattern) is the games.
        foreach (var pattern in lu.ValencePatterns)
        {
            var units = new List<OrderedCompositionComponent>(pattern.Units.Count);
            foreach (var unit in pattern.Units)
            {
                var fe = RoleAnchor.DeclareComponent(
                    b, RoleIdentityKind.FrameNet, frameId, unit.FrameElement,
                    EntityTypeRegistry.FrameNetFe, source);
                if (fe is null) continue;
                var parts = new List<OrderedCompositionComponent> { fe.Value };
                if (StructureComposer.Text(b, unit.GrammaticalFunction, source) is { } gf) parts.Add(gf);
                var (phrase, preposition) = SplitPhraseType(unit.PhraseType);
                if (StructureComposer.Text(b, phrase, source) is { } pt) parts.Add(pt);
                if (StructureComposer.Text(b, preposition, source) is { } prep) parts.Add(prep);
                units.Add(StructureComposer.Compose(
                    b, EntityTypeRegistry.FrameNetValenceUnit, source, parts));
            }
            if (units.Count == 0) continue;
            var patternId = StructureComposer.Compose(
                b, EntityTypeRegistry.FrameNetValencePattern, source, units).Id;
            b.AddAttestation(NativeAttestation.Categorical(
                luId, "HAS_VALENCE_PATTERN", patternId, source, SourceTrust.AcademicCurated,
                observationCount: pattern.Total));
        }

        foreach (var sent in lu.Sentences)
        {
            var sentId = ContentEmitter.Emit(b, sent.Text, source);
            if (sentId is null) continue;
            b.AddAttestation(NativeAttestation.Categorical(
                frameId, "HAS_EXAMPLE", sentId.Value, source, SourceTrust.AcademicCurated,
                contextId: luId));

            foreach (var annotation in sent.Annotations)
                FrameNetDecomposer.ComposeFulltextAnno(annotation, b);
        }
    }

    /// <summary>"PP[in]" is the phrase type PP realized with the preposition "in".</summary>
    private static (string Phrase, string? Preposition) SplitPhraseType(string pt)
    {
        int open = pt.IndexOf('[');
        if (open > 0 && pt.EndsWith(']'))
            return (pt[..open], pt[(open + 1)..^1]);
        return (pt, null);
    }

    public sealed record LuDocument(
        int Id, string FrameName, string LuName, string Lemma, string Pos, string Definition,
        long TotalAnnotated,
        List<ValencePattern> ValencePatterns, List<LuSentence> Sentences);

    /// <summary>
    /// A valence pattern and the number of annotated instances FrameNet recorded for it.
    ///
    /// The corpus states this on every &lt;pattern&gt; as total="N" -- 192,241 of them --
    /// and it was never read. The emitted observation count was instead however many times
    /// the pattern STRING happened to repeat in the XML, a structural artifact of the file
    /// layout, so a pattern annotated 87 times and one annotated once could enter the fold
    /// at the same strength.
    /// </summary>
    public readonly record struct ValencePattern(IReadOnlyList<ValenceUnit> Units, long Total);

    /// <summary>A frame element as FrameNet realized it: grammatical function and phrase type.</summary>
    public readonly record struct ValenceUnit(string FrameElement, string GrammaticalFunction, string PhraseType);

    /// <summary>
    /// The lexical unit named "lemma.pos" in a frame, declared as the ordered composition
    /// [frame, lemma, UPOS]. Every path that references a lexical unit uses this.
    /// </summary>
    /// <summary>The lexical unit's identity without admitting it (same law as Declare).</summary>
    public static Hash128? LexicalUnitId(string frameName, string luName)
    {
        string lemma = FrameNetLemmaHelper.LemmaOf(luName);
        int dot = luName.LastIndexOf('.');
        string pos = dot > 0 ? luName[(dot + 1)..].ToLowerInvariant() : "";
        if (lemma.Length == 0 || pos.Length == 0 || string.IsNullOrWhiteSpace(frameName)) return null;
        Hash128? frame = ContentEmitter.RootId(frameName.Trim());
        Hash128? word = ContentEmitter.RootId(lemma.Trim());
        Hash128? upos = ContentEmitter.RootId(PosReference.ResolveLabel(pos, PosReference.PosTagset.FrameNet));
        if (frame is null || word is null || upos is null) return null;
        return StructureComposer.Id([frame.Value, word.Value, upos.Value]);
    }

    public static Hash128? DeclareLexicalUnit(
        SubstrateChangeBuilder b, string frameName, string luName, Hash128 source)
    {
        string lemma = FrameNetLemmaHelper.LemmaOf(luName);
        int dot = luName.LastIndexOf('.');
        string pos = dot > 0 ? luName[(dot + 1)..].ToLowerInvariant() : "";
        if (lemma.Length == 0 || pos.Length == 0) return null;
        var frame = StructureComposer.Text(b, frameName, source);
        var word = StructureComposer.Text(b, lemma, source);
        var upos = StructureComposer.Text(b,
            PosReference.ResolveLabel(pos, PosReference.PosTagset.FrameNet), source);
        if (frame is null || word is null || upos is null) return null;
        return StructureComposer.Compose(b, LuTypeId, source, [frame.Value, word.Value, upos.Value]).Id;
    }

    public sealed record LuSentence(
        string Text, IReadOnlyList<FrameNetDecomposer.FulltextAnno> Annotations)
    {
        public string? TargetText => Annotations.FirstOrDefault()?.TargetText;
    }
}
