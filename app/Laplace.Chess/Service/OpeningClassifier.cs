using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

/// <summary>
/// Opening classifier over the Lichess catalog files <c>a.tsv</c> through <c>e.tsv</c>.
/// A game matches by UCI prefix, and by the terminal position of a catalog line, so a
/// transposition reaches the line that ended on that board. There is no hardcoded list.
/// </summary>
public static class OpeningClassifier
{
    public readonly record struct OpeningMatch(string? Name, string? Eco)
    {
        public string? Family
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Name)) return null;
                int colon = Name.IndexOf(':');
                string family = (colon < 0 ? Name : Name[..colon]).Trim();
                return family.Length == 0 ? null : family;
            }
        }

        public string? Variation
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Name)) return null;
                int colon = Name.IndexOf(':');
                if (colon < 0) return null;
                string variation = Name[(colon + 1)..].Trim();
                return variation.Length == 0 ? null : variation;
            }
        }
    }

    private readonly record struct OpeningRow(string Eco, string Name, int Moves);

    private sealed class Catalog
    {
        public Dictionary<string, OpeningRow> ByUci { get; } = new(StringComparer.Ordinal);
        public Dictionary<Hash128, OpeningRow> ByPosition { get; } = new();
        public int MaxMoves { get; set; }
    }

    private static readonly object Gate = new();
    private static Catalog? _catalog;
    private static readonly string[] CatalogFileNames = ["a.tsv", "b.tsv", "c.tsv", "d.tsv", "e.tsv"];

    public static OpeningMatch Classify(IReadOnlyList<string> sans, ChessModality? modality = null)
    {
        var catalog = EnsureLoaded();
        if (catalog is null || catalog.ByUci.Count == 0 || sans.Count == 0) return default;

        var m = modality ?? new ChessModality();
        var uci = new List<string>(sans.Count);
        var positions = new List<Hash128>(sans.Count + 1);
        var state = m.Initial();
        positions.Add(ChessCompose.PositionId(state.Board));
        foreach (var san in sans)
        {
            var mv = San.Resolve(state.Board, m.LegalActions(state), san);
            if (mv is null) break;
            uci.Add(mv.Value.ToUci());
            state = m.Apply(state, mv.Value);
            positions.Add(ChessCompose.PositionId(state.Board));
        }
        if (uci.Count == 0) return default;

        OpeningRow? byUci = null;
        int max = Math.Min(uci.Count, catalog.MaxMoves);
        var sb = new StringBuilder(max * 5);
        for (int len = 1; len <= max; len++)
        {
            if (len > 1) sb.Append(' ');
            sb.Append(uci[len - 1]);
            if (catalog.ByUci.TryGetValue(sb.ToString(), out var hit))
                byUci = hit;
        }

        OpeningRow? byPosition = null;
        foreach (var position in positions)
            if (catalog.ByPosition.TryGetValue(position, out var hit))
                byPosition = hit;

        OpeningRow? best = byUci;
        if (byPosition is { } transposed && (best is null || transposed.Moves > best.Value.Moves))
            best = transposed;
        return best is { } row ? new OpeningMatch(row.Name, row.Eco) : default;
    }

    internal static void Attest(
        SubstrateChangeBuilder b, Hash128 subject, OpeningMatch match, double weight, Hash128 sourceId)
    {
        if (match.Eco is { } eco)
            ChessGraph.AppendGameMeta(b, subject, ChessSeedManifest.HasEco, eco, weight, sourceId);
        if (match.Name is { } name)
            ChessGraph.AppendGameMeta(b, subject, ChessSeedManifest.OpeningName, name, weight, sourceId);
        AttestStructure(b, subject, match.Name, weight, sourceId);
    }

    internal static void AttestStructure(
        SubstrateChangeBuilder b, Hash128 subject, string? name, double weight, Hash128 sourceId)
    {
        var match = new OpeningMatch(name, null);
        if (match.Family is { } family)
            AttestFeature(b, subject, family, weight, sourceId);
        if (match.Variation is { } variation)
            AttestFeature(b, subject, variation, weight, sourceId);
        if (string.IsNullOrWhiteSpace(name)) return;
        foreach (var concept in ChessConcepts.All)
        {
            if (!concept.MatchesOpening(name)) continue;
            var member = concept.Member(ChessConcepts.Domain.Opening);
            b.AddEntity(concept.Id, EntityTier.Document, ChessVocabulary.AnalysisMarkerType);
            b.AddEntity(member, EntityTier.Document, ChessVocabulary.AnalysisMarkerType);
            b.AddAttestation(NativeAttestation.Categorical(
                member, "IS_EXAMPLE_OF", concept.Id, sourceId, null, weight));
            b.AddAttestation(NativeAttestation.Categorical(
                subject, "HAS_FEATURE", concept.Id, sourceId, member, weight));
        }
    }

    private static void AttestFeature(
        SubstrateChangeBuilder b, Hash128 subject, string text, double weight, Hash128 sourceId)
    {
        if (ContentEmitter.Emit(b, text, sourceId) is not { } id) return;
        b.AddAttestation(NativeAttestation.Categorical(
            subject, "HAS_FEATURE", id, sourceId, null, weight));
    }

    internal static void SetLinesForTest(IEnumerable<(string Eco, string Name, List<string> Sans)> lines)
    {
        lock (Gate) { _catalog = Build(lines.ToList()); }
    }

    internal static void ResetCache() { lock (Gate) { _catalog = null; } }

    private static Catalog EnsureLoaded()
    {
        var existing = _catalog;
        if (existing is not null) return existing;
        lock (Gate)
        {
            if (_catalog is not null) return _catalog;
            _catalog = Build(LoadLines(OpeningSeed.DefaultDir));
            return _catalog;
        }
    }

    private static Catalog Build(List<(string Eco, string Name, List<string> Sans)> lines)
    {
        var catalog = new Catalog();
        var modality = new ChessModality();
        foreach (var (eco, name, sans) in lines.OrderByDescending(static line => line.Sans.Count))
        {
            if (!TryReplay(modality, sans, out var uci, out var end)) continue;
            catalog.ByUci.TryAdd(string.Join(' ', uci), new OpeningRow(eco, name, uci.Count));
            catalog.ByPosition.TryAdd(end, new OpeningRow(eco, name, uci.Count));
            if (uci.Count > catalog.MaxMoves) catalog.MaxMoves = uci.Count;
        }
        return catalog;
    }

    private static bool TryReplay(
        ChessModality modality, List<string> sans, out List<string> uci, out Hash128 end)
    {
        uci = new List<string>(sans.Count);
        end = default;
        var state = modality.Initial();
        foreach (var san in sans)
        {
            var mv = San.Resolve(state.Board, modality.LegalActions(state), san);
            if (mv is null) return false;
            uci.Add(mv.Value.ToUci());
            state = modality.Apply(state, mv.Value);
        }
        if (uci.Count == 0) return false;
        end = ChessCompose.PositionId(state.Board);
        return true;
    }

    internal static List<(string Eco, string Name, List<string> Sans)> LoadLines(string path)
    {
        var list = new List<(string, string, List<string>)>();
        if (!Directory.Exists(path) && !File.Exists(path)) return list;
        foreach (var file in OpeningSeedFiles(path))
            foreach (var line in File.ReadLines(file))
            {
                if (ChessOpeningsDecomposer.ParseRow(line) is not { } row) continue;
                var sans = ChessOpeningsDecomposer.ExtractSans(row.Movetext);
                if (sans.Count == 0) continue;
                list.Add((row.Eco, row.Name, sans));
            }
        return list;
    }

    internal static IEnumerable<string> OpeningSeedFiles(string path)
    {
        if (File.Exists(path)) { yield return path; yield break; }
        if (!Directory.Exists(path)) yield break;
        var named = new List<string>();
        foreach (var name in CatalogFileNames)
        {
            named.AddRange(Directory.EnumerateFiles(path, name, SearchOption.AllDirectories)
                .OrderBy(static file => file, StringComparer.Ordinal));
        }
        if (named.Count > 0)
        {
            foreach (var file in named) yield return file;
            yield break;
        }
        foreach (var file in Directory.EnumerateFiles(path, "*.tsv", SearchOption.AllDirectories)
                     .OrderBy(static file => file, StringComparer.Ordinal))
            yield return file;
    }
}
