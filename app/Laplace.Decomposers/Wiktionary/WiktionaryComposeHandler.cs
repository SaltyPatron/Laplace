using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Wiktionary;

/// <summary>
/// Wiktionary's content-tier builds belong on the compose fan
/// (<see cref="IIngestRecordHandler{TRecord}.CreateDeferredUnit"/>), not in serial
/// <see cref="IIngestDeferredUnit.DrainInto"/>. <see cref="DirectComposeHandler{T}"/>
/// ran <see cref="WiktionaryEmit.Emit"/> entirely in DrainInto — every gloss/example
/// ladder on one core per segment while the pinned compose workers built empty shells.
/// Same split ContentIngestHandler / RelationTriple already use.
/// </summary>
internal sealed class WiktionaryComposeHandler : IIngestRecordHandler<WiktionaryEntry>
{
    public IIngestDeferredUnit CreateDeferredUnit(WiktionaryEntry record) =>
        new WiktionaryDeferredUnit(record);

    public void WalkWitness(
        WiktionaryEntry record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit)
    {
    }

    /// <summary>
    /// Single-tree probe contract on purpose. Exposing every surface via
    /// <see cref="IMultiTreeIngestDeferredUnit"/> put ~26 trees/record into
    /// <c>BulkDescent.ProbeFlushBatchAsync</c> — tens of thousands of tier probes per
    /// flush before the runner saw the first yield (measured: 50s+ at 0 committed on the
    /// 21GB file with compose_workers=11). Trees are still built on the fan; only the
    /// pre-drain existence probe is limited to the headword (or skipped).
    /// </summary>
    private sealed class WiktionaryDeferredUnit : IIngestDeferredUnit
    {
        private readonly WiktionaryEntry _entry;
        private readonly List<string> _surfaces = new();
        private readonly List<TierTree> _trees = new();
        private readonly List<bool> _owned = new();
        private bool _disposed;

        public WiktionaryDeferredUnit(WiktionaryEntry entry)
        {
            _entry = entry;
            var unique = new HashSet<string>(StringComparer.Ordinal);
            var reusable = new HashSet<string>(StringComparer.Ordinal);
            WiktionaryEmit.CollectSurfaces(entry, unique, reusable);
            foreach (var surface in unique)
            {
                if (!WiktionarySurfaceTrees.TryBuild(
                        surface, reusable.Contains(surface), out var tree, out bool owned))
                    continue;
                _surfaces.Add(surface);
                _trees.Add(tree);
                _owned.Add(owned);
            }
        }

        // Null on purpose: a non-null probe still books one BulkDescent slot per record.
        // Apply-side verify already drops present rows; paying O(tiers) descent here before
        // the first yield was the 50s/0-committed stall on the 21GB file.
        public TierTree? TreeForBatchProbe => null;

        public long ResidentBytes
        {
            get
            {
                long bytes = 0;
                for (int i = 0; i < _trees.Count; i++)
                {
                    if (!_owned[i]) continue;
                    bytes = checked(bytes
                        + (long)_trees[i].Capacity * MemoryTopology.TierTreeResidentBytesPerCapacity);
                }
                return bytes;
            }
        }

        public Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct) =>
            Task.FromResult<byte[]?>(null);

        public Hash128 DrainInto(SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap)
        {
            var roots = new Dictionary<string, Hash128>(StringComparer.Ordinal);
            var coords = new Dictionary<string, WiktionarySurfaceTrees.RootCoord>(StringComparer.Ordinal);
            for (int i = 0; i < _trees.Count; i++)
            {
                if (WiktionarySurfaceTrees.TryEmit(
                        builder, _trees[i], WiktionaryDecomposer.Source, ReadOnlySpan<byte>.Empty, out var root)
                    && root != default)
                {
                    roots[_surfaces[i]] = root;
                    if (WiktionarySurfaceTrees.TryRootCoord(_trees[i], out var coord))
                        coords[_surfaces[i]] = coord;
                }
            }

            WiktionaryEmit.Emit(_entry, builder, roots, coords);
            return roots.TryGetValue(_entry.Word, out var wordRoot) ? wordRoot : default;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            for (int i = 0; i < _trees.Count; i++)
            {
                if (_owned[i])
                    _trees[i].Dispose();
            }
            _trees.Clear();
            _surfaces.Clear();
            _owned.Clear();
        }
    }
}
