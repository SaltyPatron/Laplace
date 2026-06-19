using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.ConceptNet;


internal static class ConceptNetUri
{
    public static bool TryParseLangAndTerm(
        ReadOnlySpan<byte> uri, out ReadOnlySpan<byte> lang, out ReadOnlySpan<byte> termUnderscored)
    {
        lang = default;
        termUnderscored = default;
        if (uri.Length < 5 || uri[0] != (byte)'/' || uri[1] != (byte)'c' || uri[2] != (byte)'/')
            return false;
        int i = 3;
        int langStart = i;
        while (i < uri.Length && uri[i] != (byte)'/') i++;
        if (i == langStart || i >= uri.Length) return false;
        lang = uri[langStart..i];
        i++;
        int termStart = i;
        int termEnd = uri[termStart..].IndexOf((byte)'/');
        termUnderscored = termEnd < 0 ? uri[termStart..] : uri[termStart..(termStart + termEnd)];
        return !termUnderscored.IsEmpty;
    }

    public static bool TryAppendTerm(
        SubstrateChangeBuilder b, ReadOnlySpan<byte> termUnderscored, Hash128 sourceId, out Hash128 rootId) =>
        ContentWitnessBatch.TryAppendUnderscoredToBuilder(b, termUnderscored, sourceId, out rootId);
}
