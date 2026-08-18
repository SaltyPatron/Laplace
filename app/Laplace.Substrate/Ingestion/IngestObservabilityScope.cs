namespace Laplace.Ingestion;

/// <summary>
/// Ambient observability for the run in flight.
///
/// The file boundary lives in <c>IngestBatchPipeline</c>, below the generic
/// <c>Decomposer&lt;TRecord&gt;</c> driver. Threading an <c>IIngestObservability</c> value through
/// every handler and file worker would make an operational concern part of the vendor
/// contract even though only the shared boundary sites consume it. IngestRunner already
/// owns the observability instance and brackets the run, so the run brackets the ambient —
/// the same shape ContentLadderLedger.Begin()/End() uses for run-scoped state that the
/// pipeline reads without being handed it.
///
/// AsyncLocal, not a plain static: the file workers run concurrently inside the run's async
/// context and inherit the value, while a second runner in the same process (tests do this)
/// gets its own. Set is idempotent per run and Dispose restores the prior value, so nesting
/// cannot leak one run's ledger into another's.
/// </summary>
public static class IngestObservabilityScope
{
    private static readonly AsyncLocal<IIngestObservability?> _current = new();
    private static readonly AsyncLocal<string?> _sourceName = new();

    public static IIngestObservability Current => _current.Value ?? NoOpObservability.Instance;

    /// <summary>The run's source. The file boundary knows which FILE it is at and nothing
    /// about the run, so the source rides the scope rather than becoming an argument in the
    /// vendor handler contract.</summary>
    public static string SourceName => _sourceName.Value ?? "";

    public static IDisposable Begin(IIngestObservability observability, string sourceName)
    {
        var prior = _current.Value;
        var priorName = _sourceName.Value;
        _current.Value = observability;
        _sourceName.Value = sourceName;
        return new Restore(prior, priorName);
    }

    private sealed class Restore(IIngestObservability? prior, string? priorName) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _current.Value = prior;
            _sourceName.Value = priorName;
        }
    }
}
