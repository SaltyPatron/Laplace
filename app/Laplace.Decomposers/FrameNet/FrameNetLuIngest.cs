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

    internal static LuDocument? ParseLu(string path)
    {
        XDocument doc;
        try { doc = XDocument.Load(path); }
        catch (XmlException) { return null; }
        return ParseLu(doc);
    }

    internal static LuDocument? ParseLu(XDocument doc)
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
        string luKey = SourceEntityIdConventions.FrameNetLuKey(frameName, luName);

        string definition = FrameNetLemmaHelper.CollapseWs((string?)root.Element(ns + "definition") ?? "");

        var patterns = new List<ValencePatternCount>();

        // FrameNet's own annotated-instance count. A valenceUnit outside a <pattern>
        // carries no total of its own, so it enters at 1 -- the same weight it had before
        // -- rather than borrowing a number the corpus did not give it.
        static long PatternTotal(XElement el)
            => long.TryParse((string?)el.Attribute("total"), out long t) && t > 0 ? t : 1;
        foreach (var vu in root.Descendants(ns + "valenceUnit"))
        {
            string pat = ValencePattern(
                (string?)vu.Attribute("GF") ?? "",
                (string?)vu.Attribute("PT") ?? "",
                (string?)vu.Attribute("FE") ?? "");
            if (pat.Length > 0) patterns.Add(new ValencePatternCount(pat, 1));
        }
        foreach (var patEl in root.Descendants(ns + "pattern"))
        {
            var units = patEl.Elements(ns + "valenceUnit").ToList();
            if (units.Count <= 1) continue;
            var parts = new List<string>(units.Count);
            foreach (var vu in units)
            {
                string pat = ValencePattern(
                    (string?)vu.Attribute("GF") ?? "",
                    (string?)vu.Attribute("PT") ?? "",
                    (string?)vu.Attribute("FE") ?? "");
                if (pat.Length > 0) parts.Add(pat);
            }
            if (parts.Count > 0)
                patterns.Add(new ValencePatternCount(string.Join(" + ", parts), PatternTotal(patEl)));
        }

        var sentences = new List<LuSentence>();
        foreach (var sent in root.Descendants(ns + "sentence"))
        {
            string text = FrameNetLemmaHelper.CollapseWs((string?)sent.Element(ns + "text") ?? "");
            if (text.Length == 0) continue;

            string? target = null;
            foreach (var anno in sent.Elements(ns + "annotationSet"))
            {
                if (!string.Equals((string?)anno.Attribute("status"), "MANUAL", StringComparison.Ordinal))
                    continue;
                foreach (var layer in anno.Elements(ns + "layer"))
                {
                    if (!string.Equals((string?)layer.Attribute("name"), "Target", StringComparison.Ordinal))
                        continue;
                    foreach (var label in layer.Elements(ns + "label"))
                    {
                        if (!string.Equals((string?)label.Attribute("name"), "Target", StringComparison.Ordinal))
                            continue;
                        if (!int.TryParse((string?)label.Attribute("start"), out int start)) continue;
                        if (!int.TryParse((string?)label.Attribute("end"), out int end)) end = start;
                        if (start >= 0 && end >= start && end < text.Length)
                        {
                            target = text.Substring(start, end - start + 1).Trim();
                            break;
                        }
                    }
                }
                if (target is not null) break;
            }
            sentences.Add(new LuSentence(text, target));
        }

        return new LuDocument(id, frameName, luName, luKey, lemma, pos, definition, totalAnnotated, patterns, sentences);
    }

    internal static void EmitLu(SubstrateChangeBuilder b, LuDocument lu, Hash128 source)
    {
        Hash128? luAnchor = AnchorAdmission.Emit(
            b, lu.LuKey, LuTypeId, source, SourceTrust.AcademicCurated);
        Hash128? frameAnchor = CategoryAnchor.Emit(b, lu.FrameName, EntityTypeRegistry.FrameNetFrame, source, SourceTrust.AcademicCurated);
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

        foreach (var pattern in lu.ValencePatterns)
        {
            var patId = ContentEmitter.Emit(b, pattern.Pattern, source);
            if (patId is null) continue;
            // observationCount is what the fold counts as games, so FrameNet's total= --
            // the number of annotated instances realising this pattern -- belongs here.
            // It replaces a count of how often the pattern string repeated in the XML.
            b.AddAttestation(NativeAttestation.Categorical(
                luId, "HAS_VALENCE_PATTERN", patId.Value, source, SourceTrust.AcademicCurated,
                contextId: frameId, observationCount: pattern.Total));
        }

        foreach (var sent in lu.Sentences)
        {
            var sentId = ContentEmitter.Emit(b, sent.Text, source);
            if (sentId is null) continue;
            b.AddAttestation(NativeAttestation.Categorical(
                frameId, "HAS_EXAMPLE", sentId.Value, source, SourceTrust.AcademicCurated,
                contextId: luId));

            if (sent.TargetText is { Length: > 0 } target)
            {
                var targetId = ContentEmitter.Emit(b, target, source);
                if (targetId is not null)
                    b.AddAttestation(NativeAttestation.Categorical(
                        targetId.Value, "EVOKES_FRAME", frameId, source, SourceTrust.AcademicCurated,
                        contextId: sentId.Value));
            }
        }
    }

    private static string ValencePattern(string gf, string pt, string fe)
    {
        gf = gf.Trim();
        pt = pt.Trim();
        fe = fe.Trim();
        if (fe.Length == 0) return "";
        if (gf.Length == 0 && pt.Length == 0) return fe;
        if (gf.Length == 0) return $"{pt}/{fe}";
        if (pt.Length == 0) return $"{gf}/{fe}";
        return $"{gf}/{pt}/{fe}";
    }

    public sealed record LuDocument(
        int Id, string FrameName, string LuName, string LuKey, string Lemma, string Pos, string Definition,
        long TotalAnnotated,
        List<ValencePatternCount> ValencePatterns, List<LuSentence> Sentences);

    /// <summary>
    /// A valence pattern and the number of annotated instances FrameNet recorded for it.
    ///
    /// The corpus states this on every &lt;pattern&gt; as total="N" -- 192,241 of them --
    /// and it was never read. The emitted observation count was instead however many times
    /// the pattern STRING happened to repeat in the XML, a structural artifact of the file
    /// layout, so a pattern annotated 87 times and one annotated once could enter the fold
    /// at the same strength.
    /// </summary>
    public readonly record struct ValencePatternCount(string Pattern, long Total);

    public sealed record LuSentence(string Text, string? TargetText);
}
