using System.Collections.Concurrent;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Centralizes the <c>InitializeAsync</c> bootstrap pattern that every decomposer repeats:
/// build a <see cref="BootstrapIntentBuilder"/>, declare type and relation-type nodes, write it,
/// and populate the readback vocabulary dictionary. Each call emits a single batch with all
/// highway node entities (types, relation types, POS, source) so they resolve to the same ids
/// everywhere — convergence is a consequence of going through one perfcache, not a convention.
/// </summary>
public static class SourceVocabularyBootstrap
{
    /// <summary>
    /// Run the standard bootstrap: create a <see cref="BootstrapIntentBuilder"/>, add type/
    /// relation-type nodes, write the resulting change, and optionally populate a readback-names
    /// dictionary. Returns the builder so callers can emit additional changes if needed.
    /// </summary>
    public static async Task<BootstrapIntentBuilder> RegisterAsync(
        IDecomposerContext context,
        Hash128 sourceId,
        string sourceName,
        Hash128 trustClassId,
        IEnumerable<string>? typeNodeNames = null,
        IEnumerable<string>? relationNodeNames = null,
        ConcurrentDictionary<string, byte>? readbackNames = null,
        CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(sourceId, sourceName, trustClassId);
        if (typeNodeNames is not null)
            foreach (var n in typeNodeNames) boot.AddType(n);
        if (relationNodeNames is not null)
            foreach (var n in relationNodeNames) boot.AddRelationType(n);
        await context.Writer.ApplyAsync(boot.Build(), ct);
        if (readbackNames is not null)
            foreach (var n in boot.CanonicalNames)
                readbackNames.TryAdd(n, 0);
        return boot;
    }
}
