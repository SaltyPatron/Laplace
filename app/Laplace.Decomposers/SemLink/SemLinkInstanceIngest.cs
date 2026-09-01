using System.Runtime.CompilerServices;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.SemLink;

/// <summary>
/// The SemLink distribution is not only its three mapping files. <c>instances/semlink-2</c>
/// contains the manually aligned predicate occurrences: OntoNotes source position, lemma,
/// VN class, FN frame, PB roleset/sense group, and argument-span role alignments. Treating
/// the JSON maps as the whole source discarded 148,653 of the distribution's 154k records.
/// </summary>
internal static class SemLinkInstanceIngest
{
    internal const string FileName = "semlink-2";

    private static readonly Hash128 AppearsIn = SemLinkSource.AppearsInTypeId;
    private static readonly Hash128 HasRole = SemLinkSource.HasRoleTypeId;
    private static readonly Hash128 HasSense = SemLinkSource.HasSenseTypeId;
    private static readonly Hash128 MemberOfVerbNet = SemLinkSource.MemberOfVerbNetClassTypeId;
    private static readonly Hash128 EvokesFrame = SemLinkSource.EvokesFrameTypeId;
    private static readonly Hash128 RoleCorrespondsTo = SemLinkSource.RoleCorrespondsToTypeId;

    internal readonly record struct Dependency(
        string Span, string? PropBankRole, string? VerbNetRole, string? FrameNetRole);

    internal readonly record struct Record(
        string SourceFile,
        int SentenceOrdinal,
        int TokenOrdinal,
        string Lemma,
        string? VerbNetClass,
        string? FrameNetFrame,
        string? PropBankRoleset,
        string? OntoNotesSenseGroup,
        Dependency[] Dependencies);

    internal static string? ResolvePath(string instancesDir)
    {
        string direct = Path.Combine(instancesDir, FileName);
        return File.Exists(direct) ? direct : null;
    }

    internal static Task<long?> EstimateUnitCountAsync(string path, CancellationToken ct)
    {
        long lines = EtlInventory.EstimateNewlineCount(path, ct);
        return Task.FromResult<long?>(lines > 0 ? lines : null);
    }

    internal static async IAsyncEnumerable<Record> EnumerateRecordsAsync(
        string path, long maxInputUnits,
        [EnumeratorCancellation] CancellationToken ct)
    {
        long admitted = 0;
        await foreach (var line in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length == 0) continue;
            if (!TryParse(Encoding.UTF8.GetString(line.Span), out var record)) continue;
            if (maxInputUnits > 0 && admitted >= maxInputUnits) yield break;
            admitted++;
            yield return record;
        }
    }

    internal static bool TryParse(string line, out Record record)
    {
        record = default;
        string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 8
            || !int.TryParse(fields[1], out int sentence)
            || !int.TryParse(fields[2], out int token))
            return false;

        // New SemLink 2 normally omits the historical "gold" column. The official
        // annotation.py reader inserts it before indexing; accept both serializations.
        int value = fields[3].Equals("gold", StringComparison.OrdinalIgnoreCase) ? 4 : 3;
        if (fields.Length <= value + 4) return false;
        string lemma = fields[value].EndsWith("-v", StringComparison.Ordinal)
            ? fields[value][..^2]
            : fields[value];
        if (lemma.Length == 0) return false;

        string? vnClass = Value(fields[value + 1]);
        if (vnClass is not null)
            vnClass = SourceEntityIdConventions.NumericVerbNetClassId(vnClass);
        string? frame = Value(fields[value + 2], "NF", "IN");
        string? roleset = Value(fields[value + 3]);
        string? onGroup = Value(fields[value + 4]);
        var dependencies = new List<Dependency>(Math.Max(0, fields.Length - value - 5));
        for (int i = value + 5; i < fields.Length; i++)
            if (TryParseDependency(fields[i], out var dependency))
                dependencies.Add(dependency);

        record = new Record(
            Path.GetFileName(fields[0]), sentence, token, lemma,
            vnClass, frame, roleset, onGroup, dependencies.ToArray());
        return true;
    }

    private static string? Value(string value, params string[] extraMissing)
    {
        string normalized = value.Trim();
        if (normalized.Length == 0
            || normalized.Equals("None", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("null", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("-----", StringComparison.Ordinal))
            return null;
        foreach (string missing in extraMissing)
            if (normalized.Equals(missing, StringComparison.OrdinalIgnoreCase)) return null;
        return normalized.Normalize(NormalizationForm.FormC);
    }

    private static bool TryParseDependency(string raw, out Dependency dependency)
    {
        dependency = default;
        int roleAt = raw.IndexOf('-');
        if (roleAt <= 0 || roleAt == raw.Length - 1) return false;
        string span = raw[..roleAt];
        string rest = raw[(roleAt + 1)..];
        int equals = rest.IndexOf('=');
        string pbRole = equals < 0 ? rest : rest[..equals];
        if (pbRole.Equals("rel", StringComparison.OrdinalIgnoreCase)) pbRole = string.Empty;

        string? vnRole = null;
        string? fnRole = null;
        if (equals >= 0 && equals + 1 < rest.Length)
        {
            string[] mapped = rest[(equals + 1)..].Split(';', 2);
            vnRole = Value(mapped[0]);
            if (mapped.Length == 2) fnRole = Value(mapped[1]);
        }
        dependency = new Dependency(
            span, Value(pbRole), vnRole, fnRole);
        return true;
    }

    internal static void Compose(
        Record record, SubstrateChangeBuilder builder, Hash128? precomposedLemma = null)
    {
        Hash128 source = SemLinkDecomposer.Source;
        Hash128 occurrence = OccurrenceId(record);
        Hash128 sourceFile = SourceFileId(record.SourceFile);
        builder.AddEntity(occurrence, EntityTier.Document, EntityTypeRegistry.SourceReference, source);
        builder.AddEntity(sourceFile, EntityTier.Document, EntityTypeRegistry.SourceFile, source);

        Add(builder, occurrence, AppearsIn, sourceFile, occurrence);
        Hash128? lemma = precomposedLemma ?? ContentEmitter.Emit(builder, record.Lemma, source);
        if (lemma is { } lemmaId)
            Add(builder, lemmaId, AppearsIn, occurrence, occurrence);

        Hash128? vnClass = EmitCategory(
            builder, record.VerbNetClass, EntityTypeRegistry.VerbNetClass);
        Hash128? frame = EmitCategory(
            builder, record.FrameNetFrame, EntityTypeRegistry.FrameNetFrame);
        Hash128? roleset = EmitCategory(
            builder, record.PropBankRoleset, EntityTypeRegistry.PropBankRoleset);
        Hash128? onSense = record.OntoNotesSenseGroup is { } group
            ? EmitReference(builder, $"ontonotes-sense\0{record.Lemma}\0{group}")
            : null;

        if (lemma is { } predicate)
        {
            Add(builder, predicate, MemberOfVerbNet, vnClass, occurrence);
            Add(builder, predicate, EvokesFrame, frame, occurrence);
            Add(builder, predicate, HasSense, roleset, occurrence);
            Add(builder, predicate, HasSense, onSense, occurrence);
        }

        for (int i = 0; i < record.Dependencies.Length; i++)
        {
            Dependency dep = record.Dependencies[i];
            Hash128 argument = ArgumentOccurrenceId(occurrence, i, dep.Span);
            builder.AddEntity(
                argument, EntityTier.Document, EntityTypeRegistry.SourceReference, source);
            Add(builder, argument, AppearsIn, occurrence, occurrence);

            Hash128? pbRole = roleset is { } pbParent && dep.PropBankRole is { } pb
                ? RoleAnchor.Declare(
                    builder, RoleIdentityKind.PropBank, pbParent, NormalizePropBankRole(pb),
                    EntityTypeRegistry.PropBankRole, source)
                : null;
            Hash128? vnRole = vnClass is { } vnParent && dep.VerbNetRole is { } vn
                ? RoleAnchor.Declare(
                    builder, RoleIdentityKind.VerbNet, vnParent, vn,
                    EntityTypeRegistry.VerbNetRole, source)
                : null;
            Hash128? fnRole = frame is { } fnParent && dep.FrameNetRole is { } fn
                ? RoleAnchor.Declare(
                    builder, RoleIdentityKind.FrameNet, fnParent, fn,
                    EntityTypeRegistry.FrameNetFe, source)
                : null;

            Add(builder, argument, HasRole, pbRole, occurrence);
            Add(builder, argument, HasRole, vnRole, occurrence);
            Add(builder, argument, HasRole, fnRole, occurrence);
            AddPair(builder, pbRole, vnRole, occurrence);
            AddPair(builder, pbRole, fnRole, occurrence);
            AddPair(builder, vnRole, fnRole, occurrence);
        }
    }

    private static Hash128 OccurrenceId(Record record) => Hash128.OfCanonical(
        $"semlink/annotation-occurrence/{Hex(record.SourceFile)}/"
        + $"{record.SentenceOrdinal}/{record.TokenOrdinal}/{Hex(record.Lemma)}/v1");

    private static Hash128 SourceFileId(string file) =>
        Hash128.OfCanonical($"semlink/source-file/{Hex(file)}/v1");

    private static Hash128 ArgumentOccurrenceId(Hash128 occurrence, int ordinal, string span) =>
        Hash128.OfCanonical($"semlink/argument-occurrence/{occurrence}/{ordinal}/{Hex(span)}/v1");

    private static string Hex(string value) =>
        Convert.ToHexString(Encoding.UTF8.GetBytes(value));

    private static Hash128? EmitCategory(
        SubstrateChangeBuilder builder, string? value, Hash128 type) =>
        value is null
            ? null
            : AnchorAdmission.Emit(
                builder, value, type, SemLinkDecomposer.Source, TC.AcademicCurated);

    private static Hash128? EmitReference(SubstrateChangeBuilder builder, string key)
    {
        Hash128 id = Hash128.OfCanonical($"semlink/reference/{Hex(key)}/v1");
        builder.AddEntity(
            id, EntityTier.Word, EntityTypeRegistry.SourceReference, SemLinkDecomposer.Source);
        return id;
    }

    private static string NormalizePropBankRole(string role)
    {
        string normalized = role.Trim().ToUpperInvariant();
        return normalized.All(char.IsDigit) ? $"ARG{normalized}" : normalized;
    }

    private static void AddPair(
        SubstrateChangeBuilder builder, Hash128? left, Hash128? right, Hash128 context)
    {
        if (left is null || right is null) return;
        Add(builder, left.Value, RoleCorrespondsTo, right, context);
    }

    private static void Add(
        SubstrateChangeBuilder builder,
        Hash128 subject,
        Hash128 relation,
        Hash128? obj,
        Hash128 context)
    {
        if (obj is null) return;
        builder.AddAttestation(NativeAttestation.CategoricalResolved(
            subject, relation, obj.Value, SemLinkDecomposer.Source,
            context, TC.AcademicCurated));
    }

    internal sealed class RecordHandler : IIngestRecordHandler<Record>
    {
        public IIngestDeferredUnit CreateDeferredUnit(Record record) => new Unit(record);

        public void WalkWitness(
            Record record, Hash128 root,
            SubstrateChangeBuilder builder, IIngestDeferredUnit unit) { }

        private sealed class Unit : IIngestDeferredUnit
        {
            private readonly Record _record;
            private TierTree? _lemmaTree;

            public Unit(Record record)
            {
                _record = record;
                // This is the only content-tree work in the instance row. Constructing it
                // here lets the shared compose workers fan out; the ordered builder drain
                // below only emits the prebuilt tree plus governed reference testimony.
                _lemmaTree = ContentTierSpine.BuildTree(Encoding.UTF8.GetBytes(record.Lemma));
            }

            public TierTree? TreeForBatchProbe => _lemmaTree;

            public Task<byte[]?> ProbeDescentAsync(
                ISubstrateReader reader, CancellationToken ct) =>
                _lemmaTree is null
                    ? Task.FromResult<byte[]?>(null)
                    : ContentTierSpine.ExistenceEmitBitmapAsync(_lemmaTree, reader, ct);

            public Hash128 DrainInto(
                SubstrateChangeBuilder builder,
                double witnessWeight,
                byte[]? descentBitmap)
            {
                Hash128? lemma = null;
                if (_lemmaTree is not null
                    && ContentTierSpine.EmitTree(
                        builder, _lemmaTree, SemLinkDecomposer.Source,
                        descentBitmap ?? ReadOnlySpan<byte>.Empty, out Hash128 root))
                    lemma = root;
                Compose(_record, builder, lemma);
                return OccurrenceId(_record);
            }

            public void Dispose()
            {
                _lemmaTree?.Dispose();
                _lemmaTree = null;
            }
        }
    }
}

internal sealed class SemLinkInstancePhase : ComposeDecomposerPhase<SemLinkInstanceIngest.Record>
{
    private readonly string _path;

    public SemLinkInstancePhase(string path) => _path = path;

    protected override string PhaseLabel => "semlink/annotated-instances";
    public override Hash128 SourceId => SemLinkDecomposer.Source;
    public override string SourceName => "SemLinkDecomposer";
    public override int LayerOrder => 3;
    public override Hash128 TrustClassId => SemLinkDecomposer.TrustClass;
    protected override double SourceTrust => TC.AcademicCurated;

    public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        Task.CompletedTask;

    public override Task<long?> EstimateUnitCountAsync(
        IDecomposerContext context, CancellationToken ct = default) =>
        SemLinkInstanceIngest.EstimateUnitCountAsync(_path, ct);

    protected override void Compose(
        SemLinkInstanceIngest.Record record, SubstrateChangeBuilder builder) =>
        SemLinkInstanceIngest.Compose(record, builder);

    protected override IIngestRecordHandler<SemLinkInstanceIngest.Record> CreateHandler() =>
        new SemLinkInstanceIngest.RecordHandler();

    protected override IAsyncEnumerable<SemLinkInstanceIngest.Record> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options, CancellationToken ct) =>
        SemLinkInstanceIngest.EnumerateRecordsAsync(_path, options.MaxInputUnits, ct);

    protected override IngestBatchConfig BuildPipelineConfig(
        IDecomposerContext context, DecomposerOptions options) =>
        IngestPipelineDefaults.Compose(
            SourceId, BatchLabelPrefix, options, context.Reader,
            SemLinkSource.Profile, attestationCapacity: null);
}
