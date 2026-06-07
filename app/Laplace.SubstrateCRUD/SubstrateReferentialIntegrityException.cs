namespace Laplace.SubstrateCRUD;

public sealed class SubstrateReferentialIntegrityException : Exception
{
    public int MissingCount { get; }

    public SubstrateReferentialIntegrityException(int missingCount, string firstMissingHex)
        : base($"referential proof failed: {missingCount} referenced entity id(s) neither staged in this batch "
             + $"nor present in laplace.entities (decomposer bug — first missing: {firstMissingHex}); "
             + "nothing was written")
    {
        MissingCount = missingCount;
    }
}
