namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Lets a decomposer account for a run that applied ZERO units, so the runner can tell an
/// expected empty run from a broken one.
///
/// WHY THIS EXISTS. <c>IngestRunner</c> throws when a source declares input units and then
/// applies none — the "silent no-op" guard, and it is right about the case it was written
/// for (a decomposer whose grammar does not match the file format, quietly reporting
/// success). But it fires on the DECLARED-vs-APPLIED gap alone, and three legitimate lanes
/// produce that gap:
///
///  - An IDEMPOTENT RE-INGEST. The chess PGN lane read 234 games and its novelty gate
///    proved all 234 already present, so nothing was applied. Measured: re-running
///    `ingest chess &lt;dir&gt;` over an already-ingested corpus FAILED with
///    "declares 235 input unit(s) but ingested 0". Re-ingest is supposed to be the safe
///    operation.
///  - A MARKER-GATED BACKFILL that has caught up. `chess-trajectory` and `chess-syzygy`
///    declare the whole recorded corpus as their denominator and stream only the rows
///    still missing their marker. A completed backfill therefore always applies zero and
///    always failed.
///  - AN UNSET OPTIONAL DEPENDENCY. `chess-syzygy` with no tablebase directory is a
///    documented clean no-op ("unattested is not attested-false"); it exited 1.
///
/// A decomposer that returns non-null here is asserting it KNOWS why it emitted nothing.
/// Returning null keeps the old behaviour, so a decomposer that read nothing and cannot
/// say why still fails the run — which is the case the guard exists to catch.
/// </summary>
public interface IIngestNoOpExplainer
{
    /// <param name="declaredInputUnits">What the inventory promised.</param>
    /// <returns>
    /// <c>(Status, Detail)</c> for an expected empty run — <c>Status</c> lands in the
    /// journal and INGEST_COMPLETE, <c>Detail</c> is logged for the operator. Null when
    /// the empty run is NOT expected, and the runner should fail as before.
    /// </returns>
    (string Status, string Detail)? ExplainEmptyRun(long declaredInputUnits);
}
