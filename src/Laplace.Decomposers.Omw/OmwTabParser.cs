namespace Laplace.Decomposers.Omw;

using System;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Streaming parser for OMW wn-data-*.tab files. Skips comment lines
/// (start with #) and blank lines.
/// </summary>
public sealed class OmwTabParser
{
    public static IEnumerable<OmwTabRecord> Parse(string path)
    {
        using var reader = new StreamReader(path);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0 || line[0] == '#') { continue; }
            var parts = line.Split('\t');
            if (parts.Length < 3) { continue; }
            yield return new OmwTabRecord(parts[0], parts[1], parts[2]);
        }
    }
}
