using System.Collections.Concurrent;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public static class SourceVocabularyBootstrap
{
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
