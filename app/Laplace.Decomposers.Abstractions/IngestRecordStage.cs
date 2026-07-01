using System.Collections.Immutable;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public sealed class IngestRecordStage : IDisposable
{
    public IntentStage Stage { get; }

    public Hash128 RootId { get; }

    public ImmutableArray<AttestationRow> Attestations { get; }

    public IngestRecordStage(
        IntentStage stage, Hash128 rootId, ImmutableArray<AttestationRow> attestations)
    {
        Stage = stage ?? throw new ArgumentNullException(nameof(stage));
        RootId = rootId;
        Attestations = attestations;
    }

    public void Dispose() => Stage.Dispose();
}
