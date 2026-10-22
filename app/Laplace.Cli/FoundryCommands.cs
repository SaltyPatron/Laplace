using Laplace.Decomposers.Model;
namespace Laplace.Cli;
internal static class FoundryCommands
{
    public static Task<int> SynthesizeAsync(string[] args)=>FoundrySynthesis.SynthesizeAsync(args);
    internal static List<(string piece,float score,int type)> DeduplicateNativePieces(IReadOnlyList<(string piece,float score,int type)> pieces)
        =>FoundrySynthesis.DeduplicateNativePieces(pieces);
}
