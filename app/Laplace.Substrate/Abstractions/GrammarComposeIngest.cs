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
    FileMetadata? FileMetadata = null);

/// <summary>
/// Single handler for whole-file grammar compose lanes. CreateDeferredUnit runs
/// the shared full-source native composer on generic workers; DrainInto stages the
/// retained grammar and lexical trees through the same bulk operation as user code.
/// </summary>
public sealed class GrammarComposeHandler : IIngestRecordHandler<GrammarComposeRecord>
{
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
        new Unit(record, _sourceId, _trust, _reader);

    /// <summary>
    /// Source-scoped witnesses remain attached to the native source root even when
    /// its content structures deduplicate with another admitted artifact.
    /// </summary>
    public void WalkWitness(
        GrammarComposeRecord record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit)
    {
        if (root == default) return;
        EmitConceptLinks(builder, record, root, _sourceId, _trust);
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
        builder.AddAttestation(NativeAttestation.Categorical(
            conceptId, "HAS_EXAMPLE", rootId, sourceId, trust));
        builder.AddAttestation(NativeAttestation.Categorical(
            rootId, "HAS_DEFINITION", conceptId, sourceId, trust));
    }

    private sealed class Unit : IIngestDeferredUnit
    {
        private readonly GrammarComposeRecord _record;
        private readonly Hash128 _sourceId;
        private readonly double _trust;
        private GrammarAst? _ast;
        private GrammarRowComposer? _composer;
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

        public Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct) =>
            Task.FromResult<byte[]?>(null);

        public Hash128 DrainInto(SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_composer is null || _ast is null)
                throw new InvalidOperationException("whole-source grammar composition is unavailable");
            Hash128 emitted = _composer.DrainInto(builder, witnessWeight, descentBitmap);
            if (emitted != _rootId)
                throw new InvalidOperationException("whole-source identity changed during staging");
            GrammarTagWitness.Emit(builder, _record.Utf8, _ast, _composer,
                _record.Modality, _sourceId, _trust);

            if (_rootId != default && _record.ExampleSegments is { Count: > 0 })
            {
                foreach (var seg in _record.ExampleSegments)
                {
                    if (seg.Length < 3) continue;
                    if (ContentTierSpine.TryStageIntoBuilder(
                            builder, System.Text.Encoding.UTF8.GetBytes(seg), _sourceId, out var segRoot))
                    {
                        builder.AddAttestation(NativeAttestation.Categorical(
                            segRoot, "HAS_EXAMPLE", _rootId, _sourceId, _trust));
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
                        builder.AddAttestation(NativeAttestation.Categorical(
                            kwRoot, "HAS_EXAMPLE", _rootId, _sourceId, _trust));
                    }
                }
            }

            if (_record.FileMetadata is not { } metadata)
                return _rootId;
            if (!string.Equals(metadata.Modality, _record.Modality, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "whole-source file metadata modality does not match its grammar recipe");
            FileIdentity file = FileEntity.Emit(builder, _sourceId, _root, metadata);
            if (file.ContentRootId != _rootId)
                throw new InvalidOperationException(
                    "whole-source file composition changed its grammar content identity");
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
                _composer = new GrammarRowComposer(_record.Utf8, _ast, _sourceId,
                    _record.Modality, GrammarCompositionMode.FullSource);
                _root = _composer.RootComponent();
                _rootId = _root.Id;
            }
            catch
            {
                _composer?.Dispose();
                _ast?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _composer?.Dispose();
            _ast?.Dispose();
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
        return IngestBatchPipeline.RunAsync(stream, handler, config, ct);
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
            bool excluded = !rootIsFile && VendoredPathFilter.IsVendoredOrBuildPath(full);
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
