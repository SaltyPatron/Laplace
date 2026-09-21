using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Admission law for entity physicalization. Requiring a physical representation does
/// NOT mean the entity is canonical Content: ordered source structures, projections and
/// other governed records can owe the substrate a typed non-Content physicality too.
/// Content identity is separately constrained to the exact Merkle/collapse identity of
/// its decomposition trajectory.
/// </summary>
public static class EntityIdentityPolicy
{
    private static readonly HashSet<Hash128> PhysicalizedTypes =
    [
        EntityTypeRegistry.Byte,
        EntityTypeRegistry.Codepoint,
        EntityTypeRegistry.Grapheme,
        EntityTypeRegistry.Word,
        EntityTypeRegistry.Phrase,
        EntityTypeRegistry.Sentence,
        EntityTypeRegistry.Document,
        EntityTypeRegistry.Text,
        EntityTypeRegistry.Ngram,
        EntityTypeRegistry.Collection,
        EntityTypeRegistry.Range,
        EntityTypeRegistry.OpenSubtitlesAlignment,
        EntityTypeRegistry.OpenSubtitlesSequence,
        EntityTypeRegistry.FrameNetAnnotation,
        EntityTypeRegistry.Pixel,
        EntityTypeRegistry.Patch,
        EntityTypeRegistry.Region,
        EntityTypeRegistry.Image,
        EntityTypeRegistry.Sample,
        EntityTypeRegistry.Channel,
        EntityTypeRegistry.Window,
        EntityTypeRegistry.Track,
        EntityTypeRegistry.Frame,
        EntityTypeRegistry.Video,
        EntityTypeRegistry.OnsetSegment,
        EntityTypeRegistry.UdParse,
        EntityTypeRegistry.WiktionarySense,
        EntityTypeRegistry.PropBankRole,
        EntityTypeRegistry.VerbNetRole,
        EntityTypeRegistry.FrameNetFe,
        EntityTypeRegistry.PredicateMatrixRole,
        EntityTypeRegistry.EsoRole,
        EntityTypeRegistry.VerbNetMember,
        EntityTypeRegistry.VerbNetPredicate,
        // Source/catalog references are canonical content with semantic interpretations.
        // They therefore owe the same content physicality as the bytes that identify them.
        EntityTypeRegistry.SourceReference,
        EntityTypeRegistry.SourceVersion,
        EntityTypeRegistry.WordNetSynset,
        EntityTypeRegistry.WordNetSense,
        EntityTypeRegistry.PropBankRoleset,
        EntityTypeRegistry.VerbNetClass,
        EntityTypeRegistry.FrameNetLu,
        EntityTypeRegistry.WikidataItem,
        EntityTypeRegistry.PredicateMatrixPredicate,
        EntityTypeRegistry.PredicateMatrixAnnotationValue,
    ];

    /// <summary>
    /// True when this entity type owes the substrate some physical realization. The
    /// producer still owns the semantic physicality type: Content only for canonical
    /// content-addressed composition; ParseStructure/Projection/etc for other shapes.
    /// </summary>
    public static bool RequiresPhysicality(Hash128 typeId) =>
        PhysicalizedTypes.Contains(typeId);

    /// <summary>
    /// Complete immutable roster used by source-eviction repair to remove legacy
    /// source interpretations that violated today's physicality contract.
    /// </summary>
    public static IReadOnlyCollection<Hash128> PhysicalizedTypeIds => PhysicalizedTypes;
}
