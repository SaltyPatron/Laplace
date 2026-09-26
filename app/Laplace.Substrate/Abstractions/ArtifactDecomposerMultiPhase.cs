using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Multi-phase provider whose selected units are physical artifacts. It uses the shared
/// multi-file ingest's resume and receipt semantics and worker pool: each selected artifact
/// is claimed once, its content identity keys resume and completion, an artifact with a
/// completion marker is skipped before its parser opens, and a completion marker is
/// emitted only after an uncapped run over the whole artifact.
/// </summary>
public abstract class ArtifactDecomposerMultiPhase : DecomposerMultiPhase, IDecomposer
{
    /// <summary>
    /// Re-declared here so the runner sees per-file completion instead of the default
    /// interface implementation inherited through <see cref="DecomposerMultiPhase"/>.
    /// </summary>
    public bool PerFileCompletion => true;
    bool IDecomposer.PerFileCompletion => true;

    /// <summary>
    /// Runs independent artifact phases on the shared bounded ingest worker pool. Uncapped
    /// runs schedule the largest artifacts first so no single file forms a serial tail;
    /// capped runs use one worker so MaxInputUnits names an exact deterministic prefix.
    /// </summary>
    protected async IAsyncEnumerable<SubstrateChange> RunArtifactPhasesAsync<TArtifact>(
        IReadOnlyList<TArtifact> artifacts,
        IDecomposerContext context,
        DecomposerOptions options,
        Func<TArtifact, IDecomposer> phaseFactory,
        Func<TArtifact, string> labelSelector,
        Func<TArtifact, string> pathSelector,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(phaseFactory);
        ArgumentNullException.ThrowIfNull(labelSelector);
        ArgumentNullException.ThrowIfNull(pathSelector);
        if (artifacts.Count == 0) yield break;

        IReadOnlyList<TArtifact> scheduled = options.MaxInputUnits > 0
            ? artifacts
            : artifacts
                .OrderByDescending(artifact =>
                    MultiFileScheduler.EstimateBytes(pathSelector(artifact)))
                .ThenBy(pathSelector, StringComparer.Ordinal)
                .ToArray();

        int workers = options.MaxInputUnits > 0
            ? 1
            : Math.Min(scheduled.Count, Math.Max(1, IngestTopology.Current.FileWorkers));

        IAsyncEnumerable<SubstrateChange> Execute(
            TArtifact artifact,
            CancellationToken token) =>
            RunPhaseAsync(
                phaseFactory(artifact),
                context,
                options,
                labelSelector(artifact),
                pathSelector(artifact),
                token);

        await foreach (SubstrateChange change in ParallelIngestWork.RunAsync(
                           scheduled, workers, Execute, ct).ConfigureAwait(false))
            yield return change;
    }

    protected readonly record struct ArtifactPhaseWork(
        IDecomposer Phase,
        string Label,
        string Path);

    /// <summary>
    /// Runs a dependency DAG as levels of peer artifacts separated by apply barriers: a
    /// level starts only after the shared writer has committed every change of the level
    /// before it. Within a level, artifacts run on the shared worker pool.
    /// </summary>
    protected async IAsyncEnumerable<SubstrateChange> RunArtifactDependencyLevelsAsync(
        IReadOnlyList<IReadOnlyList<ArtifactPhaseWork>> levels,
        IDecomposerContext context,
        DecomposerOptions options,
        string barrierPrefix,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(levels);
        int emittedLevel = 0;
        foreach (IReadOnlyList<ArtifactPhaseWork> level in levels)
        {
            ArtifactPhaseWork[] work = level
                .Where(static item => !string.IsNullOrWhiteSpace(item.Path))
                .ToArray();
            if (work.Length == 0) continue;

            if (emittedLevel > 0)
                await foreach (SubstrateChange barrier in ApplyBarrierAsync(
                                   $"{barrierPrefix}/level-{emittedLevel}", ct)
                                   .ConfigureAwait(false))
                    yield return barrier;

            await foreach (SubstrateChange change in RunArtifactPhasesAsync(
                               work,
                               context,
                               options,
                               static item => item.Phase,
                               static item => item.Label,
                               static item => item.Path,
                               ct).ConfigureAwait(false))
                yield return change;
            emittedLevel++;
        }
    }

    /// <summary>
    /// Runs one file-backed phase with the content-root resume and completion receipts of
    /// <see cref="IngestBatchPipeline.RunMultiFileAsync{TRecord}"/>.
    /// </summary>
    protected new async IAsyncEnumerable<SubstrateChange> RunPhaseAsync(
        IDecomposer phase,
        IDecomposerContext context,
        DecomposerOptions options,
        string fileLabel,
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string fullPath = Path.GetFullPath(path);
        if (context.HasArtifactGraph
            && !context.SelectedArtifacts.Any(artifact => string.Equals(
                Path.GetFullPath(artifact.Path), fullPath, StringComparison.Ordinal)))
        {
            // The manifest knows this file but did not admit it: it is neither opened nor
            // marked complete. An admitted file the source never reaches is caught by
            // DecomposerMultiPhase's closing selected-artifact check.
            yield break;
        }

        fileLabel = ClaimArtifact(context, fullPath, fileLabel);
        Hash128? fileRoot = IngestBatchPipeline.TryResolveFileIdentity(fullPath);
        IngestArtifact? artifact = context.HasArtifactGraph
            ? context.SelectedArtifacts.SingleOrDefault(selected => string.Equals(
                Path.GetFullPath(selected.Path), fullPath, StringComparison.Ordinal))
            : null;
        SourceArtifactIdentity? semanticArtifact = artifact is null
            ? null
            : SourceArtifactProvenance.Resolve(artifact, fileRoot);
        var observability = Laplace.Ingestion.IngestObservabilityScope.Current;

        if (fileRoot is { } root
            && context.Reader is { } reader
            && !options.ReObservePresent
            && await reader.HasFileCompletedAsync(
                    root, phase.SourceId, phase.LayerOrder, ct).ConfigureAwait(false))
        {
            Console.Error.WriteLine(
                $"INGEST_FILE_SKIPPED file={fileLabel} reason=marker-complete");
            observability.OnFileComposed(
                phase.SourceName, fileLabel, semanticArtifact?.ArtifactId,
                resumeFingerprint: fileRoot);
            yield return IngestBatchPipeline.BuildSkippedBoundary(phase.SourceId, fileLabel);
            yield break;
        }

        observability.OnFileStarted(
            phase.SourceName, fileLabel, IngestBatchPipeline.TryFileBytes(fullPath));

        long records = 0;
        long entities = 0;
        long physicalities = 0;
        long attestations = 0;

        if (artifact is not null && semanticArtifact is { } artifactIdentity)
        {
            SubstrateChange provenance = SourceArtifactProvenance.BuildChange(
                artifact, phase.SourceId, phase.TrustClassId, fileRoot)
                with { CountsAsUnit = false };
            provenance = IngestBatchPipeline.BindFileLabel(provenance, fileLabel);
            yield return provenance;
            entities += provenance.Entities.Length;
            physicalities += provenance.Physicalities.Length;
            attestations += provenance.Attestations.Length;
        }

        await foreach (var change in base.RunPhaseAsync(phase, context, options, ct))
        {
            records += change.Metadata.InputUnitsConsumed;
            entities += change.Entities.Length;
            physicalities += change.Physicalities.Length;
            attestations += change.Attestations.Length;
            if (!change.IntentStages.IsDefaultOrEmpty)
            {
                foreach (var stage in change.IntentStages)
                {
                    if (stage.IsInvalid) continue;
                    entities += stage.EntityCount;
                    physicalities += stage.PhysicalityCount;
                    attestations += stage.AttestationCount;
                }
            }
            SubstrateChange bound = IngestBatchPipeline.BindFileLabel(change, fileLabel);
            if (semanticArtifact is { } identity)
                bound = SourceArtifactProvenance.Bind(bound, identity.ArtifactId);
            yield return bound;
        }

        observability.OnFileComposed(
            phase.SourceName, fileLabel, semanticArtifact?.ArtifactId,
            records, entities, physicalities, attestations,
            resumeFingerprint: fileRoot);

        // A capped run does not prove the artifact reached EOF, so no completion marker is
        // emitted even when the cap lands on an artifact boundary.
        if (options.MaxInputUnits > 0)
        {
            yield return IngestBatchPipeline.BuildCancelledBoundary(phase.SourceId, fileLabel);
            yield break;
        }

        if (fileRoot is { } completedRoot)
        {
            var names = new HashSet<string>(CanonicalNamesForReadback, StringComparer.Ordinal)
            {
                $"substrate/source/{SourceName}/v1",
            };
            yield return IngestBatchPipeline.BuildFileCompletion(
                phase.SourceId, fileLabel, completedRoot, phase.LayerOrder, names);
            yield break;
        }

        // Without a content identity there is no completion claim: only the period
        // boundary is emitted, never a marker keyed by path or name.
        yield return IngestBatchPipeline.BuildPeriodBoundary(phase.SourceId, fileLabel);
    }
}
