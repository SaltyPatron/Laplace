namespace Laplace.Decomposers.Abstractions;


public sealed record IngestFileSpec(string Id, string Path, long InputUnits);

public sealed record IngestInventory(
    string UnitType,
    long TotalInputUnits,
    IReadOnlyList<IngestFileSpec> Files)
{
    public int FileCount => Files.Count;

    public static IngestInventory Single(long units, string unitType = "units") =>
        new(unitType, units, Array.Empty<IngestFileSpec>());
}

public interface IIngestInventoryProvider
{
    Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        CancellationToken ct = default);
}


public static class EtlInventory
{
    public static async Task<long> CountDataLinesAsync(
        string path,
        Func<string, bool>? includeLine = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(path)) return 0;
        long n = 0;
        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length == 0) continue;
            if (includeLine is not null && !includeLine(line)) continue;
            n++;
        }
        return n;
    }

    /// <summary>Fast newline count for progress bars — no per-line string allocation.</summary>
    public static long EstimateNewlineCount(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return 0;
        long n = 0;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, useAsync: false);
        var buf = new byte[1 << 20];
        int read;
        while ((read = fs.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < read; i++)
                if (buf[i] == (byte)'\n') n++;
        }
        return n;
    }

    public static long CountConlluSentences(string path)
    {
        if (!File.Exists(path)) return 0;
        long n = 0;
        bool inSentence = false;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0)
            {
                if (inSentence) { n++; inSentence = false; }
                continue;
            }
            if (line[0] == '#') continue;
            if (char.IsDigit(line[0]) && line.Contains('\t', StringComparison.Ordinal))
                inSentence = true;
        }
        if (inSentence) n++;
        return n;
    }

    public static async Task<IngestInventory> TatoebaAsync(
        string ecosystemPath, LanguageFilter? langs, CancellationToken ct)
    {
        var files = new List<IngestFileSpec>();
        string sentences = Path.Combine(ecosystemPath, "sentences.csv");
        if (File.Exists(sentences))
        {
            long n = await CountDataLinesAsync(sentences, line =>
            {
                var c = line.Split('\t');
                if (c.Length < 3) return false;
                return langs?.MatchesRaw(c[1].Trim()) != false;
            }, ct);
            files.Add(new("sentences", sentences, n));
        }

        string links = Path.Combine(ecosystemPath, "links.csv");
        if (File.Exists(links))
        {
            HashSet<long>? allowed = null;
            if (langs?.IsActive == true && File.Exists(sentences))
            {
                allowed = new HashSet<long>();
                await foreach (var line in File.ReadLinesAsync(sentences, ct))
                {
                    var c = line.Split('\t');
                    if (c.Length < 2 || !long.TryParse(c[0], out long sid)) continue;
                    if (langs.MatchesRaw(c[1].Trim())) allowed.Add(sid);
                }
            }

            long linkN = await CountDataLinesAsync(links, line =>
            {
                var c = line.Split('\t');
                if (c.Length < 2) return false;
                if (allowed is null) return true;
                return long.TryParse(c[0], out long a) && long.TryParse(c[1], out long b)
                       && allowed.Contains(a) && allowed.Contains(b);
            }, ct);
            files.Add(new("links", links, linkN));
        }

        long total = 0;
        foreach (var f in files) total += f.InputUnits;
        return new("records", total, files);
    }
}
