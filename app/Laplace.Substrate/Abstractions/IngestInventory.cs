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

    public static IngestInventory? SingleFile(
        string unitType,
        string filePath,
        long maxInputUnits,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) return null;
        if (maxInputUnits > 0)
        {
            return new IngestInventory(
                unitType,
                maxInputUnits,
                [new IngestFileSpec(Path.GetFileName(filePath), filePath, maxInputUnits)]);
        }
        long n = EtlInventory.EstimateNewlineCount(filePath, ct);
        return new IngestInventory(unitType, n, [new IngestFileSpec(Path.GetFileName(filePath), filePath, n)]);
    }

    public static IngestInventory? FromFiles(
        string unitType,
        IReadOnlyList<string> paths,
        long maxInputUnits,
        CancellationToken ct = default)
    {
        if (paths.Count == 0) return null;
        if (maxInputUnits > 0)
        {
            var specs = paths.Select(p => new IngestFileSpec(Path.GetFileName(p), p, maxInputUnits)).ToList();
            return new IngestInventory(unitType, maxInputUnits, specs);
        }
        var files = new List<IngestFileSpec>();
        long total = 0;
        foreach (var p in paths)
        {
            long n = EtlInventory.EstimateNewlineCount(p, ct);
            files.Add(new IngestFileSpec(Path.GetFileName(p), p, n));
            total += n;
        }
        return new IngestInventory(unitType, total, files);
    }
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
        // Predicate callers need the decoded line; only they pay a string per line.
        if (includeLine is null) return CountNonEmptyLines(path, ct);
        long n = 0;
        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length == 0) continue;
            if (!includeLine(line)) continue;
            n++;
        }
        return n;
    }

    // Byte-level count of non-empty lines — identical to ReadLines + (Length > 0) without a
    // string per line. Terminators: \n, \r\n, lone \r; an unterminated final line counts;
    // a leading UTF-8 BOM is skipped (StreamReader strips it from the first line).
    private static long CountNonEmptyLines(string path, CancellationToken ct)
    {
        long n = 0;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, useAsync: false);
        var buf = new byte[1 << 20];
        bool hasContent = false, prevCr = false, first = true;
        int read;
        while ((read = fs.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            int i = 0;
            if (first)
            {
                first = false;
                if (read >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) i = 3;
            }
            for (; i < read; i++)
            {
                byte c = buf[i];
                if (c == (byte)'\r')
                {
                    if (hasContent) n++;
                    hasContent = false;
                    prevCr = true;
                }
                else if (c == (byte)'\n')
                {
                    if (!prevCr && hasContent) n++;
                    hasContent = false;
                    prevCr = false;
                }
                else
                {
                    hasContent = true;
                    prevCr = false;
                }
            }
        }
        if (hasContent) n++;
        return n;
    }

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

    // Byte-level equivalent of the former ReadLines pass: a sentence is open once a line
    // starts with a digit and contains a tab, and closes at a blank line. Valid CoNLL-U
    // token ids start with ASCII digits, so the byte-range digit test matches char.IsDigit.
    public static long CountConlluSentences(string path)
    {
        if (!File.Exists(path)) return 0;
        long n = 0;
        bool inSentence = false;
        bool lineHasContent = false, sawTab = false, prevCr = false, first = true;
        byte firstByte = 0;

        void EndLine()
        {
            if (!lineHasContent)
            {
                if (inSentence) { n++; inSentence = false; }
            }
            else if (firstByte >= (byte)'0' && firstByte <= (byte)'9' && sawTab)
            {
                inSentence = true;
            }
            lineHasContent = false;
            sawTab = false;
            firstByte = 0;
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, useAsync: false);
        var buf = new byte[1 << 20];
        int read;
        while ((read = fs.Read(buf, 0, buf.Length)) > 0)
        {
            int i = 0;
            if (first)
            {
                first = false;
                if (read >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) i = 3;
            }
            for (; i < read; i++)
            {
                byte c = buf[i];
                if (c == (byte)'\r')
                {
                    EndLine();
                    prevCr = true;
                }
                else if (c == (byte)'\n')
                {
                    if (prevCr) prevCr = false;
                    else EndLine();
                }
                else
                {
                    if (!lineHasContent) { lineHasContent = true; firstByte = c; }
                    if (c == (byte)'\t') sawTab = true;
                    prevCr = false;
                }
            }
        }
        if (lineHasContent) EndLine();
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
