using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// One grammar-file record for sources that parse a whole file through tree-sitter
/// (Code, Stack, …). Extraction only; compose/existence/emit uses the shared pipeline.
/// </summary>
public readonly record struct GrammarComposeRecord(
    byte[] Utf8,
    string Modality,
    IReadOnlyList<string>? ExampleSegments = null,
    string? ConceptAnchorKey = null,
    Hash128? ConceptCategoryTypeId = null,
    IReadOnlyList<string>? KeywordExamples = null,
    Hash128? ParentContainerId = null,
    // Legacy content/provenance hint. A plain-text identity is not proof that the
    // source grammar's complete syntax and lexical representation was admitted.
    Hash128? SourceId = null,
    // Present for physical source-file observations. Synthetic grammar records leave
    // this null and retain their grammar root as the record root.
    FileMetadata? FileMetadata = null,
    byte[]? ObservedPromptUtf8 = null,
    // A source's declared structural semantics reuse the retained native AST.
    // The ordinary full-source grammar/file admission remains the record owner.
    IGrammarWitness? StructureWitness = null,
    bool RequireSourceAst = false,
    bool RawText = false) : IIngestResidentRecord
{
    public long ResidentInputBytes => (Utf8?.LongLength ?? 0) + (ObservedPromptUtf8?.LongLength ?? 0);
}

/// <summary>
/// Single handler for whole-file grammar compose lanes. CreateDeferredUnit runs
/// the shared full-source native composer on generic workers; DrainInto stages the
/// retained grammar and lexical trees through the same bulk operation as user code.
/// </summary>
public sealed class GrammarComposeHandler : IIngestRecordHandler<GrammarComposeRecord>
{
    private static readonly Hash128 ExampleRelation = RelationTypeRegistry.Resolve("HAS_EXAMPLE").Id;
    private readonly Hash128 _sourceId;
    private readonly double _trust;
    private readonly ISubstrateReader? _reader;

    public GrammarComposeHandler(Hash128 sourceId, double trust, ISubstrateReader? reader)
    {
        _sourceId = sourceId;
        _trust = trust;
        _reader = reader;
    }

    public IIngestDeferredUnit CreateDeferredUnit(GrammarComposeRecord record) =>
        record.RawText ? new RawFileUnit(record, _sourceId, _trust)
            : new Unit(record, _sourceId, _trust, _reader);

    /// <summary>
    /// Source-scoped witnesses remain attached to the native source root even when
    /// its content structures deduplicate with another admitted artifact.
    /// </summary>
    public void WalkWitness(
        GrammarComposeRecord record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit)
    {
        if (root == default) return;
        EmitConceptLinks(builder, record, root, _sourceId, _trust);
        if (record.StructureWitness is { } witness)
        {
            if (!string.Equals(record.Modality, witness.ModalityId, StringComparison.Ordinal))
                throw new InvalidOperationException("source witness grammar does not match the admitted source");
            if (unit is not Unit native)
                throw new InvalidOperationException("source witness requires the retained native grammar unit");
            witness.WalkRow(native.WitnessContext(root), new RowContext(0, 1, root), builder);
        }
    }

    private static void EmitConceptLinks(
        SubstrateChangeBuilder builder, GrammarComposeRecord record, Hash128 rootId, Hash128 sourceId, double trust)
    {
        if (record.ConceptAnchorKey is not { Length: > 0 }
            || record.ConceptCategoryTypeId is not { } ctype || ctype == default
            || CategoryAnchor.Emit(builder, record.ConceptAnchorKey, ctype, sourceId, trust) is not { } conceptId)
            return;

        if (record.ParentContainerId is { } parent && parent != default)
        {
            builder.AddAttestation(NativeAttestation.Categorical(
                parent, "CONTAINS", conceptId, sourceId, trust));
        }
        builder.AddAttestation(NativeAttestation.CategoricalResolved(
            conceptId, ExampleRelation, rootId, sourceId, null, trust));
        builder.AddAttestation(NativeAttestation.Categorical(
            rootId, "HAS_DEFINITION", conceptId, sourceId, trust));
    }

    private sealed class RawFileUnit : IIngestDeferredUnit
    {
        private readonly GrammarComposeRecord _record;
        private readonly Hash128 _sourceId;
        private readonly double _trust;
        private readonly IIngestDeferredUnit _content;
        public RawFileUnit(GrammarComposeRecord record, Hash128 sourceId, double trust)
        {
            if (record.Modality != "text" || record.FileMetadata is null || record.FileMetadata.Value.Modality != "text"
                || record.StructureWitness is not null || record.ObservedPromptUtf8 is not null
                || !GrammarSourceFileSupport.IsExactNativeText(record.Utf8))
                throw new InvalidDataException("Raw source admission requires exact native text and physical file metadata.");
            _record = record; _sourceId = sourceId; _trust = trust;
            _content = new ContentIngestHandler(sourceId).CreateDeferredUnit(new ContentIngestRecord(record.Utf8));
        }
        public TierTree? TreeForBatchProbe => _content.TreeForBatchProbe;
        public long ResidentBytes => _content.ResidentBytes;
        public Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct)
            => _content.ProbeDescentAsync(reader, ct);
        public Hash128 DrainInto(SubstrateChangeBuilder builder, double witnessWeight, byte[]? bitmap)
        {
            Hash128 content = _content.DrainInto(builder, witnessWeight, bitmap);
            var tree = _content.TreeForBatchProbe ?? throw new InvalidDataException("Raw source native content tree is absent.");
            FileIdentity file = FileEntity.Emit(builder, _sourceId, FileEntity.RootComponent(tree), _record.FileMetadata!.Value, _trust);
            if (content == default || content != file.ContentRootId)
                throw new InvalidDataException("Raw source changed between native content and file composition.");
            builder.SetFileId(file.FileId);
            return file.FileId;
        }
        public void Dispose() => _content.Dispose();
    }

    private sealed class Unit : IIngestDeferredUnit
    {
        private readonly GrammarComposeRecord _record;
        private readonly Hash128 _sourceId;
        private readonly double _trust;
        private GrammarAst? _ast;
        private GrammarRowComposer? _composer;
        private GrammarAst? _promptAst;
        private GrammarRowComposer? _promptComposer;
        private TierTree? _promptTree;
        private OrderedCompositionRequest? _observation;
        private OrderedCompositionComponent _root;
        private Hash128 _rootId;
        private bool _disposed;

        public Unit(GrammarComposeRecord record, Hash128 sourceId, double trust, ISubstrateReader? reader)
        {
            _record = record;
            _sourceId = sourceId;
            _trust = trust;
            Build(reader);
        }

        public TierTree? TreeForBatchProbe => null;

        public GrammarComposeContext WitnessContext(Hash128 root)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new GrammarComposeContext(_record.Utf8,
                _ast ?? throw new InvalidOperationException("source grammar is unavailable"),
                root, _composer);
        }

        public long ResidentBytes => checked((_composer?.ResidentBytes ?? 0)
            + (_promptComposer?.ResidentBytes ?? 0) + (_promptTree?.ResidentBytes ?? 0));

        public Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct) =>
            Task.FromResult<byte[]?>(null);

        public Hash128 DrainInto(SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_composer is null || _ast is null)
                throw new InvalidOperationException("whole-source grammar composition is unavailable");
            Hash128 emitted = _composer.DrainInto(builder, witnessWeight, descentBitmap);
            if (emitted != _root.Id)
                throw new InvalidOperationException("whole-source identity changed during staging");
            GrammarTagWitness.Emit(builder, _record.Utf8, _ast, _composer,
                _record.Modality, _sourceId, _trust, _rootId);

            if (_observation is not null && _promptComposer is not null)
            {
                _promptComposer.DrainInto(builder, witnessWeight);
                if (_promptTree is null || !ContentTierSpine.EmitTree(
                        builder, _promptTree, _sourceId, [], out Hash128 prompt))
                    throw new InvalidOperationException("observed prompt has no canonical conversation identity");
                Span<OrderedCompositionResult> pair = stackalloc OrderedCompositionResult[1];
                OrderedComposition.StageBatch(builder.ContentStage, [_observation], pair);
                if (pair[0].Id != _rootId)
                    throw new InvalidOperationException("observed prompt/response identity changed during staging");
                // Resolve the instruction exactly as conversation admission does.
                // The context keeps the unnormalized source bytes and their
                // ordered pairing; a grammar AST is not the query's text root.
                builder.AddAttestation(NativeAttestation.CategoricalResolved(
                    prompt, ExampleRelation, emitted, _sourceId, _rootId, _trust));
            }

            if (_rootId != default && _record.ExampleSegments is { Count: > 0 })
            {
                foreach (var seg in _record.ExampleSegments)
                {
                    if (seg.Length < 3) continue;
                    if (ContentTierSpine.TryStageIntoBuilder(
                            builder, System.Text.Encoding.UTF8.GetBytes(seg), _sourceId, out var segRoot))
                    {
                        builder.AddAttestation(NativeAttestation.CategoricalResolved(
                            segRoot, ExampleRelation, _rootId, _sourceId, null, _trust));
                    }
                }
            }

            // Concept-anchor converse.links(repo CONTAINS this file, file HAS_DEFINITION this
            // concept) moved to WalkWitness — the pipeline calls handler.WalkWitness
            // right after DrainInto on every novel record (IngestDescentFlush.cs) AND
            // on every content-already-known short-circuit (IngestExistenceGate.cs), so
            // emitting them there instead of here covers both paths from one call site
            // instead of duplicating the block (GH #592).

            if (_rootId != default && _record.KeywordExamples is { Count: > 0 })
            {
                foreach (var kw in _record.KeywordExamples)
                {
                    if (kw.Length < 4) continue;
                    if (ContentTierSpine.TryStageIntoBuilder(
                            builder, System.Text.Encoding.UTF8.GetBytes(kw), _sourceId, out var kwRoot))
                    {
                        builder.AddAttestation(NativeAttestation.CategoricalResolved(
                            kwRoot, ExampleRelation, _rootId, _sourceId, null, _trust));
                    }
                }
            }

            if (_record.FileMetadata is not { } metadata)
                return _rootId;
            if (!string.Equals(metadata.Modality, _record.Modality, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "whole-source file metadata modality does not match its grammar recipe");
            FileIdentity file = FileEntity.Emit(builder, _sourceId, _root, metadata, _trust);
            if (file.ContentRootId != _rootId)
                throw new InvalidOperationException(
                    "whole-source file composition changed its grammar content identity");
            builder.SetFileId(file.FileId);
            return file.FileId;
        }

        private void Build(ISubstrateReader? reader)
        {
            IntPtr recipe = GrammarDecomposer.LookupById(_record.Modality);
            if (recipe == IntPtr.Zero)
                throw new InvalidOperationException($"unknown source grammar '{_record.Modality}'");
            try
            {
                _ast = GrammarDecomposer.Parse(_record.Utf8, recipe);
                if (_record.RequireSourceAst) GrammarSourceFileSupport.RequireNativeSourceAst(_ast);
                _composer = new GrammarRowComposer(_record.Utf8, _ast, _sourceId,
                    _record.Modality, GrammarCompositionMode.FullSource);
                _root = _composer.RootComponent();
                _rootId = _root.Id;
                if (_record.ObservedPromptUtf8 is { Length: > 0 } prompt)
                {
                    if (_record.FileMetadata is not null)
                        throw new InvalidOperationException("a prompt/response record requires its own physical container metadata");
                    _promptAst = GrammarDecomposer.Parse(prompt, "markdown");
                    _promptComposer = new GrammarRowComposer(prompt, _promptAst, _sourceId,
                        "markdown", GrammarCompositionMode.FullSource);
                    _promptTree = ContentTierSpine.BuildTree(prompt)
                        ?? throw new InvalidOperationException("observed prompt cannot be resolved as conversation content");
                    _observation = new OrderedCompositionRequest(
                        [_promptComposer.RootComponent(), _root], EntityTypeRegistry.Text, _sourceId, 0);
                    _rootId = OrderedComposition.ComposeBatch([_observation])[0].Id;
                }
            }
            catch
            {
                _composer?.Dispose();
                _ast?.Dispose();
                _promptComposer?.Dispose();
                _promptAst?.Dispose();
                _promptTree?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _composer?.Dispose();
            _ast?.Dispose();
            _promptComposer?.Dispose();
            _promptAst?.Dispose();
            _promptTree?.Dispose();
        }
    }
}

public static class GrammarComposeIngestSupport
{
    public static IngestBatchConfig PipelineConfig(
        Hash128 sourceId, string batchLabelPrefix, ISubstrateReader? reader)
    {
        var profile = IngestSourceProfile.Default;
        var ws = IngestPipelineDefaults.ResolveWorkingSet(profile);
        return new()
        {
            SourceId = sourceId,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = ws.Batch,
            ProbeChunkSize = ws.ProbeChunk,
            ContainmentReader = reader,
            EntityCapacity = ws.Batch * 8,
            PhysicalityCapacity = ws.Batch * 8,
            AttestationCapacity = ws.Batch * 16,
            WorkingSet = WorkingSetMode.Enabled,
            WorkingSetProbeInterval = ws.ProbeInterval,
            WorkingSetRecordCap = ws.RecordCap,
            WorkingSetProfile = profile,
        };
    }

    public static IAsyncEnumerable<SubstrateChange> RunPipelineAsync(
        IAsyncEnumerable<GrammarComposeRecord> records,
        Hash128 sourceId,
        double trust,
        string batchLabelPrefix,
        ISubstrateReader? reader,
        DecomposerOptions options,
        CancellationToken ct = default)
    {
        if (options.DryRun) return Empty();
        var stream = new AsyncEnumerableRecordStream<GrammarComposeRecord>(records);
        var handler = new GrammarComposeHandler(sourceId, trust, reader);
        var config = IngestPipelineDefaults.ApplyMaxInputUnits(
            IngestPipelineDefaults.GrammarCompose(sourceId, batchLabelPrefix, options, reader),
            options);
        return IngestBatchPipeline.RunAsync(stream, handler, config, ct).WithSourcePrior(sourceId, trust, ct);
    }

    private static async IAsyncEnumerable<SubstrateChange> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>
/// Shared physical-file description for whole-source grammar lanes. The artifact graph
/// records every loose file under the selected root; only registered, authored source
/// files are admitted to grammar compose.
/// </summary>
public static class GrammarSourceFileSupport
{
    public static GrammarAstDiagnostics RequireNativeSourceAst(GrammarAst ast)
    {
        var diagnostics = ast.Diagnostics;
        if (diagnostics.AstNodeCount == 0 || ast.GetNode(0).Parent != GrammarAst.Root)
            throw new InvalidDataException("Native source grammar produced no rooted AST.");
        // Error/missing nodes describe a partial concrete syntax tree. The existing
        // native full-source composer retains exact source spans and gaps; it does
        // not invent bytes for zero-width recovery nodes. Receipts retain diagnostics.
        return diagnostics;
    }

    public static unsafe bool IsExactNativeText(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.AsSpan().Contains((byte)0)) return false;
        byte* normalized = null;
        nuint length = 0;
        try
        {
            fixed (byte* input = bytes)
                if (NativeInterop.NormalizeNfcUtf8(input, (nuint)bytes.Length, &normalized, &length) != 0)
                    return false;
            return length == (nuint)bytes.Length
                && bytes.AsSpan().SequenceEqual(new ReadOnlySpan<byte>(normalized, bytes.Length));
        }
        finally { if (normalized != null) System.Runtime.InteropServices.NativeMemory.Free(normalized); }
    }

    public static FileMetadata MetadataFromPath(
        string absolutePath, string relativePath, string modality)
    {
        var info = new FileInfo(absolutePath);
        if (!info.Exists)
            throw new FileNotFoundException(
                $"source file vanished between enumeration and open: {absolutePath}");
        return new FileMetadata(
            info.Name,
            relativePath.Replace('\\', '/'),
            info.Length,
            info.LastWriteTimeUtc,
            modality);
    }

    public static IngestArtifactGraph? BuildArtifactGraph(
        string root,
        string sourceName,
        string journalPrefix,
        Func<string, string?> modalityFor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPrefix);
        ArgumentNullException.ThrowIfNull(modalityFor);
        if (string.IsNullOrWhiteSpace(root)) return null;

        bool rootIsFile = File.Exists(root);
        if (!rootIsFile && !Directory.Exists(root)) return null;
        string fullRoot = Path.GetFullPath(root);
        IEnumerable<string> files = rootIsFile
            ? [fullRoot]
            : Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories)
                .OrderBy(static path => path, StringComparer.Ordinal);

        var artifacts = files.Select(file =>
        {
            string full = Path.GetFullPath(file);
            string relative = rootIsFile
                ? Path.GetFileName(full)
                : Path.GetRelativePath(fullRoot, full).Replace('\\', '/');
            string? modality = modalityFor(full);
            bool excluded = !rootIsFile && VendoredPathFilter.IsVendoredOrBuildPath(full, fullRoot);
            bool registered = modality is not null && GrammarDecomposer.LookupById(modality) != IntPtr.Zero;
            IngestArtifactDisposition disposition = excluded
                ? IngestArtifactDisposition.ExcludedWithReason
                : registered
                    ? IngestArtifactDisposition.Admitted
                    : IngestArtifactDisposition.Unsupported;
            string reason = excluded
                ? "vendored, generated, build-tree, or oversized source artifact"
                : registered
                    ? ""
                    : "no registered grammar for this source artifact";
            var info = new FileInfo(full);
            return new IngestArtifact(
                sourceName,
                "local-working-tree",
                relative,
                relative,
                full,
                disposition,
                UpstreamUrl: "",
                FetchedAtUtc: "",
                Bytes: info.Length,
                Sha256: "",
                UpstreamChecksum: "",
                MediaType: registered ? $"text/x-{modality}" : "",
                License: "",
                Citation: "",
                Language: "",
                Split: "",
                AnnotationOrigin: "local-filesystem",
                Notes: reason,
                JournalLabel: $"{journalPrefix}/{relative}",
                ModifiedAt: new DateTimeOffset(info.LastWriteTimeUtc));
        });
        return new IngestArtifactGraph(artifacts);
    }
}
