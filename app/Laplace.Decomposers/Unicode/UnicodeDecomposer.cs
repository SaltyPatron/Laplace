using System.Globalization;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Unicode;

/// <summary>
/// Unicode/UCD ingestion at physical-artifact grain. No source is preloaded during
/// Initialize: every selected file is claimed exactly once by the shared multi-phase
/// artifact executor and streamed through parse → compose → shared apply. DUCET owns
/// tier-0 UCA geometry; UCD XML is independently parsed/validated; property tables own
/// only the claims they physically state.
/// </summary>
public sealed class UnicodeDecomposer
    : DecomposerMultiPhase<UnicodeSource, FullScope>, IIngestInventoryProvider
{
    public static readonly Hash128 Source = UnicodeSource.SourceId;
    public static readonly Hash128 TrustClass = UnicodeSource.TrustClass;
    public static readonly Hash128 CodepointType = EntityTypeRegistry.Codepoint;

    private readonly string? _ucdxmlZip;
    private readonly string? _ducet;
    private readonly ConcurrentStringSet _canonicalNames = new(StringComparer.Ordinal);

    public UnicodeDecomposer(string? ucdxmlZip = null, string? ducet = null)
    {
        _ucdxmlZip = ucdxmlZip;
        _ducet = ducet;
    }

    public override int LayerOrder => 0;

    protected override Task OnInitializedAsync(IDecomposerContext context, CancellationToken ct)
    {
        // Source vocabulary bootstrap is metadata-only. Physical source files are not
        // opened until their claimed RunPhaseAsync worker begins.
        return Task.CompletedTask;
    }

    public override IReadOnlyCollection<string> CanonicalNamesForReadback
    {
        get
        {
            var names = new HashSet<string>(_canonicalNames, StringComparer.Ordinal)
            {
                "Byte",
                "substrate/encoding/ISO-8859-1/v1",
                "substrate/encoding/windows-1252/v1",
                "substrate/utf8/continuation/v1",
                "substrate/utf8/lead2/v1",
                "substrate/utf8/lead3/v1",
                "substrate/utf8/lead4/v1",
                "substrate/utf8/invalid/v1",
                "ordinal/0/v1",
                "ordinal/1/v1",
            };
            return names.ToArray();
        }
    }

    protected override async IAsyncEnumerable<SubstrateChange> RunIngestAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        int batch = IngestPipelineDefaults.ResolveBatch(IngestSourceProfile.Unicode, options);
        IReadOnlyList<ArtifactJob> jobs = ResolveArtifactJobs(context);

        foreach (ArtifactJob job in jobs)
        {
            await foreach (SubstrateChange change in RunArtifactAsync(
                job, context, options, batch, ct))
            {
                yield return change;
            }
        }
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        CancellationToken ct = default)
    {
        IReadOnlyList<ArtifactJob> jobs = ResolveArtifactJobs(context);
        if (jobs.Count == 0) return Task.FromResult<IngestInventory?>(null);

        // Do not open the artifacts just to count them: the physical source law requires
        // one claimed open. DUCET has an exact semantic denominator by contract; the
        // remaining source-row denominator is raised truthfully from observed parser units
        // as those files execute.
        long declared = jobs.Any(static job => job.Kind == ArtifactKind.Ducet)
            ? UnicodeSeed.CodepointCount
            : 0L;
        if (options.MaxInputUnits > 0) declared = Math.Min(declared, options.MaxInputUnits);

        var files = jobs.Select(job => new IngestFileSpec(
            job.Label,
            job.Path,
            job.Kind == ArtifactKind.Ducet ? UnicodeSeed.CodepointCount : 0L)).ToArray();
        return Task.FromResult<IngestInventory?>(
            new IngestInventory("source-rows", declared, files, TracksFileCompletion: true));
    }

    public override async Task<long?> EstimateUnitCountAsync(
        IDecomposerContext context,
        CancellationToken ct = default)
    {
        IngestInventory? inventory = await DescribeInputAsync(
            context, DecomposerOptions.Default, ct).ConfigureAwait(false);
        return inventory?.TotalInputUnits;
    }

    internal static Hash128 CodepointId(uint codepoint)
    {
        if (codepoint > 0x10FFFFu)
            throw new ArgumentOutOfRangeException(nameof(codepoint));

        Span<byte> encoded = stackalloc byte[4];
        int length;
        if (codepoint < 0x80u)
        {
            encoded[0] = (byte)codepoint;
            length = 1;
        }
        else if (codepoint < 0x800u)
        {
            encoded[0] = (byte)(0xC0u | (codepoint >> 6));
            encoded[1] = (byte)(0x80u | (codepoint & 0x3Fu));
            length = 2;
        }
        else if (codepoint < 0x10000u)
        {
            // The native tier-0 universe includes surrogate positions as positions, so
            // preserve the same byte preimage rather than applying scalar-value policy.
            encoded[0] = (byte)(0xE0u | (codepoint >> 12));
            encoded[1] = (byte)(0x80u | ((codepoint >> 6) & 0x3Fu));
            encoded[2] = (byte)(0x80u | (codepoint & 0x3Fu));
            length = 3;
        }
        else
        {
            encoded[0] = (byte)(0xF0u | (codepoint >> 18));
            encoded[1] = (byte)(0x80u | ((codepoint >> 12) & 0x3Fu));
            encoded[2] = (byte)(0x80u | ((codepoint >> 6) & 0x3Fu));
            encoded[3] = (byte)(0x80u | (codepoint & 0x3Fu));
            length = 4;
        }
        return Hash128.Blake3(encoded[..length]);
    }

    internal static Hash128 StageCodepointTarget(uint targetCp) => CodepointId(targetCp);

    private Hash128 ClassifierEntity(
        SubstrateChangeBuilder builder,
        string canonicalPrefix,
        string value)
    {
        string canonical = $"{canonicalPrefix}/{value}/v1";
        _canonicalNames.Add(canonical);
        Hash128 id = Hash128.OfCanonical(canonical);
        builder.AddEntity(id, EntityTier.Word, EntityTypeRegistry.UcdClassifier, Source);
        return id;
    }

    private static void EnsureOrdinalContexts(SubstrateChangeBuilder builder)
    {
        builder.AddEntity(new EntityRow(
            UcdProperties.OrdinalCtx0, EntityTier.Word,
            EntityTypeRegistry.OrdinalContext, Source));
        builder.AddEntity(new EntityRow(
            UcdProperties.OrdinalCtx1, EntityTier.Word,
            EntityTypeRegistry.OrdinalContext, Source));
    }

    private async IAsyncEnumerable<SubstrateChange> RunArtifactAsync(
        ArtifactJob job,
        IDecomposerContext context,
        DecomposerOptions options,
        int batch,
        [EnumeratorCancellation] CancellationToken ct)
    {
        IDecomposer phase = job.Kind switch
        {
            ArtifactKind.Ducet => new DucetTier0Phase(job.Path, batch),
            ArtifactKind.UcdXml => new UcdXmlValidationPhase(job.Path, batch),
            ArtifactKind.UnicodeData => new UnicodeDataPhase(this, job.Path, batch),
            ArtifactKind.Scripts => new RangePropertyPhase(
                this, job.Path, "scripts", UcdProperties.RelTypeHasScript,
                "unicode/script", batch),
            ArtifactKind.Blocks => new RangePropertyPhase(
                this, job.Path, "blocks", UcdProperties.RelTypeHasBlock,
                "unicode/block", batch),
            ArtifactKind.DerivedAge => new RangePropertyPhase(
                this, job.Path, "age", UcdProperties.RelTypeHasAge,
                "unicode/age", batch),
            ArtifactKind.LineBreak => new RangePropertyPhase(
                this, job.Path, "line-break", UcdProperties.RelTypeHasLineBreak,
                "unicode/line_break", batch),
            ArtifactKind.EastAsianWidth => new RangePropertyPhase(
                this, job.Path, "east-asian-width", UcdProperties.RelTypeHasEastAsianWidth,
                "unicode/east_asian_width", batch),
            ArtifactKind.JoiningType => new RangePropertyPhase(
                this, job.Path, "joining-type", UcdProperties.RelTypeHasJoiningType,
                "unicode/joining_type", batch),
            ArtifactKind.NumericType => new RangePropertyPhase(
                this, job.Path, "numeric-type", UcdProperties.RelTypeHasNumericType,
                "unicode/numeric_type", batch),
            ArtifactKind.BidiMirroring => new MirrorPhase(job.Path, batch),
            ArtifactKind.EmojiData => new RangePropertyPhase(
                this, job.Path, "emoji", UcdProperties.RelTypeHasEmojiProperty,
                "unicode/emoji", batch,
                new HashSet<string>(UcdProperties.EmojiPropNames, StringComparer.Ordinal)),
            ArtifactKind.NameAliases => new AliasPhase(job.Path, batch),
            ArtifactKind.Confusables => new ConfusablePhase(job.Path, batch),
            ArtifactKind.DerivedNormalization => new NormalizationPhase(this, job.Path, batch),
            _ => throw new InvalidOperationException($"Unsupported Unicode artifact kind {job.Kind}."),
        };

        await foreach (SubstrateChange change in RunPhaseAsync(
            phase, context, options, job.Label, job.Path, ct))
        {
            yield return change;
        }
    }

    private IReadOnlyList<ArtifactJob> ResolveArtifactJobs(IDecomposerContext context)
    {
        string baseDir = Path.GetFullPath(context.EcosystemPath);
        string xml = Path.GetFullPath(
            _ucdxmlZip ?? Path.Combine(baseDir, "ucdxml", "ucd.nounihan.flat.zip"));
        string ducet = Path.GetFullPath(
            _ducet ?? Path.Combine(baseDir, "uca", "allkeys.txt"));

        if (context.HasArtifactGraph)
        {
            var jobs = new List<ArtifactJob>(context.SelectedArtifacts.Count);
            var kinds = new HashSet<ArtifactKind>();
            foreach (IngestArtifact artifact in context.SelectedArtifacts)
            {
                string path = Path.GetFullPath(artifact.Path);
                ArtifactKind kind = ClassifyArtifact(path, baseDir, xml, ducet);
                if (!kinds.Add(kind))
                    throw new InvalidOperationException(
                        $"Unicode selected more than one admitted artifact for role {kind}; "
                        + "equivalent/superseded packaging must not double-vote.");
                jobs.Add(new ArtifactJob(kind, path, artifact.FileLabel));
            }
            jobs.Sort(static (left, right) => left.Kind.CompareTo(right.Kind));
            return jobs;
        }

        var legacy = new List<ArtifactJob>();
        AddIfPresent(legacy, ArtifactKind.Ducet, ducet, "uca/allkeys.txt");
        AddIfPresent(legacy, ArtifactKind.UcdXml, xml, "ucdxml/ucd.nounihan.flat.zip");
        AddIfPresent(legacy, ArtifactKind.UnicodeData,
            Path.Combine(baseDir, "ucd", "UnicodeData.txt"), "ucd/UnicodeData.txt");
        AddIfPresent(legacy, ArtifactKind.Scripts,
            Path.Combine(baseDir, "ucd", "Scripts.txt"), "ucd/Scripts.txt");
        AddIfPresent(legacy, ArtifactKind.Blocks,
            Path.Combine(baseDir, "ucd", "Blocks.txt"), "ucd/Blocks.txt");
        AddIfPresent(legacy, ArtifactKind.DerivedAge,
            Path.Combine(baseDir, "ucd", "DerivedAge.txt"), "ucd/DerivedAge.txt");
        AddIfPresent(legacy, ArtifactKind.LineBreak,
            Path.Combine(baseDir, "ucd", "LineBreak.txt"), "ucd/LineBreak.txt");
        AddIfPresent(legacy, ArtifactKind.EastAsianWidth,
            Path.Combine(baseDir, "ucd", "EastAsianWidth.txt"), "ucd/EastAsianWidth.txt");
        AddFirstPresent(legacy, ArtifactKind.JoiningType,
            (Path.Combine(baseDir, "ucd", "extracted", "DerivedJoiningType.txt"),
             "ucd/extracted/DerivedJoiningType.txt"),
            (Path.Combine(baseDir, "ucd", "DerivedJoiningType.txt"),
             "ucd/DerivedJoiningType.txt"));
        AddFirstPresent(legacy, ArtifactKind.NumericType,
            (Path.Combine(baseDir, "ucd", "extracted", "DerivedNumericType.txt"),
             "ucd/extracted/DerivedNumericType.txt"),
            (Path.Combine(baseDir, "ucd", "DerivedNumericType.txt"),
             "ucd/DerivedNumericType.txt"));
        AddIfPresent(legacy, ArtifactKind.BidiMirroring,
            Path.Combine(baseDir, "ucd", "BidiMirroring.txt"), "ucd/BidiMirroring.txt");
        AddIfPresent(legacy, ArtifactKind.EmojiData,
            Path.Combine(baseDir, "ucd", "emoji", "emoji-data.txt"), "ucd/emoji/emoji-data.txt");
        AddIfPresent(legacy, ArtifactKind.NameAliases,
            Path.Combine(baseDir, "ucd", "NameAliases.txt"), "ucd/NameAliases.txt");
        AddIfPresent(legacy, ArtifactKind.Confusables,
            Path.Combine(baseDir, "security", "confusables.txt"), "security/confusables.txt");
        AddIfPresent(legacy, ArtifactKind.DerivedNormalization,
            Path.Combine(baseDir, "ucd", "DerivedNormalizationProps.txt"),
            "ucd/DerivedNormalizationProps.txt");
        legacy.Sort(static (left, right) => left.Kind.CompareTo(right.Kind));
        return legacy;
    }

    private static ArtifactKind ClassifyArtifact(
        string fullPath,
        string baseDir,
        string xml,
        string ducet)
    {
        if (string.Equals(fullPath, ducet, StringComparison.Ordinal)) return ArtifactKind.Ducet;
        if (string.Equals(fullPath, xml, StringComparison.Ordinal)) return ArtifactKind.UcdXml;

        string relative = Path.GetRelativePath(baseDir, fullPath).Replace('\\', '/');
        return relative switch
        {
            "uca/allkeys.txt" => ArtifactKind.Ducet,
            "ucdxml/ucd.nounihan.flat.zip" or "ucdxml/ucd.nounihan.flat.xml" => ArtifactKind.UcdXml,
            "ucd/UnicodeData.txt" => ArtifactKind.UnicodeData,
            "ucd/Scripts.txt" => ArtifactKind.Scripts,
            "ucd/Blocks.txt" => ArtifactKind.Blocks,
            "ucd/DerivedAge.txt" => ArtifactKind.DerivedAge,
            "ucd/LineBreak.txt" => ArtifactKind.LineBreak,
            "ucd/EastAsianWidth.txt" => ArtifactKind.EastAsianWidth,
            "ucd/extracted/DerivedJoiningType.txt" or "ucd/DerivedJoiningType.txt" => ArtifactKind.JoiningType,
            "ucd/extracted/DerivedNumericType.txt" or "ucd/DerivedNumericType.txt" => ArtifactKind.NumericType,
            "ucd/BidiMirroring.txt" => ArtifactKind.BidiMirroring,
            "ucd/emoji/emoji-data.txt" => ArtifactKind.EmojiData,
            "ucd/NameAliases.txt" => ArtifactKind.NameAliases,
            "security/confusables.txt" => ArtifactKind.Confusables,
            "ucd/DerivedNormalizationProps.txt" => ArtifactKind.DerivedNormalization,
            _ => throw new InvalidOperationException(
                $"Selected Unicode artifact has no ingest disposition/handler: '{fullPath}'."),
        };
    }

    private static void AddIfPresent(
        List<ArtifactJob> jobs,
        ArtifactKind kind,
        string path,
        string label)
    {
        path = Path.GetFullPath(path);
        if (File.Exists(path)) jobs.Add(new ArtifactJob(kind, path, label));
    }

    private static void AddFirstPresent(
        List<ArtifactJob> jobs,
        ArtifactKind kind,
        params (string Path, string Label)[] candidates)
    {
        foreach ((string path, string label) in candidates)
        {
            string full = Path.GetFullPath(path);
            if (!File.Exists(full)) continue;
            jobs.Add(new ArtifactJob(kind, full, label));
            return;
        }
    }

    private enum ArtifactKind
    {
        Ducet = 0,
        UcdXml = 1,
        UnicodeData = 2,
        Scripts = 3,
        Blocks = 4,
        DerivedAge = 5,
        LineBreak = 6,
        EastAsianWidth = 7,
        JoiningType = 8,
        NumericType = 9,
        BidiMirroring = 10,
        EmojiData = 11,
        NameAliases = 12,
        Confusables = 13,
        DerivedNormalization = 14,
    }

    private readonly record struct ArtifactJob(ArtifactKind Kind, string Path, string Label);

    private abstract class UnicodeComposePhase<T> : ComposeDecomposerPhase<T>
    {
        private readonly int _commitEpoch;
        private readonly int? _attestationCapacity;

        protected UnicodeComposePhase(int batch, int commitEpoch = 0, int? attestationCapacity = null)
        {
            _commitEpoch = commitEpoch;
            _attestationCapacity = attestationCapacity;
        }

        public override Hash128 SourceId => Source;
        public override string SourceName => "UnicodeDecomposer";
        public override int LayerOrder => 0;
        public override Hash128 TrustClassId => TrustClass;
        protected override double SourceTrust => TC.StandardsDerived;

        public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.CompletedTask;

        public override Task<long?> EstimateUnitCountAsync(
            IDecomposerContext context, CancellationToken ct = default)
            => Task.FromResult<long?>(null);

        protected override IngestBatchConfig BuildPipelineConfig(
            IDecomposerContext context,
            DecomposerOptions options) =>
            IngestPipelineDefaults.ApplyMaxInputUnits(
                IngestPipelineDefaults.Compose(
                    SourceId,
                    BatchLabelPrefix,
                    options,
                    context.Reader,
                    IngestSourceProfile.Unicode,
                    attestationCapacity: _attestationCapacity,
                    commitEpoch: _commitEpoch),
                options);
    }

    private sealed class DucetTier0Phase : UnicodeComposePhase<int>
    {
        private readonly string _path;
        private CodepointRecord[]? _records;

        public DucetTier0Phase(string path, int batch)
            : base(batch, attestationCapacity: 0) => _path = path;

        protected override string PhaseLabel => "uca/allkeys";

        protected override void Compose(int cp, SubstrateChangeBuilder builder)
        {
            CodepointRecord[] records = _records
                ?? throw new InvalidOperationException("DUCET geometry has not been computed.");
            ref readonly CodepointRecord record = ref records[cp];
            Hash128 entityId = record.Hash;
            builder.AddEntity(entityId, tier: 0, CodepointType, firstObservedBy: Source);
            Hash128 physicalityId = PhysicalityId.Compute(entityId, PhysicalityType.Content);
            builder.AddPhysicality(new PhysicalityRow(
                Id: physicalityId,
                EntityId: entityId,
                SourceId: Source,
                Type: PhysicalityType.Content,
                CoordX: record.CoordX,
                CoordY: record.CoordY,
                CoordZ: record.CoordZ,
                CoordM: record.CoordM,
                HilbertIndex: record.Hilbert,
                TrajectoryXyzm: null,
                NConstituents: 0,
                AlignmentResidual: null,
                SourceDim: null,
                ObservedAtUnixUs: 0));

            if (cp == 0) EmitByteCatalog(builder);
            if (cp <= 0xFF) EmitByte(builder, (byte)cp);
        }

        protected override async IAsyncEnumerable<int> ExtractRecordsAsync(
            string ecosystemPath,
            DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            _records = UnicodeSeed.ComputeDucetGeometry(_path);
            await Task.CompletedTask;
            for (int cp = 0; cp < _records.Length; ++cp)
            {
                ct.ThrowIfCancellationRequested();
                yield return cp;
            }
        }

        private static void EmitByteCatalog(SubstrateChangeBuilder builder)
        {
            Hash128 latin1 = SubstrateCanonicalIds.OfVersioned("encoding", "ISO-8859-1");
            Hash128 cp1252 = SubstrateCanonicalIds.OfVersioned("encoding", "windows-1252");
            Hash128 encodingType = EntityTypeRegistry.CharacterEncoding;
            Hash128 roleType = EntityTypeRegistry.Utf8Role;
            builder.AddEntity(new EntityRow(latin1, EntityTier.Word, encodingType, Source));
            builder.AddEntity(new EntityRow(cp1252, EntityTier.Word, encodingType, Source));
            foreach (string role in new[] { "continuation", "lead2", "lead3", "lead4", "invalid" })
            {
                Hash128 roleId = Hash128.OfCanonical($"substrate/utf8/{role}/v1");
                builder.AddEntity(new EntityRow(roleId, EntityTier.Word, roleType, Source));
            }
        }

        private static void EmitByte(SubstrateChangeBuilder builder, byte value)
        {
            Hash128 byteId = ByteAtoms.Id(value);
            builder.AddEntity(byteId, tier: 0, ByteAtoms.TypeId, firstObservedBy: Source);
            double[] coord = ByteAtoms.Coord(value);
            Hash128 physicalityId = PhysicalityId.Compute(byteId, PhysicalityType.Content);
            builder.AddPhysicality(new PhysicalityRow(
                Id: physicalityId,
                EntityId: byteId,
                SourceId: Source,
                Type: PhysicalityType.Content,
                CoordX: coord[0],
                CoordY: coord[1],
                CoordZ: coord[2],
                CoordM: coord[3],
                HilbertIndex: ByteAtoms.Hilbert(value),
                TrajectoryXyzm: null,
                NConstituents: 0,
                AlignmentResidual: null,
                SourceDim: null,
                ObservedAtUnixUs: 0));

            Hash128 roleId = Hash128.OfCanonical($"substrate/utf8/{ByteAtoms.Utf8Role(value)}/v1");
            builder.AddAttestation(NativeAttestation.Categorical(
                byteId, "HAS_UTF8_ROLE", roleId, Source, TC.StandardsDerived));

            Hash128 latin1 = SubstrateCanonicalIds.OfVersioned("encoding", "ISO-8859-1");
            Hash128 cp1252 = SubstrateCanonicalIds.OfVersioned("encoding", "windows-1252");
            builder.AddAttestation(NativeAttestation.Categorical(
                byteId, "DECODES_TO", CodepointId(value), Source,
                TC.StandardsDerived, contextId: latin1));

            uint cp1252Target = value <= 0x9F
                ? ByteAtoms.Cp1252High[value - 0x80]
                : value;
            if (cp1252Target != 0)
                builder.AddAttestation(NativeAttestation.Categorical(
                    byteId, "DECODES_TO", CodepointId(cp1252Target), Source,
                    TC.StandardsDerived, contextId: cp1252));
        }
    }

    private sealed class UcdXmlValidationPhase : UnicodeComposePhase<int>
    {
        private readonly string _path;

        public UcdXmlValidationPhase(string path, int batch)
            : base(batch, attestationCapacity: 0) => _path = path;

        protected override string PhaseLabel => "ucdxml";
        protected override void Compose(int record, SubstrateChangeBuilder builder) { }

        protected override async IAsyncEnumerable<int> ExtractRecordsAsync(
            string ecosystemPath,
            DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            UnicodeSeed.ValidateUcdXml(_path);
            await Task.CompletedTask;
            yield return 0;
        }
    }

    private sealed class UnicodeDataPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.UnicodeDataRow>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;

        public UnicodeDataPhase(UnicodeDecomposer owner, string path, int batch)
            : base(batch) => (_owner, _path) = (owner, path);

        protected override string PhaseLabel => "ucd/UnicodeData";

        protected override void Compose(
            UnicodePhysicalArtifactParser.UnicodeDataRow row,
            SubstrateChangeBuilder builder)
        {
            Hash128 entityId = CodepointId(row.Codepoint);
            double structural = RelationTypeRank.StandardsStructural * TC.StandardsDerived;

            if (row.Name is { Length: > 0 } name
                && ContentEmitter.Emit(builder, name, Source) is { } nameId)
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    entityId, UcdProperties.RelTypeHasName, nameId,
                    Source, null, structural));

            if (row.GeneralCategory.Length > 0)
            {
                Hash128 categoryId = _owner.ClassifierEntity(
                    builder, "unicode/category", row.GeneralCategory);
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    entityId, UcdProperties.RelTypeHasGeneralCategory, categoryId,
                    Source, null, structural));
            }

            if (row.CombiningClass > 0)
            {
                Hash128 ccId = _owner.ClassifierEntity(
                    builder, "unicode/combining_class",
                    row.CombiningClass.ToString(CultureInfo.InvariantCulture));
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    entityId, UcdProperties.RelTypeHasCombiningClass, ccId,
                    Source, null, structural));
            }

            if (row.BidiClass.Length > 0)
            {
                Hash128 bidiId = _owner.ClassifierEntity(
                    builder, "unicode/bidi_class", row.BidiClass);
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    entityId, UcdProperties.RelTypeHasBidiClass, bidiId,
                    Source, null, structural));
            }

            if (row.NumericValue is { Length: > 0 } numeric)
            {
                Hash128 numericId = _owner.ClassifierEntity(
                    builder, "unicode/numeric", numeric);
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    entityId, UcdProperties.RelTypeHasNumericValue, numericId,
                    Source, null, RelationTypeRank.ScalarValued * TC.StandardsDerived));
            }

            if (row.UppercaseMapping != 0)
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    entityId, UcdProperties.RelTypeHasUppercaseMapping,
                    CodepointId(row.UppercaseMapping), Source, null, structural));
            if (row.LowercaseMapping != 0)
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    entityId, UcdProperties.RelTypeHasLowercaseMapping,
                    CodepointId(row.LowercaseMapping), Source, null, structural));
            if (row.TitlecaseMapping != 0)
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    entityId, UcdProperties.RelTypeHasTitlecaseMapping,
                    CodepointId(row.TitlecaseMapping), Source, null, structural));

            EmitDecomposition(
                builder, entityId, row.CanonicalDecomposition,
                UcdProperties.RelTypeCanonDecomposesTo, structural);
            EmitDecomposition(
                builder, entityId, row.CompatibilityDecomposition,
                UcdProperties.RelTypeCompatDecomposesTo, structural);
        }

        private static void EmitDecomposition(
            SubstrateChangeBuilder builder,
            Hash128 subject,
            uint[]? decomposition,
            Hash128 relation,
            double weight)
        {
            if (decomposition is null || decomposition.Length == 0) return;
            EnsureOrdinalContexts(builder);
            for (int index = 0; index < decomposition.Length; ++index)
            {
                Hash128 context = index == 0
                    ? UcdProperties.OrdinalCtx0
                    : UcdProperties.OrdinalCtx1;
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    subject, relation, CodepointId(decomposition[index]),
                    Source, context, weight));
            }
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.UnicodeDataRow>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.UnicodeDataAsync(_path, ct);
    }

    private sealed class RangePropertyPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.RangePoint>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;
        private readonly string _label;
        private readonly Hash128 _relation;
        private readonly string _canonicalPrefix;
        private readonly HashSet<string>? _allowed;

        public RangePropertyPhase(
            UnicodeDecomposer owner,
            string path,
            string label,
            Hash128 relation,
            string canonicalPrefix,
            int batch,
            HashSet<string>? allowed = null)
            : base(batch)
        {
            _owner = owner;
            _path = path;
            _label = label;
            _relation = relation;
            _canonicalPrefix = canonicalPrefix;
            _allowed = allowed;
        }

        protected override string PhaseLabel => _label;

        protected override long UnitsPerRecord(UnicodePhysicalArtifactParser.RangePoint row) =>
            row.CountsSourceRow ? 1 : 0;

        protected override void Compose(
            UnicodePhysicalArtifactParser.RangePoint row,
            SubstrateChangeBuilder builder)
        {
            if (_allowed is not null && !_allowed.Contains(row.Value)) return;
            Hash128 valueId = _owner.ClassifierEntity(
                builder, _canonicalPrefix, row.Value);
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                CodepointId(row.Codepoint), _relation, valueId, Source, null,
                RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.RangePoint>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.RangePointsAsync(_path, ct);
    }

    private sealed class MirrorPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.MirrorRow>
    {
        private readonly string _path;
        public MirrorPhase(string path, int batch) : base(batch) => _path = path;
        protected override string PhaseLabel => "bidi-mirroring";

        protected override void Compose(
            UnicodePhysicalArtifactParser.MirrorRow row,
            SubstrateChangeBuilder builder)
        {
            if (row.Codepoint > row.Mirror) return;
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                CodepointId(row.Codepoint), UcdProperties.RelTypeHasMirror,
                CodepointId(row.Mirror), Source, null,
                RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.MirrorRow>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.MirrorsAsync(_path, ct);
    }

    private sealed class AliasPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.AliasRow>
    {
        private readonly string _path;
        public AliasPhase(string path, int batch) : base(batch, commitEpoch: 1) => _path = path;
        protected override string PhaseLabel => "name-aliases";

        protected override void Compose(
            UnicodePhysicalArtifactParser.AliasRow row,
            SubstrateChangeBuilder builder)
        {
            if (ContentEmitter.Emit(builder, row.Alias, Source) is not { } aliasId) return;
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                CodepointId(row.Codepoint), UcdProperties.RelTypeHasNameAlias,
                aliasId, Source, null,
                RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.AliasRow>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.AliasesAsync(_path, ct);
    }

    private sealed class ConfusablePhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.ConfusableRow>
    {
        private readonly string _path;
        public ConfusablePhase(string path, int batch) : base(batch, commitEpoch: 1) => _path = path;
        protected override string PhaseLabel => "confusables";

        protected override void Compose(
            UnicodePhysicalArtifactParser.ConfusableRow row,
            SubstrateChangeBuilder builder)
        {
            string target = row.Target;
            int first = char.ConvertToUtf32(target, 0);
            int firstLength = char.IsSurrogatePair(target, 0) ? 2 : 1;
            Hash128? targetId = target.Length == firstLength
                ? CodepointId((uint)first)
                : ContentEmitter.Emit(builder, target, Source);
            if (targetId is null) return;
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                CodepointId(row.Codepoint), UcdProperties.RelTypeConfusableWith,
                targetId.Value, Source, null,
                RelationTypeRank.StandardsStructural * TC.StandardsDerived));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.ConfusableRow>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.ConfusablesAsync(_path, ct);
    }

    private sealed class NormalizationPhase
        : UnicodeComposePhase<UnicodePhysicalArtifactParser.NormalizationRow>
    {
        private readonly UnicodeDecomposer _owner;
        private readonly string _path;
        public NormalizationPhase(UnicodeDecomposer owner, string path, int batch)
            : base(batch) => (_owner, _path) = (owner, path);
        protected override string PhaseLabel => "normalization";

        protected override long UnitsPerRecord(UnicodePhysicalArtifactParser.NormalizationRow row) =>
            row.CountsSourceRow ? 1 : 0;

        protected override void Compose(
            UnicodePhysicalArtifactParser.NormalizationRow row,
            SubstrateChangeBuilder builder)
        {
            Hash128 formId = _owner.ClassifierEntity(
                builder, "unicode/normalization_form", row.Form);
            double weight = RelationTypeRank.StandardsStructural * TC.StandardsDerived;
            Hash128 subject = CodepointId(row.Codepoint);
            builder.AddAttestation(row.Maybe
                ? NativeAttestation.ResolvedScored(
                    subject, UcdProperties.RelTypeHasNormalizationForm,
                    formId, Source, null, weight,
                    signedMagnitude: 0.0, arenaScale: 1.0)
                : NativeAttestation.CategoricalResolved(
                    subject, UcdProperties.RelTypeHasNormalizationForm,
                    formId, Source, null, weight, confirm: false));
        }

        protected override IAsyncEnumerable<UnicodePhysicalArtifactParser.NormalizationRow>
            ExtractRecordsAsync(
                string ecosystemPath,
                DecomposerOptions options,
                CancellationToken ct) =>
            UnicodePhysicalArtifactParser.NormalizationAsync(_path, ct);
    }
}
