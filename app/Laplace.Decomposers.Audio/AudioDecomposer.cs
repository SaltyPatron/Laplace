using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Audio;

public sealed class AudioDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/AudioDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    public Hash128 SourceId     => Source;
    public string  SourceName   => "AudioDecomposer";
    public int     LayerOrder   => 12;
    public Hash128 TrustClassId => TrustClass;

    public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("Audio_Sample");
        boot.AddType("Audio_Frame");
        boot.AddType("Audio_Track");
        boot.AddType("Voice");
        boot.AddRelationType("IS_AT_SAMPLE");
        boot.AddRelationType("HAS_FREQUENCY_PEAK");
        boot.AddRelationType("HAS_VOICE");
        boot.AddRelationType("TRANSCRIBES_AS");
        return context.Writer.ApplyAsync(boot.Build(), ct);
    }

#pragma warning disable CS1998
    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield break;
    }
#pragma warning restore CS1998

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
