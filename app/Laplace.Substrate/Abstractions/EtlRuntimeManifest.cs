using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Runtime <see cref="ISourceManifest"/> built from an <see cref="EtlSource"/> —
/// Relations come from BootstrapRelations + NodeEdgeMap (no fake static ISeedSource).
/// </summary>
public sealed class EtlRuntimeManifest : ISourceManifest
{
    private readonly EtlSource _src;
    private readonly IReadOnlyList<string> _relations;

    public EtlRuntimeManifest(EtlSource src)
    {
        _src = src ?? throw new ArgumentNullException(nameof(src));
        _relations = CollectRelations(src);
    }

    public Hash128 SourceId => _src.SourceId;
    public string SourceName => _src.Name;
    public Hash128 TrustClass => _src.TrustClassId;
    public IReadOnlyList<string> Relations => _relations;
    public SourceLicense License => SourceLicense.Unknown;
    public IngestSourceProfile Profile => IngestSourceProfile.Wiktionary;
    public IReadOnlyList<string>? TypeNodeNames => null;

    private static IReadOnlyList<string> CollectRelations(EtlSource src)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        if (src.BootstrapRelations is not null)
        {
            foreach (var r in src.BootstrapRelations)
                if (seen.Add(r)) list.Add(r);
        }
        foreach (var rule in src.NodeEdgeMap)
            if (seen.Add(rule.RelationType)) list.Add(rule.RelationType);
        return list;
    }
}
