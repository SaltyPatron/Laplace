using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Admission law for entity identity. Content and compositions require a physical
/// realization; governed vocabulary, source references, semantic concepts and
/// occurrence identities do not become content merely because their keys are strings.
/// </summary>
public static class EntityIdentityPolicy
{
    private static readonly HashSet<Hash128> PhysicalContentTypes =
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
    ];

    /// <summary>
    /// True when an entity of this type is content/composition and therefore owes the
    /// substrate a physicality. All other registered types are governed structural
    /// identities unless and until their admission policy is explicitly promoted here.
    /// </summary>
    public static bool RequiresPhysicality(Hash128 typeId) =>
        PhysicalContentTypes.Contains(typeId);
}
