using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Multi-phase source whose externally selected units are physical artifacts.
///
/// The generic multi-file lane already owns the file-resume contract. Multi-phase
/// sources need the same contract without inventing a second scheduler: each selected
/// artifact is claimed exactly once, its content identity owns resume/completion, a
/// marker-complete artifact true-skips before its parser opens, and a completion marker
/// is emitted only after an uncapped full artifact execution.
/// </summary>
public abstract class ArtifactDecomposerMultiPhase : DecomposerMultiPhase, IDecomposer
{
    /// <summary>
    /// Re-declare the interface on this derived base so the runner sees per-file
    /// completion rather than the default interface implementation inherited through
    /// <see cref="DecomposerMultiPhase"/>.
    /// </summary>
    public bool PerFileCompletion => true;
    bool IDecomposer.PerFileCompletion => true;

    /// <summary>
    /// File-backed phase using the same content-root resume and completion semantics as
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
            // The manifest explicitly knows this physical file but did not admit it.
            // Do not open it and do not claim completion. Conversely, any admitted file
            // that the source never reaches is caught by DecomposerMultiPhase's terminal
            // selected-artifact closure check.
            yield break;
        }

        fileLabel = ClaimArtifact(context, fullPath, fileLabel);
        Hash128? fileRoot = IngestBatchPipeline.TryResolveFileIdentity(fullPath);
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
                phase.SourceName, fileLabel, resumeFingerprint: fileRoot);
            yield return IngestBatchPipeline.BuildSkippedBoundary(phase.SourceId, fileLabel);
            yield break;
        }

        observability.OnFileStarted(
            phase.SourceName, fileLabel, IngestBatchPipeline.TryFileBytes(fullPath));

        long records = 0;
        long entities = 0;
        long physicalities = 0;
        long attestations = 0;

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
            yield return change;
        }

        observability.OnFileComposed(
            phase.SourceName, fileLabel, null,
            records, entities, physicalities, attestations,
            resumeFingerprint: fileRoot);

        // A diagnostic/capped run is not proof that the physical artifact reached EOF.
        // Never publish its durable completion marker even if the cap happened to land
        // on an artifact boundary; a later full run will safely re-observe that file.
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

        // No readable content identity means no resumable completion claim. Preserve the
        // accounting boundary, but do not manufacture a file marker from a path/name.
        yield return IngestBatchPipeline.BuildPeriodBoundary(phase.SourceId, fileLabel);
    }
}
