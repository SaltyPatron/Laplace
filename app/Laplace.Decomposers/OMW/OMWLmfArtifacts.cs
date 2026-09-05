using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.OMW;

internal static class OMWLmfArtifacts
{
    private const string Release = "2.0";
    private const string ReleaseUrl = "https://github.com/omwn/omw-data/releases/tag/v2.0";

    internal static IngestArtifactGraph? Build(string ecosystemPath, DecomposerOptions options)
    {
        if (!Directory.Exists(ecosystemPath)) return null;
        string root = Path.GetFullPath(ecosystemPath);
        string[] files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(static path => Path.GetFullPath(path), StringComparer.Ordinal)
            .ToArray();
        if (!files.Any(IsLexiconXml)) return null;

        return new IngestArtifactGraph(files.Select(path => BuildOne(root, path, options)));
    }

    private static IngestArtifact BuildOne(
        string root, string path, DecomposerOptions options)
    {
        string full = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        string name = Path.GetFileName(full);
        string? lexicon = LexiconDirectory(full);
        string? language = lexicon is { Length: > 4 } ? lexicon[4..] : null;
        bool languageSelected = options.Languages?.MatchesRaw(language) != false;

        var disposition = IngestArtifactDisposition.Unsupported;
        string notes = "OMW 2.0 adapter does not interpret this artifact type";
        string mediaType = "application/octet-stream";
        string labelKind = "unsupported";

        if (IsLexiconXml(full))
        {
            disposition = languageSelected
                ? IngestArtifactDisposition.Admitted
                : IngestArtifactDisposition.ExcludedWithReason;
            notes = languageSelected ? "" : "excluded by the configured language filter";
            mediaType = "application/xml";
            labelKind = "xml";
        }
        else if (lexicon is not null && IsMetadataSidecar(name))
        {
            disposition = languageSelected
                ? IngestArtifactDisposition.Admitted
                : IngestArtifactDisposition.ExcludedWithReason;
            notes = languageSelected ? "" : "sidecar belongs to a lexicon excluded by the language filter";
            mediaType = MetadataMediaType(name);
            labelKind = SidecarKind(name);
        }
        else if (string.Equals(name, "index.toml", StringComparison.OrdinalIgnoreCase))
        {
            disposition = IngestArtifactDisposition.Admitted;
            notes = "";
            mediaType = "application/toml";
            labelKind = "index";
        }
        else if (name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase))
        {
            disposition = IngestArtifactDisposition.EquivalentPackaging;
            notes = "combined archive is represented by its enumerated extracted lexicons and sidecars";
            mediaType = "application/x-xz";
            labelKind = "archive";
        }

        var info = new FileInfo(full);
        return new IngestArtifact(
            OMWSource.SourceName,
            Release,
            relative,
            relative,
            full,
            disposition,
            ReleaseUrl,
            FetchedAtUtc: "",
            Bytes: info.Length,
            Sha256: "",
            UpstreamChecksum: "",
            MediaType: mediaType,
            License: "per-lexicon WN-LMF declaration and LICENSE sidecar",
            Citation: "per-lexicon WN-LMF declaration and citation.bib sidecar",
            Language: language ?? "mul",
            Split: "",
            AnnotationOrigin: "OMW 2.0 combined release",
            Notes: notes,
            JournalLabel: $"{OMWDecomposer.LmfLabelPrefix}{labelKind}/{relative}",
            ModifiedAt: info.LastWriteTimeUtc);
    }

    private static bool IsLexiconXml(string path)
    {
        string? lexicon = LexiconDirectory(path);
        return lexicon is not null
            && string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileNameWithoutExtension(path), lexicon, StringComparison.Ordinal);
    }

    private static string? LexiconDirectory(string path)
    {
        for (DirectoryInfo? dir = new FileInfo(path).Directory; dir is not null; dir = dir.Parent)
            if (dir.Name.StartsWith("omw-", StringComparison.Ordinal) && dir.Name.Length > 4)
                return dir.Name;
        return null;
    }

    private static bool IsMetadataSidecar(string name) =>
        string.Equals(name, "LICENSE", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "citation.bib", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("README", StringComparison.OrdinalIgnoreCase);

    private static string SidecarKind(string name) =>
        string.Equals(name, "LICENSE", StringComparison.OrdinalIgnoreCase) ? "license"
        : string.Equals(name, "citation.bib", StringComparison.OrdinalIgnoreCase) ? "citation"
        : "readme";

    private static string MetadataMediaType(string name) =>
        string.Equals(name, "citation.bib", StringComparison.OrdinalIgnoreCase)
            ? "application/x-bibtex" : "text/plain";
}
