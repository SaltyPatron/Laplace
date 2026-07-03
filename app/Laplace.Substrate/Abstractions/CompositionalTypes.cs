using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

public static class CompositionalTypes
{
    private static readonly Hash128[] Compositional =
    [
        TextEntityBuilder.CodepointTypeId,
        TextEntityBuilder.GraphemeTypeId,
        TextEntityBuilder.WordTypeId,
        TextEntityBuilder.SentenceTypeId,
        TextEntityBuilder.DocumentTypeId,
    ];

    public static bool IsCompositional(Hash128 typeId)
    {
        for (int i = 0; i < Compositional.Length; i++)
            if (typeId.EqualsBytewise(Compositional[i])) return true;
        return false;
    }
}
