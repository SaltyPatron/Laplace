using System.Collections.Immutable;

namespace Laplace.Engine.Core;

/// <summary>Exact source owner of an appended native physicality row interval.</summary>
public readonly record struct PhysicalitySourceRange(int FirstRow, int RowCount, Hash128 SourceId);

public sealed partial class IntentStage
{
    private readonly List<PhysicalitySourceRange> _physicalitySourceRanges = new();

    /// <summary>Producer metadata in native row order, captured before filtering or partitioning.
    /// Uncovered rows have no declared owner; callers must never fill them from an entity ID.</summary>
    public ImmutableArray<PhysicalitySourceRange> PhysicalitySourceRanges =>
        _physicalitySourceRanges.ToImmutableArray();

    public void RecordPhysicalitySourceRange(int firstRow, int rowCount, Hash128 sourceId)
    {
        RecordPhysicalitySourceRanges([new(firstRow, rowCount, sourceId)]);
    }

    /// <summary>Validate one bulk owner result against one native row-count observation.</summary>
    public void RecordPhysicalitySourceRanges(ReadOnlySpan<PhysicalitySourceRange> ranges)
        => RecordPhysicalitySourceRanges(ranges, PhysicalityCount);

    private void RecordPhysicalitySourceRanges(ReadOnlySpan<PhysicalitySourceRange> ranges, int actualCount)
    {
        int previousEnd = _physicalitySourceRanges.Count == 0 ? 0 : checked(
            _physicalitySourceRanges[^1].FirstRow + _physicalitySourceRanges[^1].RowCount);
        foreach (var range in ranges)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(range.FirstRow);
            ArgumentOutOfRangeException.ThrowIfNegative(range.RowCount);
            int end = checked(range.FirstRow + range.RowCount);
            if (end > actualCount)
                throw new ArgumentOutOfRangeException(nameof(ranges), "source range exceeds actual native physicality rows");
            if (range.RowCount == 0) continue;
            if (range.FirstRow < previousEnd)
                throw new InvalidOperationException("physicality source ranges overlap or are out of native row order");
            previousEnd = end;
        }
        foreach (var range in ranges)
            AppendPhysicalitySourceRange(range.FirstRow, range.RowCount, range.SourceId);
    }

    private void AppendPhysicalitySourceRange(int firstRow, int rowCount, Hash128 sourceId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstRow);
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        if (rowCount == 0) return;
        if (_physicalitySourceRanges.Count > 0)
        {
            var previous = _physicalitySourceRanges[^1];
            int previousEnd = checked(previous.FirstRow + previous.RowCount);
            if (firstRow < previousEnd)
                throw new InvalidOperationException("physicality source ranges overlap or are out of native row order");
            if (firstRow == previousEnd && previous.SourceId == sourceId)
            {
                _physicalitySourceRanges[^1] = previous with { RowCount = checked(previous.RowCount + rowCount) };
                return;
            }
        }
        _physicalitySourceRanges.Add(new(firstRow, rowCount, sourceId));
    }

    public void RecordPhysicalitySourceSince(int firstRow, Hash128 sourceId)
    {
        int actualCount = PhysicalityCount;
        RecordPhysicalitySourceRanges([new(firstRow, checked(actualCount - firstRow), sourceId)], actualCount);
    }
}
