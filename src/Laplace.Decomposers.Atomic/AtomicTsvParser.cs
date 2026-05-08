namespace Laplace.Decomposers.Atomic;

using System.Collections.Generic;
using System.IO;

/// <summary>
/// Streaming parser for ATOMIC 2020 <c>{train,dev,test}.tsv</c> files. Yields
/// one <see cref="AtomicTripleRecord"/> per non-empty line. Comments (none in
/// the canonical release, but defensively skipped) start with <c>#</c>.
/// </summary>
public sealed class AtomicTsvParser
{
    public static IEnumerable<AtomicTripleRecord> Parse(string path)
    {
        using var reader = new StreamReader(path);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0 || line[0] == '#') { continue; }
            var parts = line.Split('\t');
            if (parts.Length < 3) { continue; }
            yield return new AtomicTripleRecord(parts[0], parts[1], parts[2]);
        }
    }
}
