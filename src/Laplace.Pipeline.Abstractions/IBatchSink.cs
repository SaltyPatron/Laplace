namespace Laplace.Pipeline.Abstractions;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Bounded-channel buffered SQL writer. One channel per record kind, one
/// long-lived Npgsql connection per channel. Each drain task drains within
/// the same connection that COPY'd: TRUNCATE pg_temp staging → COPY ...
/// FROM STDIN BINARY → INSERT ... SELECT FROM staging ON CONFLICT DO NOTHING.
/// No persistent staging schema. Channel capacity bounded to apply
/// natural backpressure on producers.
/// </summary>
public interface IBatchSink : IAsyncDisposable
{
    Task FlushAsync(CancellationToken cancellationToken);

    /// <summary>Wait until all currently-buffered records have drained to disk.</summary>
    Task DrainAsync(CancellationToken cancellationToken);
}
