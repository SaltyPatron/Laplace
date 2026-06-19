using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.FrameNet;

internal static class FrameNetLuIngest
{
    private static readonly Hash128 LuTypeId = EntityTypeRegistry.FrameNetLu;
    private const string Ns = "http://framenet.icsi.berkeley.edu";

    internal static async IAsyncEnumerable<SubstrateChange> StreamLuAsync(
        string luDir, int batch, Hash128 source,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!Directory.Exists(luDir)) yield break;
        var b = NewBuilder("framenet/lu-0", batch);
        int count = 0, batchNum = 0;

        foreach (var path in Directory.EnumerateFiles(luDir, "lu*.xml").OrderBy(p => p, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            LuDocument? lu = ParseLu(path);
            if (lu is null) continue;

            EmitLu(b, lu, source);

            if (++count >= batch)
            {
                yield return b.Build();
                b = NewBuilder($"framenet/lu-{++batchNum}", batch);
                count = 0;
                await Task.Yield();
            }
        }
        if (count > 0) yield return b.Build();
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

        string? frameName = (string?)root.Attribute("frame");
        string? luName = (string?)root.Attribute("name");
        string pos = (string?)root.Attribute("POS") ?? (string?)root.Element(ns + "lexeme")?.Attribute("POS") ?? "";
        if (string.IsNullOrEmpty(frameName) || string.IsNullOrEmpty(luName) || string.IsNullOrEmpty(pos))
            return null;

        string lemma = LemmaOf(luName);
        if (lemma.Length == 0) return null;

        string definition = CollapseWs((string?)root.Element(ns + "definition") ?? "");

        var patterns = new List<string>();
        foreach (var vu in root.Descendants(ns + "valenceUnit"))
        {
            string pat = ValencePattern(
                (string?)vu.Attribute("GF") ?? "",
                (string?)vu.Attribute("PT") ?? "",
                (string?)vu.Attribute("FE") ?? "");
            if (pat.Length > 0) patterns.Add(pat);
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
            if (parts.Count > 0) patterns.Add(string.Join(" + ", parts));
        }

        var sentences = new List<LuSentence>();
        foreach (var sent in root.Descendants(ns + "sentence"))
        {
            string text = CollapseWs((string?)sent.Element(ns + "text") ?? "");
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

        return new LuDocument(id, frameName, lemma, pos, definition, patterns, sentences);
    }

    private static void EmitLu(SubstrateChangeBuilder b, LuDocument lu, Hash128 source)
    {
        Hash128? luAnchor = CategoryAnchor.Emit(b, lu.Id.ToString(), LuTypeId, source, SourceTrust.AcademicCurated);
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
                contextId: luId));
        }

        if (lu.Definition.Length > 0)
        {
            var defId = ContentEmitter.Emit(b, lu.Definition, source);
            if (defId is not null)
                b.AddAttestation(NativeAttestation.Categorical(
                    luId, "HAS_DEFINITION", defId.Value, source, SourceTrust.AcademicCurated,
                    contextId: frameId));
        }

        foreach (string pattern in lu.ValencePatterns)
        {
            var patId = ContentEmitter.Emit(b, pattern, source);
            if (patId is null) continue;
            b.AddAttestation(NativeAttestation.Categorical(
                luId, "HAS_VALENCE_PATTERN", patId.Value, source, SourceTrust.AcademicCurated,
                contextId: frameId));
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

    private static string LemmaOf(string luName)
    {
        int dot = luName.LastIndexOf('.');
        return (dot > 0 ? luName[..dot] : luName).Trim();
    }

    private static string CollapseWs(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool ws = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) { ws = true; continue; }
            if (ws && sb.Length > 0) sb.Append(' ');
            ws = false;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static SubstrateChangeBuilder NewBuilder(string unit, int batch) =>
        new(FrameNetDecomposer.Source, unit, null,
            entityCapacity:      batch * 48,
            physicalityCapacity: batch * 48,
            attestationCapacity: batch * 64);

    internal sealed record LuDocument(
        int Id, string FrameName, string Lemma, string Pos, string Definition,
        List<string> ValencePatterns, List<LuSentence> Sentences);

    internal sealed record LuSentence(string Text, string? TargetText);
}
