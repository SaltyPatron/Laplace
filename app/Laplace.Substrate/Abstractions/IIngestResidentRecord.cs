using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>Retained source payload bytes before native composition.</summary>
public interface IIngestResidentRecord
{
    long ResidentInputBytes { get; }
}

internal static class IngestRecordMemory
{
    internal static long Measure<TRecord>(TRecord record, IngestSourceProfile? profile)
    {
        long estimate = (profile ?? IngestSourceProfile.Default).UncomposedResidentBytesPerRecord;
        return record is IIngestResidentRecord resident
            ? Math.Max(estimate, resident.ResidentInputBytes)
            : estimate;
    }
}
