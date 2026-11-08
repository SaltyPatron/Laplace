using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

public static class EtlInventory
{
    /// <summary>
    /// Files at or below this size get an exact newline count; larger files are sampled.
    /// Inventory is a progress denominator, not a correctness gate, and must not delay
    /// the first batch.
    /// </summary>
    internal static long ExactScanThresholdBytes =>
        IngestSizing.ResolveSequentialIoBufferBytes();

    /// <summary>Total sample budget across head/mid/tail windows for large-file estimates.</summary>
    internal static long SampleBudgetBytes =>
        IngestSizing.ResolveSequentialIoBufferBytes();

    /// <summary>
    /// Cap on total bytes read while building a multi-file inventory, so a long list of
    /// small files under <see cref="ExactScanThresholdBytes"/> cannot delay INGEST_START.
    /// </summary>
    internal static long MultiFileInventoryBudgetBytes =>
        IngestSizing.ResolveSequentialIoBufferBytes();

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
        using var fs = IngestIo.OpenSequentialRead(path);
        var buf = new byte[IngestSizing.ResolveSequentialIoBufferBytes()];
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

    /// <summary>
    /// Progress-denominator newline estimate. Exact for files ≤
    /// <see cref="ExactScanThresholdBytes"/>; head/mid/tail sample extrapolation above that.
    /// </summary>
    public static long EstimateNewlineCount(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return 0;
        long size = new FileInfo(path).Length;
        if (size == 0) return 0;
        if (size <= ExactScanThresholdBytes)
            return CountNewlinesExact(path, size, ct);
        return EstimateByByteSample(path, size, SampleBudgetBytes, CountNewlinesInBuffer, ct);
    }

    /// <summary>
    /// CoNLL-U sentence estimate for inventory. Exact under the threshold; sampled above.
    /// </summary>
    public static long EstimateConlluSentences(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return 0;
        long size = new FileInfo(path).Length;
        if (size == 0) return 0;
        if (size <= ExactScanThresholdBytes)
            return CountConlluSentences(path);
        // Sample windows and scale by file size using newline density as the byte ruler —
        // sentence boundaries track blank lines roughly with file length on UD treebanks.
        long sampleSentences = 0;
        long sampleBytes = 0;
        foreach (var (offset, len) in SampleWindows(size, SampleBudgetBytes))
        {
            ct.ThrowIfCancellationRequested();
            var buf = ReadWindow(path, offset, len);
            sampleBytes += buf.Length;
            sampleSentences += CountConlluSentencesInBuffer(buf);
        }
        if (sampleBytes == 0) return 0;
        return Math.Max(1, (long)Math.Round(sampleSentences * (double)size / sampleBytes));
    }

    /// <summary>
    /// Per-file newline estimates under a shared <see cref="MultiFileInventoryBudgetBytes"/>
    /// read budget. Exact while cumulative size fits; then density extrapolation / samples.
    /// </summary>
    public static long[] EstimateNewlineCounts(
        IReadOnlyList<string> paths, CancellationToken ct = default) =>
        EstimateMultiFileUnits(paths, CountNewlinesInBuffer, CountNewlinesExact, ct);

    /// <summary>
    /// Per-file CoNLL-U sentence estimates under the same multi-file inventory budget.
    /// </summary>
    public static long[] EstimateConlluSentenceCounts(
        IReadOnlyList<string> paths, CancellationToken ct = default) =>
        EstimateMultiFileUnits(
            paths,
            CountConlluSentencesInBuffer,
            static (path, size, token) => CountConlluSentences(path),
            ct);

    private static long[] EstimateMultiFileUnits(
        IReadOnlyList<string> paths,
        Func<ReadOnlySpan<byte>, long> countInBuffer,
        Func<string, long, CancellationToken, long> exactCount,
        CancellationToken ct)
    {
        var units = new long[paths.Count];
        if (paths.Count == 0) return units;

        var sizes = new long[paths.Count];
        long totalBytes = 0;
        for (int i = 0; i < paths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(paths[i])) { sizes[i] = 0; continue; }
            sizes[i] = new FileInfo(paths[i]).Length;
            totalBytes += sizes[i];
        }
        if (totalBytes == 0) return units;

        // Whole corpus fits the single-file exact threshold → exact every file.
        if (totalBytes <= ExactScanThresholdBytes)
        {
            for (int i = 0; i < paths.Count; i++)
            {
                if (sizes[i] == 0) continue;
                units[i] = exactCount(paths[i], sizes[i], ct);
            }
            return units;
        }

        // Exact the largest files until the multi-file budget is spent; extrapolate the rest
        // from measured density so thousands of small tabs don't each pay a full open+scan.
        var order = Enumerable.Range(0, paths.Count)
            .Where(i => sizes[i] > 0)
            .OrderByDescending(i => sizes[i])
            .ToArray();

        long budgetLeft = MultiFileInventoryBudgetBytes;
        long measuredHits = 0;
        long measuredBytes = 0;

        foreach (int i in order)
        {
            ct.ThrowIfCancellationRequested();
            long size = sizes[i];
            if (size <= budgetLeft && size <= ExactScanThresholdBytes)
            {
                long n = exactCount(paths[i], size, ct);
                units[i] = n;
                measuredHits += n;
                measuredBytes += size;
                budgetLeft -= size;
                continue;
            }

            if (budgetLeft >= MemoryTopology.CopyStartupBytesPerConnection)
            {
                long fileBudget = Math.Min(budgetLeft, SampleBudgetBytes);
                var (n, hits, sampled) = SampleFile(paths[i], size, fileBudget, countInBuffer, ct);
                units[i] = n;
                measuredHits += hits;
                measuredBytes += sampled;
                budgetLeft -= sampled;
                continue;
            }

            // Budget exhausted: scale unread files by density from measured files.
            if (measuredBytes > 0)
            {
                units[i] = Math.Max(1, (long)Math.Round(size * (double)measuredHits / measuredBytes));
            }
            else
            {
                // No measurements at all (degenerate) — one cheap head sample of this file.
                long head = Math.Min(
                    IngestSizing.ResolveSequentialIoBufferBytes(), size);
                var (n, hits, sampled) = SampleFile(paths[i], size, head, countInBuffer, ct);
                units[i] = n;
                measuredHits += hits;
                measuredBytes += sampled;
            }
        }

        return units;
    }

    /// <summary>Exact newline count for background denominator refinement.</summary>
    internal static long CountNewlinesExactFor(string path, CancellationToken ct) =>
        CountNewlinesExact(path, new FileInfo(path).Length, ct);

    /// <summary>Exact CoNLL-U sentence count for background denominator refinement.</summary>
    internal static long CountConlluSentencesFor(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return CountConlluSentences(path);
    }

    private static long CountNewlinesExact(string path, long size, CancellationToken ct)
    {
        long n = 0;
        using var fs = IngestIo.OpenSequentialRead(path);
        var buf = new byte[IngestSizing.ResolveSequentialIoBufferBytes()];
        int read;
        long remaining = size;
        while (remaining > 0 && (read = fs.Read(buf, 0, (int)Math.Min(buf.Length, remaining))) > 0)
        {
            ct.ThrowIfCancellationRequested();
            n += CountNewlinesInBuffer(buf.AsSpan(0, read));
            remaining -= read;
        }
        return n;
    }

    private static long CountNewlinesInBuffer(ReadOnlySpan<byte> buf)
    {
        long n = 0;
        for (int i = 0; i < buf.Length; i++)
            if (buf[i] == (byte)'\n') n++;
        return n;
    }

    private static long EstimateByByteSample(
        string path, long size, long budget,
        Func<ReadOnlySpan<byte>, long> countInBuffer,
        CancellationToken ct)
    {
        var (estimate, _, _) = SampleFile(path, size, budget, countInBuffer, ct);
        return estimate;
    }

    private static (long Estimate, long Hits, long SampleBytes) SampleFile(
        string path, long size, long budget,
        Func<ReadOnlySpan<byte>, long> countInBuffer,
        CancellationToken ct)
    {
        long hits = 0;
        long sampleBytes = 0;
        foreach (var (offset, len) in SampleWindows(size, budget))
        {
            ct.ThrowIfCancellationRequested();
            var buf = ReadWindow(path, offset, len);
            sampleBytes += buf.Length;
            hits += countInBuffer(buf);
        }
        if (sampleBytes == 0) return (0, 0, 0);
        long estimate = Math.Max(1, (long)Math.Round(hits * (double)size / sampleBytes));
        return (estimate, hits, sampleBytes);
    }

    private static IEnumerable<(long Offset, int Length)> SampleWindows(long size, long budget)
    {
        int window = (int)Math.Min(budget / 3, int.MaxValue);
        if (window < MemoryTopology.CopyStartupBytesPerConnection)
            window = (int)Math.Min(budget, MemoryTopology.CopyStartupBytesPerConnection);
        if (window > size) { yield return (0, (int)size); yield break; }

        yield return (0, window);
        long mid = Math.Max(0, (size / 2) - (window / 2));
        if (mid > 0 && mid + window <= size) yield return (mid, window);
        long tail = Math.Max(0, size - window);
        if (tail > mid) yield return (tail, window);
    }

    private static byte[] ReadWindow(string path, long offset, int length)
    {
        using var fs = IngestIo.OpenSequentialRead(path);
        fs.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[length];
        int got = 0;
        while (got < length)
        {
            int r = fs.Read(buf, got, length - got);
            if (r == 0) break;
            got += r;
        }
        if (got == length) return buf;
        if (got == 0) return Array.Empty<byte>();
        var trimmed = new byte[got];
        Buffer.BlockCopy(buf, 0, trimmed, 0, got);
        return trimmed;
    }

    // Byte-level scan: a sentence is open once a line starts with a digit and contains a
    // tab, and closes at a blank line. CoNLL-U token ids start with ASCII digits, so a
    // byte-range digit test suffices.
    public static long CountConlluSentences(string path)
    {
        if (!File.Exists(path)) return 0;
        using var fs = IngestIo.OpenSequentialRead(path);
        var buf = new byte[IngestSizing.ResolveSequentialIoBufferBytes()];
        var state = new ConlluCountState();
        int read;
        while ((read = fs.Read(buf, 0, buf.Length)) > 0)
            state.Feed(buf.AsSpan(0, read));
        return state.Finish();
    }

    private static long CountConlluSentencesInBuffer(ReadOnlySpan<byte> buf)
    {
        var state = new ConlluCountState();
        state.Feed(buf);
        return state.Finish();
    }

    private struct ConlluCountState
    {
        private long _n;
        private bool _inSentence;
        private bool _lineHasContent, _sawTab, _prevCr, _bomChecked;
        private byte _firstByte;

        public void Feed(ReadOnlySpan<byte> buf)
        {
            int i = 0;
            if (!_bomChecked)
            {
                _bomChecked = true;
                if (buf.Length >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) i = 3;
            }
            for (; i < buf.Length; i++)
            {
                byte c = buf[i];
                if (c == (byte)'\r')
                {
                    EndLine();
                    _prevCr = true;
                }
                else if (c == (byte)'\n')
                {
                    if (_prevCr) _prevCr = false;
                    else EndLine();
                }
                else
                {
                    if (!_lineHasContent) { _lineHasContent = true; _firstByte = c; }
                    if (c == (byte)'\t') _sawTab = true;
                    _prevCr = false;
                }
            }
        }

        public long Finish()
        {
            if (_lineHasContent) EndLine();
            if (_inSentence) _n++;
            return _n;
        }

        private void EndLine()
        {
            if (!_lineHasContent)
            {
                if (_inSentence) { _n++; _inSentence = false; }
            }
            else if (_firstByte >= (byte)'0' && _firstByte <= (byte)'9' && _sawTab)
            {
                _inSentence = true;
            }
            _lineHasContent = false;
            _sawTab = false;
            _firstByte = 0;
        }
    }

    /// <summary>
    /// Inventory for Tatoeba from sample estimates only. An active language filter scales
    /// the unfiltered estimate by a sampled match rate.
    /// </summary>
    public static Task<IngestInventory> TatoebaAsync(
        string ecosystemPath, LanguageFilter? langs, CancellationToken ct)
    {
        var files = new List<IngestFileSpec>();
        string sentences = Path.Combine(ecosystemPath, "sentences.csv");
        if (File.Exists(sentences))
        {
            long n = EstimateNewlineCount(sentences, ct);
            if (langs is { IsActive: true })
                n = ScaleBySampledLineRate(sentences, n, line =>
                {
                    var c = line.Split('\t');
                    return c.Length >= 3 && langs.MatchesRaw(c[1].Trim());
                }, ct);
            files.Add(new("sentences", sentences, n));
        }

        string links = Path.Combine(ecosystemPath, "links.csv");
        if (File.Exists(links))
        {
            // Links filtered by sentence-id allow-set cannot be exact without a full
            // sentences pass — inventory must not pay that. Unfiltered estimate; when a
            // language filter is active, reuse the sentence match-rate as a coarse scale.
            long linkN = EstimateNewlineCount(links, ct);
            if (langs is { IsActive: true } && File.Exists(sentences))
            {
                long allSent = EstimateNewlineCount(sentences, ct);
                long matched = files.Count > 0 ? files[0].InputUnits : allSent;
                if (allSent > 0)
                    linkN = Math.Max(1, (long)Math.Round(linkN * (double)matched / allSent));
            }
            files.Add(new("links", links, linkN));
        }

        long total = 0;
        foreach (var f in files) total += f.InputUnits;
        return Task.FromResult(new IngestInventory("records", total, files));
    }

    /// <summary>
    /// Scale an unfiltered unit estimate by the fraction of lines matching
    /// <paramref name="include"/> over a bounded head sample (≤16 MiB / 200k lines).
    /// </summary>
    private static long ScaleBySampledLineRate(
        string path, long unfilteredEstimate, Func<string, bool> include, CancellationToken ct)
    {
        if (unfilteredEstimate <= 0) return 0;
        const int maxLines = 200_000;
        const long maxBytes = 16L << 20;
        long seen = 0, matched = 0, bytes = 0;
        foreach (var line in File.ReadLines(path))
        {
            ct.ThrowIfCancellationRequested();
            bytes += line.Length + 1;
            seen++;
            if (line.Length > 0 && include(line)) matched++;
            if (seen >= maxLines || bytes >= maxBytes) break;
        }
        if (seen == 0) return unfilteredEstimate;
        return Math.Max(1, (long)Math.Round(unfilteredEstimate * (double)matched / seen));
    }

    /// <summary>
    /// Byte-scan estimate of PGN games (<c>[Event </c> at line start). Exact under the
    /// threshold; sampled above.
    /// </summary>
    public static long EstimatePgnGameCount(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return 0;
        long size = new FileInfo(path).Length;
        if (size == 0) return 0;
        ReadOnlySpan<byte> marker = "[Event "u8;
        if (size <= ExactScanThresholdBytes)
            return CountLineStartMarkerExact(path, marker, ct);
        long hits = 0;
        long sampleBytes = 0;
        foreach (var (offset, len) in SampleWindows(size, SampleBudgetBytes))
        {
            ct.ThrowIfCancellationRequested();
            var buf = ReadWindow(path, offset, len);
            sampleBytes += buf.Length;
            // Mid/tail windows are not guaranteed at a line boundary; count only markers
            // preceded by '\n' (or offset 0 for the head window).
            hits += CountLineStartMarkerInBuffer(buf, marker, offset == 0);
        }
        if (sampleBytes == 0) return 0;
        return Math.Max(1, (long)Math.Round(hits * (double)size / sampleBytes));
    }

    private static long CountLineStartMarkerExact(
        string path, ReadOnlySpan<byte> marker, CancellationToken ct)
    {
        using var fs = IngestIo.OpenSequentialRead(path);
        var buf = new byte[IngestSizing.ResolveSequentialIoBufferBytes()];
        long n = 0;
        bool atLineStart = true;
        // Carry for marker spanning buffer boundaries.
        int markProgress = 0;
        int read;
        while ((read = fs.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < read; i++)
            {
                byte c = buf[i];
                if (c == (byte)'\n')
                {
                    atLineStart = true;
                    markProgress = 0;
                    continue;
                }
                if (c == (byte)'\r') continue;
                if (!atLineStart && markProgress == 0) continue;
                if (c == marker[markProgress])
                {
                    markProgress++;
                    if (markProgress == marker.Length)
                    {
                        n++;
                        markProgress = 0;
                        atLineStart = false;
                    }
                }
                else
                {
                    markProgress = 0;
                    atLineStart = false;
                }
            }
        }
        return n;
    }

    private static long CountLineStartMarkerInBuffer(
        ReadOnlySpan<byte> buf, ReadOnlySpan<byte> marker, bool headWindow)
    {
        long n = 0;
        bool atLineStart = headWindow;
        int markProgress = 0;
        for (int i = 0; i < buf.Length; i++)
        {
            byte c = buf[i];
            if (c == (byte)'\n')
            {
                atLineStart = true;
                markProgress = 0;
                continue;
            }
            if (c == (byte)'\r') continue;
            if (!atLineStart && markProgress == 0) continue;
            if (c == marker[markProgress])
            {
                markProgress++;
                if (markProgress == marker.Length)
                {
                    n++;
                    markProgress = 0;
                    atLineStart = false;
                }
            }
            else
            {
                markProgress = 0;
                atLineStart = false;
            }
        }
        return n;
    }
}
