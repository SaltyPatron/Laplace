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
    None = 0,
    LeadingSpace = 1,
    ByteLevel = 2,
    Special = 4,
    Continuation = 8,
}

public sealed class LlamaTokenizerParser
{
    public sealed class TokenRecord
    {
        public required int TokenId { get; init; }
        public required string RawToken { get; init; }
        public required byte[] CanonicalBytes { get; init; }
        public required Hash128 EntityId { get; init; }
        public required byte Tier { get; init; }
        public required bool IsByteLevel { get; init; }
        public required TokenRole Role { get; init; }

        public required double ContentX { get; init; }
        public required double ContentY { get; init; }
        public required double ContentZ { get; init; }
        public required double ContentM { get; init; }
        public required bool HasContentCoord { get; init; }
    }

    public static IReadOnlyList<TokenRecord> Parse(string tokenizerJsonPath)
    {
        byte[] jsonBytes = File.ReadAllBytes(tokenizerJsonPath);
        return Parse(jsonBytes);
    }

    public static IReadOnlyList<TokenRecord> Parse(ReadOnlyMemory<byte> jsonBytes)
    {
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
                tier = EntityTier.Word;
            }
            else if (TryDecomposeRoot(canonical, out entityId, out tier, out cx, out cy, out cz, out cm))
            {
                hasContent = true;
            }
            else
            {
                (entityId, tier, cx, cy, cz, cm) = OpaqueByteRoot(canonical);
                hasContent = canonical.Length > 0;
            }

            records.Add(new TokenRecord
            {
                TokenId = tokenId,
                RawToken = raw,
                CanonicalBytes = canonical,
                EntityId = entityId,
                Tier = tier,
                IsByteLevel = role.HasFlag(TokenRole.ByteLevel),
                Role = role,
                ContentX = cx,
                ContentY = cy,
                ContentZ = cz,
                ContentM = cm,
                HasContentCoord = hasContent,
            });
        }

        records.Sort((a, b) => a.TokenId.CompareTo(b.TokenId));

        return records;
    }

    internal static bool TryDecomposeRoot(
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


            if (!CodepointPerfcache.IsLoaded) throw;
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
            if (!CodepointPerfcache.IsLoaded) throw;
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

    private static readonly Dictionary<char, byte> UnicodeToByte = BuildUnicodeToByte();
    private static Dictionary<char, byte> BuildUnicodeToByte()
    {
        var map = new char[256]; var self = new bool[256];
        void mark(int lo, int hi) { for (int b = lo; b <= hi; b++) { map[b] = (char)b; self[b] = true; } }
        mark('!', '~'); mark(0xA1, 0xAC); mark(0xAE, 0xFF);
        int k = 0; for (int b = 0; b < 256; b++) if (!self[b]) map[b] = (char)(256 + k++);
        var rev = new Dictionary<char, byte>(256);
        for (int b = 0; b < 256; b++) rev[map[b]] = (byte)b;
        return rev;
    }




    private static bool TryByteLevelDecode(string raw, out byte[] bytes, out bool leadingSpace)
    {
        bytes = Array.Empty<byte>(); leadingSpace = false;
        if (raw.Length == 0) return false;
        var buf = new byte[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            if (!UnicodeToByte.TryGetValue(raw[i], out byte b)) return false;
            buf[i] = b;
        }
        // GPT-2 byte-level `Ġ` is byte 0x20.  The space is CONTENT, not a
        // disposable tokenizer hint: stripping it aliases `Ġword` with `word`
        // and makes exact tokenizer reconstruction impossible.  Keep the decoded
        // bytes and retain the role as realization metadata for consumers.
        leadingSpace = buf[0] == (byte)' ';
        bytes = buf;
        return true;
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


        if (rawToken.Length > 0 && rawToken[0] != '▁' && !rawToken.StartsWith("##", StringComparison.Ordinal)
            && TryByteLevelDecode(rawToken, out byte[] blBytes, out bool blLead) && blBytes.Length > 0)
        {





            TokenRole br = blLead ? TokenRole.LeadingSpace : TokenRole.None;
            return (NormalizeNfc(blBytes), br);
        }

        TokenRole role = TokenRole.None;
        string surface = rawToken;

        if (surface.Length > 0 && (surface[0] == '▁' || surface[0] == 'Ġ'))
        {
            role |= TokenRole.LeadingSpace;
            // SentencePiece metaspace and GPT-2's byte-level space marker both
            // realize a LEADING U+0020.  Preserve it in canonical content so its
            // physicality trajectory is [space, token], never [token, space].
            surface = " " + surface.Substring(1);
        }
        else if (surface.Length > 2 && surface.StartsWith("##", StringComparison.Ordinal))
        {


            role |= TokenRole.Continuation;
            surface = surface.Substring(2);
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

    public static void StageVocabToken(SubstrateChangeBuilder b, TokenRecord rec, Hash128 sourceId)
    {
        Span<double> coord = stackalloc double[4];
        if (!rec.Role.HasFlag(TokenRole.Special)
            && TryBuildTreeRows(rec.CanonicalBytes, sourceId, out var treeEntities, out var treePhys))
        {
            foreach (var e in treeEntities) b.AddEntity(e);
            foreach (var p in treePhys) b.AddPhysicality(p);
        }
        else
        {
            // A special/control token is a governed tokenizer reference, not
            // ordinary textual content.  All other fallbacks are literal byte
            // sequences and therefore owe one exact Merkle physicality.
            Hash128 typeId = rec.Role.HasFlag(TokenRole.Special)
                ? EntityTypeRegistry.SourceReference
                : TextEntityBuilder.WordTypeId;
            b.AddEntity(rec.EntityId, rec.Tier, typeId, firstObservedBy: sourceId);
            if (rec.HasContentCoord)
            {
                if (rec.CanonicalBytes.Length > 1)
                {
                    var children = ByteConstituents(rec.CanonicalBytes);
                    var composed = NgramTrajectory.Compose(
                        children, rec.Tier, TextEntityBuilder.WordTypeId, sourceId,
                        IngestClock.NowUnixUs());
                    if (composed.Id != rec.EntityId)
                        throw new InvalidOperationException(
                            $"opaque tokenizer byte identity mismatch: parsed={rec.EntityId} composed={composed.Id}");
                    b.AddPhysicality(composed.Physicality);
                }
                else
                {
                    coord[0] = rec.ContentX; coord[1] = rec.ContentY;
                    coord[2] = rec.ContentZ; coord[3] = rec.ContentM;
                    Hash128 physId = PhysicalityId.Compute(rec.EntityId, PhysicalityType.Content);
                    b.AddPhysicality(new PhysicalityRow(
                        Id: physId, EntityId: rec.EntityId, SourceId: sourceId,
                        Type: PhysicalityType.Content,
                        CoordX: rec.ContentX, CoordY: rec.ContentY, CoordZ: rec.ContentZ, CoordM: rec.ContentM,
                        HilbertIndex: Hilbert128.Encode(coord),
                        TrajectoryXyzm: null, NConstituents: 0,
                        AlignmentResidual: null, SourceDim: null, ObservedAtUnixUs: 0));
                }
            }
        }
    }

    private static (Hash128 Id, byte Tier, double X, double Y, double Z, double M)
        OpaqueByteRoot(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
            return (Hash128.Blake3(bytes), EntityTier.Word,
                double.NaN, double.NaN, double.NaN, double.NaN);

        var children = ByteConstituents(bytes);
        if (children.Length == 1)
        {
            var c = children[0];
            return (c.Id, 0, c.X, c.Y, c.Z, c.M);
        }

        var ids = new Hash128[children.Length];
        var coords = new double[children.Length * 4];
        for (int i = 0; i < children.Length; i++)
        {
            var c = children[i];
            ids[i] = c.Id;
            coords[i * 4] = c.X; coords[i * 4 + 1] = c.Y;
            coords[i * 4 + 2] = c.Z; coords[i * 4 + 3] = c.M;
        }
        double[] center = Math4d.KarcherMean(coords);
        return (Hash128.Merkle(EntityTier.Word, ids), EntityTier.Word,
            center[0], center[1], center[2], center[3]);
    }

    private static NgramTrajectory.Constituent[] ByteConstituents(ReadOnlySpan<byte> bytes)
    {
        var children = new NgramTrajectory.Constituent[bytes.Length];
        var codepoints = CodepointPerfcache.Records;
        for (int i = 0; i < bytes.Length; i++)
        {
            byte value = bytes[i];
            Hash128 id = ByteAtoms.Id(value);
            if (value < ByteAtoms.First)
            {
                ref readonly var cp = ref codepoints[value];
                children[i] = new(id, cp.CoordX, cp.CoordY, cp.CoordZ, cp.CoordM);
            }
            else
            {
                ReadOnlySpan<double> c = ByteAtoms.Coord(value);
                children[i] = new(id, c[0], c[1], c[2], c[3]);
            }
        }
        return children;
    }

    public static async IAsyncEnumerable<TokenRecord> EnumerateVocabRecordsAsync(
        IReadOnlyList<TokenRecord> records,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (int i = 0; i < records.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return records[i];
        }
        await Task.CompletedTask;
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
            string? left, right;
            if (el.ValueKind == JsonValueKind.String)
            {

                string? pair = el.GetString();
                if (string.IsNullOrEmpty(pair)) continue;
                int sp = pair!.IndexOf(' ');
                if (sp <= 0 || sp + 1 >= pair.Length) continue;
                left = pair[..sp];
                right = pair[(sp + 1)..];
            }
            else if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() == 2)
            {

                left = el[0].GetString();
                right = el[1].GetString();
            }
            else continue;
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) continue;
            (byte[] l, _) = Canonicalize(left!);
            (byte[] r, _) = Canonicalize(right!);
            merges.Add((l, r));
        }
        return merges;
    }

    public readonly record struct MergeRecord(byte[] Left, byte[] Right, int Index, double ArenaScale);

    public static async IAsyncEnumerable<MergeRecord> EnumerateMergeRecordsAsync(
        List<(byte[] Left, byte[] Right)> merges,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (merges.Count == 0) yield break;
        double sumSq = 0;
        for (int i = 0; i < merges.Count; i++) sumSq += (double)i * i;
        double m = Math.Sqrt(sumSq / merges.Count);
        if (m <= 0) yield break;
        for (int i = 0; i < merges.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (l, r) = merges[i];
            yield return new MergeRecord(l, r, i, m);
        }
    }

    public static void StageMergeRecord(
        SubstrateChangeBuilder b, MergeRecord rec, Hash128 sourceId, Hash128 textTypeId)
    {
        Hash128 lid = ResolveMergeSide(b, rec.Left, sourceId, textTypeId);
        Hash128 rid = ResolveMergeSide(b, rec.Right, sourceId, textTypeId);
        b.AddAttestation(NativeAttestation.Categorical(
            lid, "MERGES_WITH", rid, sourceId, SourceTrust.AiModelProbe,
            magnitude: rec.ArenaScale - rec.Index, arenaScale: rec.ArenaScale));
    }

    public readonly record struct TokenMapsToRecord(Hash128 TokenizerEntityId, TokenRecord Token);

    public static async IAsyncEnumerable<TokenMapsToRecord> EnumerateMapsToRecordsAsync(
        IReadOnlyList<TokenRecord> records,
        Hash128 tokenizerEntityId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (int i = 0; i < records.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return new TokenMapsToRecord(tokenizerEntityId, records[i]);
        }
        await Task.CompletedTask;
    }

    public static void StageMapsToRecord(
        SubstrateChangeBuilder b, TokenMapsToRecord rec, Hash128 sourceId)
    {
        b.AddAttestation(NativeAttestation.Categorical(
            rec.TokenizerEntityId, "TOKEN_MAPS_TO", rec.Token.EntityId, sourceId,
            SourceTrust.AiModelProbe));
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
        b.AddEntity(id, EntityTier.Word, TextEntityBuilder.WordTypeId, firstObservedBy: sourceId);
        // Same fallback coordinate rule as Parse(): a lone high byte still has a real,
        // deterministic ByteAtoms placement -- give it the matching physicality rather than
        // leaving this Word-tier content entity geometry-less.
        if (canonical.Length == 1 && canonical[0] >= ByteAtoms.First)
        {
            var bc = ByteAtoms.Coord(canonical[0]);
            Span<double> coord = stackalloc double[4] { bc[0], bc[1], bc[2], bc[3] };
            Hash128 physId = PhysicalityId.Compute(id, PhysicalityType.Content);
            b.AddPhysicality(new PhysicalityRow(
                Id: physId, EntityId: id, SourceId: sourceId,
                Type: PhysicalityType.Content,
                CoordX: bc[0], CoordY: bc[1], CoordZ: bc[2], CoordM: bc[3],
                HilbertIndex: Hilbert128.Encode(coord),
                TrajectoryXyzm: null, NConstituents: 0,
                AlignmentResidual: null, SourceDim: null, ObservedAtUnixUs: 0));
        }
        return id;
    }

}
