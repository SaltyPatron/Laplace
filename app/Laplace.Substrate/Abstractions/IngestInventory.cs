using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;


public enum IngestArtifactDisposition
{
    Admitted,
    EquivalentPackaging,
    Superseded,
    ExcludedWithReason,
    Unsupported,
    Absent,
}

public sealed record IngestArtifact(
    string Source,
    string Release,
    string Artifact,
    string RelativePath,
    string Path,
    IngestArtifactDisposition Disposition,
    string UpstreamUrl,
    string FetchedAtUtc,
    long? Bytes,
    string Sha256,
    string UpstreamChecksum,
    string MediaType,
    string License,
    string Citation,
    string Language,
    string Split,
    string AnnotationOrigin,
    string Notes,
    string? JournalLabel = null,
    DateTimeOffset? ModifiedAt = null)
{
    public string Id => $"{Source}/{Release}/{Artifact}";
    public string FileLabel => JournalLabel ?? Id;
    public bool IsSelected => Disposition == IngestArtifactDisposition.Admitted;

    public string DispositionName => Disposition switch
    {
        IngestArtifactDisposition.Admitted => "admitted",
        IngestArtifactDisposition.EquivalentPackaging => "equivalent-packaging",
        IngestArtifactDisposition.Superseded => "superseded",
        IngestArtifactDisposition.ExcludedWithReason => "excluded-with-reason",
        IngestArtifactDisposition.Unsupported => "unsupported-with-why-not",
        IngestArtifactDisposition.Absent => "absent",
        _ => throw new InvalidOperationException($"Unknown artifact disposition {Disposition}."),
    };
}

public sealed class IngestArtifactGraph
{
    private static readonly string[] ManifestColumns =
    [
        "source", "release", "artifact", "relative_path", "disposition", "upstream_url",
        "fetched_at_utc", "bytes", "sha256", "upstream_checksum", "media_type", "license",
        "citation", "language", "split", "annotation_origin", "notes",
    ];

    public IngestArtifactGraph(IEnumerable<IngestArtifact> artifacts)
    {
        Artifacts = artifacts.ToArray();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var physicalPaths = new HashSet<string>(StringComparer.Ordinal);
        var journalLabels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in Artifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact.Source)
                || string.IsNullOrWhiteSpace(artifact.Release)
                || string.IsNullOrWhiteSpace(artifact.Artifact))
                throw new InvalidOperationException(
                    "Artifact source, release, and artifact identity components are required.");
            if (!identities.Add(artifact.Id))
                throw new InvalidOperationException($"Duplicate artifact identity '{artifact.Id}'.");
            string fullPath = Path.GetFullPath(artifact.Path);
            if (!physicalPaths.Add(fullPath))
                throw new InvalidOperationException(
                    $"Physical artifact path is declared more than once: '{fullPath}'.");
            if (artifact.Bytes < 0)
                throw new InvalidOperationException(
                    $"Artifact '{artifact.Id}' has a negative byte count.");
            if (string.IsNullOrWhiteSpace(artifact.FileLabel))
                throw new InvalidOperationException(
                    $"Artifact '{artifact.Id}' has an empty journal label.");
            if (!journalLabels.Add(artifact.FileLabel))
                throw new InvalidOperationException(
                    $"Artifact journal label is declared more than once: '{artifact.FileLabel}'.");
            if (!artifact.IsSelected && string.IsNullOrWhiteSpace(artifact.Notes))
                throw new InvalidOperationException(
                    $"Artifact '{artifact.Id}' has disposition {artifact.Disposition} without a reason.");
        }
    }

    public IReadOnlyList<IngestArtifact> Artifacts { get; }

    public IReadOnlyList<IngestArtifact> Selected =>
        Artifacts.Where(static artifact => artifact.IsSelected).ToArray();

    public static IngestArtifactGraph Load(string sourceRoot)
    {
        string manifestPath = Path.Combine(sourceRoot, "MANIFEST.tsv");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("Source estate has no MANIFEST.tsv.", manifestPath);

        using var reader = new StreamReader(manifestPath);
        string[] header = (reader.ReadLine() ?? "").Split('\t');
        if (!header.SequenceEqual(ManifestColumns, StringComparer.Ordinal))
            throw new InvalidDataException(
                $"{manifestPath} header must be exactly: {string.Join('\t', ManifestColumns)}");

        var artifacts = new List<IngestArtifact>();
        int lineNumber = 1;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (line.Length == 0) continue;
            string[] fields = line.Split('\t');
            if (fields.Length != ManifestColumns.Length)
                throw new InvalidDataException(
                    $"{manifestPath}:{lineNumber} has {fields.Length} fields; expected {ManifestColumns.Length}.");

            string relativePath = NormalizeRelativePath(fields[3], manifestPath, lineNumber);
            var disposition = ParseDisposition(fields[4], manifestPath, lineNumber);
            long? bytes = null;
            if (fields[7].Length > 0)
            {
                if (!long.TryParse(
                        fields[7], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsedBytes))
                    throw new InvalidDataException($"{manifestPath}:{lineNumber} has invalid bytes '{fields[7]}'.");
                bytes = parsedBytes;
            }

            artifacts.Add(new IngestArtifact(
                fields[0], fields[1], fields[2], relativePath,
                Path.GetFullPath(Path.Combine(sourceRoot, relativePath)), disposition,
                fields[5], fields[6], bytes, fields[8], fields[9], fields[10], fields[11],
                fields[12], fields[13], fields[14], fields[15], fields[16]));
        }

        var graph = new IngestArtifactGraph(artifacts);
        graph.ValidatePhysicalEstate(sourceRoot, manifestPath);
        return graph;
    }

    private static string NormalizeRelativePath(string path, string manifestPath, int lineNumber)
    {
        string normalized = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized)
            || Path.IsPathRooted(normalized)
            || normalized.Split('/').Contains("..", StringComparer.Ordinal))
            throw new InvalidDataException(
                $"{manifestPath}:{lineNumber} has invalid relative_path '{path}'.");
        return normalized;
    }

    private static IngestArtifactDisposition ParseDisposition(
        string value, string manifestPath, int lineNumber) => value switch
    {
        "admitted" => IngestArtifactDisposition.Admitted,
        "equivalent-packaging" => IngestArtifactDisposition.EquivalentPackaging,
        "superseded" => IngestArtifactDisposition.Superseded,
        "excluded-with-reason" => IngestArtifactDisposition.ExcludedWithReason,
        "unsupported-with-why-not" => IngestArtifactDisposition.Unsupported,
        "absent" => IngestArtifactDisposition.Absent,
        _ => throw new InvalidDataException(
            $"{manifestPath}:{lineNumber} has unknown disposition '{value}'."),
    };

    private void ValidatePhysicalEstate(string sourceRoot, string manifestPath)
    {
        var declared = Artifacts
            .Where(static artifact => artifact.Disposition != IngestArtifactDisposition.Absent)
            .Select(static artifact => artifact.RelativePath)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var artifact in Artifacts.Where(static artifact => artifact.IsSelected))
        {
            if (!File.Exists(artifact.Path))
                throw new InvalidDataException(
                    $"Admitted artifact '{artifact.Id}' is absent at '{artifact.Path}'.");
            if (artifact.Bytes is { } expectedBytes && new FileInfo(artifact.Path).Length != expectedBytes)
                throw new InvalidDataException(
                    $"Admitted artifact '{artifact.Id}' byte count differs from MANIFEST.tsv.");
        }

        var controls = new HashSet<string>(StringComparer.Ordinal)
        {
            "MANIFEST.tsv", "MANIFEST.sha256", "PROVENANCE.md",
        };
        foreach (string path in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');
            if (!controls.Contains(relative) && !declared.Contains(relative))
                throw new InvalidDataException(
                    $"Unmanifested physical artifact '{relative}' under '{sourceRoot}'.");
        }
    }

    public IngestInventory? ToFileInventory(
        string unitType,
        bool tracksFileCompletion = true)
    {
        var selected = Selected;
        if (selected.Count == 0) return null;
        var files = selected
            .Select(static artifact => new IngestFileSpec(artifact.FileLabel, artifact.Path, 0))
            .ToArray();
        return new IngestInventory(unitType, 0, files, tracksFileCompletion);
    }

    public void ValidateInventory(IngestInventory inventory)
    {
        var selectedPaths = Selected
            .Select(static artifact => artifact.Path)
            .ToHashSet(StringComparer.Ordinal);
        var inventoryPaths = inventory.Files
            .Select(static file => Path.GetFullPath(file.Path))
            .ToHashSet(StringComparer.Ordinal);
        if (selectedPaths.SetEquals(inventoryPaths)) return;

        string omitted = string.Join(", ", selectedPaths.Except(inventoryPaths).OrderBy(static path => path));
        string undeclared = string.Join(", ", inventoryPaths.Except(selectedPaths).OrderBy(static path => path));
        throw new InvalidOperationException(
            $"Decomposer inventory differs from MANIFEST.tsv selected artifacts. "
            + $"Omitted: [{omitted}]. Undeclared: [{undeclared}].");
    }
}

public sealed record IngestFileSpec(string Id, string Path, long InputUnits);

public sealed record IngestInventory(
    string UnitType,
    long TotalInputUnits,
    IReadOnlyList<IngestFileSpec> Files,
    /// <summary>
    /// When true, <see cref="FileCount"/> feeds the journal's <c>files_total</c> and the
    /// runner requires <c>files_done == files_total</c> for status <c>ok</c>. Only an
    /// ingest that writes a <c>period-boundary/</c> (or <c>file-failed/</c>) receipt per file
    /// may set it; a stream that lists files only for unit estimates leaves it false.
    /// </summary>
    bool TracksFileCompletion = false)
{
    /// <summary>Journal <c>files_total</c>. Zero unless <see cref="TracksFileCompletion"/>.</summary>
    public int FileCount => TracksFileCompletion ? Files.Count : 0;

    private long _refinedTotal;
    private long _observedFloor;

    /// <summary>
    /// <see cref="TotalInputUnits"/> unless an exact total or a larger observed floor has
    /// been published. Sampled estimates can miss the true denominator either way; progress
    /// and INGEST_COMPLETE read this so the denominator corrects mid-run.
    /// </summary>
    public long EffectiveTotalInputUnits
    {
        get
        {
            long refined = Interlocked.Read(ref _refinedTotal);
            long declared = refined > 0 ? refined : TotalInputUnits;
            return Math.Max(declared, Interlocked.Read(ref _observedFloor));
        }
    }

    /// <summary>Publish the exact total once a background count finishes.</summary>
    public void PublishExactTotal(long exact)
    {
        if (exact > 0) Interlocked.Exchange(ref _refinedTotal, exact);
    }

    /// <summary>
    /// Raise a sampled/refined denominator when extraction has already observed more units.
    /// The observation is a hard lower bound even when no exact background counter exists
    /// for the source's logical unit (for example PGN games rather than file newlines).
    /// </summary>
    public void PublishObservedFloor(long observed)
    {
        if (observed <= 0) return;
        while (true)
        {
            long current = Interlocked.Read(ref _observedFloor);
            if (current >= observed) return;
            if (Interlocked.CompareExchange(ref _observedFloor, observed, current) == current)
                return;
        }
    }

    // Inventory reads a bounded number of bytes. Large-corpus estimates are corrected by
    // the units the ingest is already extracting (PublishObservedFloor) and, after a
    // successful uncapped run, by IngestRunner.PublishExactTotal(InputUnitsDone); no
    // second full-file counter runs beside the ingest.

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
        return new IngestInventory(
            unitType, n, [new IngestFileSpec(Path.GetFileName(filePath), filePath, n)]);
    }

    /// <param name="tracksFileCompletion">
    /// True only for a <see cref="DecomposerMultiFile{TRecord}"/> ingest that writes one
    /// period-boundary (or file-failed) receipt per enumerated file.
    /// </param>
    public static IngestInventory? FromFiles(
        string unitType,
        IReadOnlyList<string> paths,
        long maxInputUnits,
        CancellationToken ct = default,
        bool tracksFileCompletion = false)
    {
        if (paths.Count == 0) return null;
        if (maxInputUnits > 0)
        {
            var specs = paths.Select(p => new IngestFileSpec(Path.GetFileName(p), p, maxInputUnits)).ToList();
            return new IngestInventory(unitType, maxInputUnits, specs, tracksFileCompletion);
        }
        long[] units = EtlInventory.EstimateNewlineCounts(paths, ct);
        var files = new List<IngestFileSpec>(paths.Count);
        long total = 0;
        for (int i = 0; i < paths.Count; i++)
        {
            files.Add(new IngestFileSpec(Path.GetFileName(paths[i]), paths[i], units[i]));
            total += units[i];
        }
        return new IngestInventory(unitType, total, files, tracksFileCompletion);
    }

    /// <summary>
    /// One input unit per file (XML / document corpora), where a newline count
    /// (<see cref="FromFiles"/>) would not measure the unit.
    /// </summary>
    public static IngestInventory? FromFileUnits(
        string unitType,
        IReadOnlyList<string> paths,
        long maxInputUnits = 0,
        bool tracksFileCompletion = true)
    {
        if (paths.Count == 0) return null;
        if (maxInputUnits > 0)
        {
            int n = (int)Math.Min(paths.Count, maxInputUnits);
            var capped = new List<IngestFileSpec>(n);
            for (int i = 0; i < n; i++)
                capped.Add(new IngestFileSpec(Path.GetFileName(paths[i]), paths[i], 1));
            return new IngestInventory(unitType, n, capped, tracksFileCompletion);
        }
        var specs = paths.Select(p => new IngestFileSpec(Path.GetFileName(p), p, 1)).ToList();
        return new IngestInventory(unitType, paths.Count, specs, tracksFileCompletion);
    }

    /// <summary>
    /// Multi-file source whose files emit a variable number of logical records and
    /// cannot know that total without doing the ingest twice. The live denominator is
    /// deliberately unknown (zero) while file progress remains exact; the runner
    /// publishes the observed record total at successful completion. A capped run does
    /// know its record ceiling and reports that ceiling immediately.
    /// </summary>
    public static IngestInventory? FromFilesWithUnknownUnitCount(
        string unitType,
        IReadOnlyList<string> paths,
        long maxInputUnits = 0,
        bool tracksFileCompletion = true)
    {
        if (paths.Count == 0) return null;
        var specs = paths.Select(p => new IngestFileSpec(
            Path.GetFileName(p), p, InputUnits: 0)).ToList();
        return new IngestInventory(
            unitType,
            maxInputUnits > 0 ? maxInputUnits : 0,
            specs,
            tracksFileCompletion);
    }

    /// <summary>
    /// Multi-file CoNLL-U inventory under one read budget across the path list, as
    /// <see cref="FromFiles"/>.
    /// </summary>
    public static IngestInventory? FromConlluFiles(
        string unitType,
        IReadOnlyList<string> paths,
        long maxInputUnits,
        CancellationToken ct = default,
        bool tracksFileCompletion = false)
    {
        if (paths.Count == 0) return null;
        if (maxInputUnits > 0)
        {
            var specs = paths.Select(p => new IngestFileSpec(
                Path.GetFileNameWithoutExtension(p), p, maxInputUnits)).ToList();
            return new IngestInventory(unitType, maxInputUnits, specs, tracksFileCompletion);
        }
        long[] units = EtlInventory.EstimateConlluSentenceCounts(paths, ct);
        var files = new List<IngestFileSpec>(paths.Count);
        long total = 0;
        for (int i = 0; i < paths.Count; i++)
        {
            files.Add(new IngestFileSpec(
                Path.GetFileNameWithoutExtension(paths[i]), paths[i], units[i]));
            total += units[i];
        }
        return new IngestInventory(unitType, total, files, tracksFileCompletion);
    }
}

public interface IIngestInventoryProvider
{
    Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        CancellationToken ct = default);
}

/// <summary>
/// Supplies one complete physical-artifact snapshot when an ordinary source directory has
/// no explicit MANIFEST.tsv selection. The runner passes the admitted projection from this
/// graph to both inventory and execution and gives the complete graph to observability.
/// </summary>
public interface IIngestArtifactGraphProvider
{
    Task<IngestArtifactGraph?> DescribeArtifactsAsync(
        string ecosystemPath,
        DecomposerOptions options,
        CancellationToken ct = default);
}
