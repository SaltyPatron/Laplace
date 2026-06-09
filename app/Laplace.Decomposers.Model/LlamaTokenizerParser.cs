using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

[Flags]
public enum TokenRole : byte
{
    None         = 0,
    LeadingSpace = 1,
    ByteLevel    = 2,
    Special      = 4,
}

public sealed class LlamaTokenizerParser
{
    public sealed class TokenRecord
    {
        public required int      TokenId        { get; init; }
        public required string   RawToken       { get; init; }
        public required byte[]   CanonicalBytes { get; init; }
        public required Hash128  EntityId       { get; init; }
        public required byte     Tier           { get; init; }
        public required bool     IsByteLevel    { get; init; }
        public required TokenRole Role          { get; init; }

        public required double   ContentX       { get; init; }
        public required double   ContentY       { get; init; }
        public required double   ContentZ       { get; init; }
        public required double   ContentM       { get; init; }
        public required bool     HasContentCoord { get; init; }
    }

    public static IReadOnlyList<TokenRecord> Parse(string tokenizerJsonPath)
    {
        byte[] jsonBytes = File.ReadAllBytes(tokenizerJsonPath);
        using var doc = JsonDocument.Parse(jsonBytes);

        JsonElement vocab;
        if (!doc.RootElement.TryGetProperty("model", out var model) ||
            !model.TryGetProperty("vocab", out vocab))
        {
            if (!doc.RootElement.TryGetProperty("vocab", out vocab))
                throw new InvalidDataException("tokenizer.json: cannot find model.vocab or vocab");
        }

        var specialIds = new HashSet<int>();
        if (doc.RootElement.TryGetProperty("added_tokens", out var added) &&
            added.ValueKind == JsonValueKind.Array)
        {
            foreach (var at in added.EnumerateArray())
                if (at.TryGetProperty("special", out var sp) && sp.ValueKind == JsonValueKind.True &&
                    at.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out int sid))
                    specialIds.Add(sid);
        }

        var records = new List<TokenRecord>(vocab.GetPropertyCount() + 16);

        foreach (var entry in vocab.EnumerateObject())
        {
            string raw = entry.Name;
            int tokenId = entry.Value.GetInt32();

            (byte[] canonical, TokenRole role) = Canonicalize(raw);
            if (specialIds.Contains(tokenId)) role |= TokenRole.Special;

            Hash128 entityId;
            byte tier;
            double cx = double.NaN, cy = double.NaN, cz = double.NaN, cm = double.NaN;
            bool hasContent = false;
            if (role.HasFlag(TokenRole.Special))
            {
                entityId = Hash128.OfCanonical($"substrate/token/special/{raw}/v1");
                tier = 0;
            }
            else if (TryDecomposeRoot(canonical, out entityId, out tier, out cx, out cy, out cz, out cm))
            {
                hasContent = true;
            }
            else
            {
                entityId = Hash128.Blake3(canonical);
                tier = EntityTier.Word;
                if (canonical.Length == 1 && canonical[0] >= ByteAtoms.First)
                {
                    var bc = ByteAtoms.Coord(canonical[0]);
                    cx = bc[0]; cy = bc[1]; cz = bc[2]; cm = bc[3];
                    hasContent = true;
                }
            }

            records.Add(new TokenRecord
            {
                TokenId        = tokenId,
                RawToken       = raw,
                CanonicalBytes = canonical,
                EntityId       = entityId,
                Tier           = tier,
                IsByteLevel    = role.HasFlag(TokenRole.ByteLevel),
                Role           = role,
                ContentX       = cx,
                ContentY       = cy,
                ContentZ       = cz,
                ContentM       = cm,
                HasContentCoord = hasContent,
            });
        }

        records.Sort((a, b) => a.TokenId.CompareTo(b.TokenId));

        return records;
    }

    private static bool TryDecomposeRoot(
        byte[] canonical,
        out Hash128 entityId, out byte tier,
        out double cx, out double cy, out double cz, out double cm)
    {
        try
        {
            using var tree = TextDecomposer.Run(canonical);
            unsafe { HashComposer.Run(tree, &PerfcacheResolver); }

            int nc = tree.NodeCount;
            if (nc == 0)
            {
                entityId = default; tier = 0;
                cx = cy = cz = cm = double.NaN;
                return false;
            }
            var rootNode = tree.GetNode(tree.NaturalUnitIndex());
            entityId = rootNode.Id;
            tier = rootNode.Tier;
            unsafe { cx = rootNode.Coord[0]; cy = rootNode.Coord[1]; cz = rootNode.Coord[2]; cm = rootNode.Coord[3]; }
            return true;
        }
        catch (InvalidOperationException)
        {
            entityId = default;
            tier = 0;
            cx = cy = cz = cm = double.NaN;
            return false;
        }
    }

    private static bool TryBuildTreeRows(
        byte[] canonical, Hash128 sourceId,
        out ImmutableArray<EntityRow> entities,
        out ImmutableArray<PhysicalityRow> physicalities)
    {
        try
        {
            using var tree = TextDecomposer.Run(canonical);
            if (tree.NodeCount == 0)
            {
                entities = ImmutableArray<EntityRow>.Empty;
                physicalities = ImmutableArray<PhysicalityRow>.Empty;
                return false;
            }
            unsafe { HashComposer.Run(tree, &PerfcacheResolver); }
            var (es, ps) = new TextEntityBuilder(tree, sourceId).Build();
            entities = es;
            physicalities = ps;
            return true;
        }
        catch (InvalidOperationException)
        {
            entities = ImmutableArray<EntityRow>.Empty;
            physicalities = ImmutableArray<PhysicalityRow>.Empty;
            return false;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int PerfcacheResolver(
        uint atom, IntPtr userData,
        Hash128* outId, double* outCoord, Hilbert128* outHb)
    {
        var recs = CodepointPerfcache.Records;
        if (atom >= (uint)recs.Length) return -1;
        ref readonly var r = ref recs[(int)atom];
        *outId = r.Hash;
        outCoord[0] = r.CoordX; outCoord[1] = r.CoordY;
        outCoord[2] = r.CoordZ; outCoord[3] = r.CoordM;
        *outHb = r.Hilbert;
        return 0;
    }

    public static (byte[] canonical, TokenRole role) Canonicalize(string rawToken)
    {
        if (rawToken.Length == 6 && rawToken.StartsWith("<0x", StringComparison.Ordinal)
            && rawToken.EndsWith('>'))
        {
            string hex = rawToken.Substring(3, 2);
            if (byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out byte b))
                return ([b], TokenRole.ByteLevel);
        }

        TokenRole role = TokenRole.None;
        string surface = rawToken;

        if (surface.Length > 0 && (surface[0] == '▁' || surface[0] == 'Ġ'))
        {
            role |= TokenRole.LeadingSpace;
            surface = surface.Substring(1);
        }

        surface = surface.Replace('Ċ', '\n');

        if (surface.Length == 0)
            surface = " ";

        var raw = Encoding.UTF8.GetBytes(surface);
        return (NormalizeNfc(raw), role);
    }

    private static unsafe byte[] NormalizeNfc(byte[] utf8)
    {
        if (utf8.Length == 0) return utf8;
        byte* outPtr = null;
        nuint outLen = 0;
        fixed (byte* p = utf8)
        {
            int rc = NativeInterop.NormalizeNfcUtf8(p, (nuint)utf8.Length, &outPtr, &outLen);
            if (rc != 0) return utf8;
        }
        try
        {
            return new ReadOnlySpan<byte>(outPtr, (int)outLen).ToArray();
        }
        finally
        {
            if (outPtr != null) NativeMemory.Free(outPtr);
        }
    }

    public static IEnumerable<SubstrateChange> BuildBatches(
        IReadOnlyList<TokenRecord> records,
        Hash128 sourceId,
        Hash128 textTypeId,
        int batchSize = 512)
    {
        int total = records.Count;
        for (int start = 0; start < total; start += batchSize)
        {
            int end = Math.Min(start + batchSize, total);
            int n   = end - start;

            var b = new SubstrateChangeBuilder(
                sourceId,
                $"tokenizer/vocab/{start}..{end - 1}",
                entityCapacity: n * 5,
                physicalityCapacity: n * 5,
                attestationCapacity: 0);

            for (int i = start; i < end; i++)
            {
                var rec = records[i];

                if (!rec.Role.HasFlag(TokenRole.Special)
                    && TryBuildTreeRows(rec.CanonicalBytes, sourceId, out var treeEntities, out var treePhys))
                {
                    foreach (var e in treeEntities) b.AddEntity(e);
                    foreach (var p in treePhys)    b.AddPhysicality(p);
                }
                else
                {
                    b.AddEntity(rec.EntityId, EntityTier.Vocabulary, TextEntityBuilder.WordTypeId,
                        firstObservedBy: sourceId);
                }

            }

            yield return b.Build();
        }
    }

    public static List<(byte[] Left, byte[] Right)> ParseMerges(string tokenizerJsonPath)
    {
        var merges = new List<(byte[], byte[])>();
        byte[] jsonBytes = File.ReadAllBytes(tokenizerJsonPath);
        using var doc = JsonDocument.Parse(jsonBytes);
        if (!doc.RootElement.TryGetProperty("model", out var model) ||
            !model.TryGetProperty("merges", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return merges;
        foreach (var el in arr.EnumerateArray())
        {
            string? pair = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
            if (string.IsNullOrEmpty(pair)) continue;
            int sp = pair!.IndexOf(' ');
            if (sp <= 0 || sp + 1 >= pair.Length) continue;
            (byte[] l, _) = Canonicalize(pair[..sp]);
            (byte[] r, _) = Canonicalize(pair[(sp + 1)..]);
            merges.Add((l, r));
        }
        return merges;
    }

    public static IEnumerable<SubstrateChange> BuildMergesBatches(
        List<(byte[] Left, byte[] Right)> merges,
        Hash128 sourceId,
        Hash128 textTypeId,
        int batchSize = 8192)
    {
        if (merges.Count == 0) yield break;
        double sumSq = 0;
        for (int i = 0; i < merges.Count; i++) sumSq += (double)i * i;
        double m = Math.Sqrt(sumSq / merges.Count);
        if (m <= 0) yield break;

        for (int start = 0; start < merges.Count; start += batchSize)
        {
            int end = Math.Min(start + batchSize, merges.Count);
            var b = new SubstrateChangeBuilder(
                sourceId, $"tokenizer/merges/{start}..{end - 1}",
                entityCapacity: (end - start) * 6,
                physicalityCapacity: (end - start) * 6,
                attestationCapacity: end - start);
            for (int i = start; i < end; i++)
            {
                var (l, r) = merges[i];
                Hash128 lid = ResolveMergeSide(b, l, sourceId, textTypeId);
                Hash128 rid = ResolveMergeSide(b, r, sourceId, textTypeId);
                b.AddAttestation(Laplace.Decomposers.Abstractions.RelationTypeRegistry.AttestWeighted(
                    lid, "MERGES_WITH", rid, sourceId,
                    Laplace.Decomposers.Abstractions.SourceTrust.AiModelProbe,
                    magnitude: m - i, arenaScale: m));
            }
            yield return b.Build();
        }
    }

    private static Hash128 ResolveMergeSide(
        SubstrateChangeBuilder b, byte[] canonical, Hash128 sourceId, Hash128 textTypeId)
    {
        if (TryBuildTreeRows(canonical, sourceId, out var entities, out var physicalities))
        {
            foreach (var e in entities) b.AddEntity(e);
            foreach (var ph in physicalities) b.AddPhysicality(ph);
            if (TryDecomposeRoot(canonical, out var rootId, out _, out _, out _, out _, out _))
                return rootId;
        }
        var id = Hash128.Blake3(canonical);
        b.AddEntity(id, EntityTier.Vocabulary, TextEntityBuilder.WordTypeId, firstObservedBy: sourceId);
        return id;
    }

    public static IEnumerable<SubstrateChange> BuildTokenMapsToCategorical(
        IReadOnlyList<TokenRecord> records,
        Hash128 sourceId,
        Hash128 tokenizerEntityId,
        int batchSize = 8192)
    {
        int total = records.Count;
        for (int start = 0; start < total; start += batchSize)
        {
            int end = Math.Min(start + batchSize, total);
            var b = new SubstrateChangeBuilder(
                sourceId, $"tokenizer/maps-to/{start}..{end - 1}",
                entityCapacity: 0, physicalityCapacity: 0,
                attestationCapacity: end - start);
            for (int i = start; i < end; i++)
            {
                b.AddAttestation(Laplace.Decomposers.Abstractions.RelationTypeRegistry.Attest(
                    tokenizerEntityId, "TOKEN_MAPS_TO", records[i].EntityId, sourceId,
                    Laplace.Decomposers.Abstractions.SourceTrust.AiModelProbe));
            }
            yield return b.Build();
        }
    }
}
