using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.Code;

/// <summary>Read-only observation of an exact Git tree. Paths/revisions are provenance,
/// never substitutes for the native identities of the admitted source bytes.</summary>
public sealed class VerifiedGitRepository
{
    public sealed record Selection(string Upstream, string Commit, string License,
        string BinaryPath, string BuildReceiptPath, string? RequiredModality = null);
    public sealed record Entry(string Path, string Mode, string GitObject, long? Bytes,
        string Sha256, string? Modality, string Disposition, string Reason)
    {
        public string Representation => Disposition != "admitted" ? "unadmitted"
            : Modality == "text" ? "raw-text" : "native-cst";
    }

    public string Root { get; }
    public Selection Input { get; }
    public string Tree { get; }
    public string BinarySha256 { get; }
    public string BuildReceiptSha256 { get; }
    public IReadOnlyList<Entry> Entries { get; }
    public byte[] ProvenanceUtf8 { get; }
    public IngestArtifactGraph Graph { get; }

    private VerifiedGitRepository(string root, Selection input, string tree,
        string binarySha256, string buildReceiptSha256, JsonElement recipe, List<Entry> entries)
    {
        Root = root; Input = input; Tree = tree; BinarySha256 = binarySha256;
        BuildReceiptSha256 = buildReceiptSha256; Entries = entries;
        // Deliberately exclude raw origin/config, local private receipt and binary paths,
        // timestamps and filesystem mtimes from the public, repeatable provenance body.
        ProvenanceUtf8 = JsonSerializer.SerializeToUtf8Bytes(new {
            schema = "laplace.verified-git-corpus.v1", upstream = input.Upstream,
            commit = input.Commit, tree, license = input.License,
            selection = "all Git-tree entries; regular files must match committed blob bytes; untracked and ignored files are outside this selection",
            build = new { binary_sha256 = binarySha256, receipt_sha256 = buildReceiptSha256, recipe },
            artifacts = entries });
        Graph = new IngestArtifactGraph(entries.Select(e => new IngestArtifact(
            RepoSource.SourceName, "git-" + input.Commit, e.Path, e.Path,
            Path.Combine(root, e.Path), e.Disposition == "admitted"
                ? IngestArtifactDisposition.Admitted : IngestArtifactDisposition.Unsupported,
            input.Upstream, "", e.Bytes, e.Sha256, e.GitObject, e.Modality ?? "",
            input.License, input.Upstream + "/tree/" + input.Commit, "", "",
            "verified-git-tracked-bytes", e.Reason,
            "repo/git-" + input.Commit + "/" + e.Path,
            File.Exists(Path.Combine(root, e.Path)) ? File.GetLastWriteTimeUtc(Path.Combine(root, e.Path)) : null)));
    }

    public static VerifiedGitRepository Capture(string root, Selection selection,
        Func<string, byte[], (string? Modality, string? UnsupportedReason)> classify)
    {
        root = Path.GetFullPath(root);
        if (new DirectoryInfo(root).LinkTarget is not null)
            throw new InvalidDataException("Use the real Git checkout root, not a symbolic link.");
        if (!Uri.TryCreate(selection.Upstream, UriKind.Absolute, out var upstream)
            || upstream.Scheme != "https" || upstream.UserInfo.Length != 0
            || upstream.Query.Length != 0 || upstream.Fragment.Length != 0)
            throw new InvalidDataException("Expected upstream must be a public HTTPS repository URL without credentials.");
        if (selection.Commit.Length is not (40 or 64) || !selection.Commit.All(Uri.IsHexDigit))
            throw new InvalidDataException("An exact full Git commit is required.");
        if (Path.GetFullPath(GitText(root, "rev-parse", "--show-toplevel")) != root
            || GitText(root, "rev-parse", "HEAD") != selection.Commit)
            throw new InvalidDataException("Checkout root or HEAD does not match the selected Git commit.");
        string origin = GitText(root, "remote", "get-url", "origin");
        if (NormalizeUpstream(origin) != NormalizeUpstream(selection.Upstream))
            throw new InvalidDataException("Checkout origin does not match the expected public upstream.");
        string tree = GitText(root, "rev-parse", "HEAD^{tree}");
        string privateReceipt = GitText(root, "rev-parse", "--git-path", "laplace-stockfish-build.json");
        privateReceipt = Path.GetFullPath(Path.IsPathRooted(privateReceipt) ? privateReceipt : Path.Combine(root, privateReceipt));
        if (privateReceipt != Path.GetFullPath(selection.BuildReceiptPath))
            throw new InvalidDataException("Build receipt must be the retained receipt in this checkout's Git metadata.");
        byte[] buildBytes = File.ReadAllBytes(privateReceipt);
        using var build = JsonDocument.Parse(buildBytes);
        var recipe = build.RootElement.GetProperty("recipe");
        if (recipe.GetProperty("commit").GetString() != selection.Commit)
            throw new InvalidDataException("Retained engine build belongs to another Git commit.");
        if (!recipe.TryGetProperty("source_integrity", out var sourceIntegrity)
            || sourceIntegrity.ValueKind != JsonValueKind.String
            || sourceIntegrity.GetString() != "git-committed-bytes-and-modes-v1"
            || !recipe.TryGetProperty("dependency_include", out var dependencyInclude)
            || dependencyInclude.ValueKind != JsonValueKind.String
            || dependencyInclude.GetString() != "regenerated-by-upstream-make-with-prior-file-preserved")
            throw new InvalidDataException("Retained Stockfish build lacks the required source-byte/mode and dependency-include verification. "
                + "Rebuild this checkout with scripts/install-stockfish.py --source-dir <checkout> --rebuild before corpus admission.");
        if (Path.GetFullPath(selection.BinaryPath) != Path.Combine(root, "src", OperatingSystem.IsWindows() ? "stockfish.exe" : "stockfish"))
            throw new InvalidDataException("Selected engine must be the direct executable of this source checkout.");
        string binaryHash = HashFile(selection.BinaryPath);
        if (binaryHash != build.RootElement.GetProperty("binary_sha256").GetString())
            throw new InvalidDataException("Actual engine bytes do not match the retained build receipt.");
        // Only declared, public recipe fields cross into substrate provenance.
        var publicRecipe = JsonSerializer.SerializeToElement(new {
            commit = recipe.GetProperty("commit").GetString(), arch = recipe.GetProperty("arch").GetString(),
            cpu = recipe.GetProperty("cpu").GetString(), compiler = recipe.GetProperty("compiler").GetString(),
            compiler_version = recipe.GetProperty("compiler_version").GetString(),
            source_integrity = sourceIntegrity.GetString(), dependency_include = dependencyInclude.GetString() });
        var entries = new List<Entry>();
        foreach (string line in new UTF8Encoding(false, true).GetString(Git(root, "ls-tree", "-rz", "--full-tree", "HEAD")).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int tab = line.IndexOf('\t');
            if (tab < 0) throw new InvalidDataException("Malformed Git tree entry.");
            string[] fields = line[..tab].Split(' ');
            string relative = line[(tab + 1)..];
            if (Path.IsPathRooted(relative) || relative.Split('/').Any(p => p is ".." or "." or "") || relative.Contains('\\'))
                throw new InvalidDataException("Git tree contains an unsafe or ambiguous path.");
            string path = Path.Combine(root, relative);
            for (string? parent = Path.GetDirectoryName(path); parent != root && parent is not null; parent = Path.GetDirectoryName(parent))
                if (new DirectoryInfo(parent).LinkTarget is not null)
                    throw new InvalidDataException("A tracked file has a symbolic-link parent.");
            if (fields[1] != "blob" || fields[0] is not ("100644" or "100755"))
            {
                entries.Add(new(relative, fields[0], fields[2], null, "", null, "unsupported-with-why-not",
                    "Non-regular Git entry is recorded as a pointer; its target bytes are not admitted."));
                continue;
            }
            if (new FileInfo(path).LinkTarget is not null)
                throw new InvalidDataException("A committed regular file was replaced by a symbolic link.");
            byte[] bytes = File.ReadAllBytes(path);
            VerifyGitBlob(bytes, fields[2]);
            if (!OperatingSystem.IsWindows())
            {
                bool executable = (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
                if (executable != (fields[0] == "100755"))
                    throw new InvalidDataException("Working-tree executable mode differs from the selected Git tree.");
            }
            var (modality, why) = classify(path, bytes);
            if (bytes.Length == 0) why = "Empty source has no native grammar composition; bytes and Git identity are retained.";
            if (modality is null && why is null) why = "No registered native grammar for this artifact.";
            entries.Add(new(relative, fields[0], fields[2], bytes.LongLength, Hash(bytes), modality,
                why is null ? "admitted" : "unsupported-with-why-not", why ?? ""));
        }
        if (!entries.Any(e => e.Disposition == "admitted"))
            throw new InvalidDataException("Selected Git tree has no artifacts supported by the native grammar registry.");
        if (selection.RequiredModality is { } required
            && (!entries.Any(e => e.Disposition == "admitted" && e.Modality == required)
                || entries.Any(e => e.Path.EndsWith("." + required, StringComparison.Ordinal)
                    && (e.Disposition != "admitted" || e.Modality != required))))
            throw new InvalidDataException("Required native source grammar is unavailable for the selected Git code corpus.");
        var result = new VerifiedGitRepository(root, selection, tree, binaryHash, Hash(buildBytes), publicRecipe,
            entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToList());
        result.VerifyUnchanged();
        return result;
    }

    public byte[] ReadVerified(Entry entry)
    {
        string path = Path.Combine(Root, entry.Path);
        for (string? parent = Path.GetDirectoryName(path); parent != Root && parent is not null; parent = Path.GetDirectoryName(parent))
            if (new DirectoryInfo(parent).LinkTarget is not null)
                throw new InvalidDataException("A tracked file parent became a symbolic link.");
        if (new FileInfo(path).LinkTarget is not null)
            throw new InvalidDataException("A tracked regular file became a symbolic link.");
        if (!OperatingSystem.IsWindows())
        {
            bool executable = (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
            if (executable != (entry.Mode == "100755")) throw new InvalidDataException("Tracked executable mode changed.");
        }
        byte[] bytes = File.ReadAllBytes(path);
        if (Hash(bytes) != entry.Sha256) throw new InvalidDataException("Tracked bytes changed after Git observation: " + entry.Path);
        return bytes;
    }

    public void VerifyUnchanged()
    {
        if (GitText(Root, "rev-parse", "HEAD") != Input.Commit || GitText(Root, "rev-parse", "HEAD^{tree}") != Tree
            || NormalizeUpstream(GitText(Root, "remote", "get-url", "origin")) != NormalizeUpstream(Input.Upstream)
            || HashFile(Input.BinaryPath) != BinarySha256 || HashFile(Input.BuildReceiptPath) != BuildReceiptSha256)
            throw new InvalidDataException("Git/build identity changed during corpus admission.");
        foreach (var e in Entries.Where(e => e.Bytes is not null)) ReadVerified(e);
    }

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static string NormalizeUpstream(string value)
    {
        string normalized = value.Trim().TrimEnd('/').Replace("git@github.com:", "https://github.com/", StringComparison.Ordinal);
        return normalized.EndsWith(".git", StringComparison.Ordinal) ? normalized[..^4] : normalized;
    }
    private static void VerifyGitBlob(byte[] bytes, string expected)
    {
        using var hash = IncrementalHash.CreateHash(expected.Length == 40 ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes($"blob {bytes.Length}\0")); hash.AppendData(bytes);
        if (Convert.ToHexStringLower(hash.GetHashAndReset()) != expected)
            throw new InvalidDataException("Actual tracked bytes do not match the selected Git blob.");
    }
    private static string GitText(string root, params string[] args) => Encoding.UTF8.GetString(Git(root, args)).Trim();
    private static byte[] Git(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (string arg in new[] { "--no-optional-locks", "--no-replace-objects", "-c", "safe.directory=" + root }.Concat(args)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not start Git.");
        var error = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream(); process.StandardOutput.BaseStream.CopyTo(output); process.WaitForExit(); error.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidDataException("Read-only Git identity inspection failed.");
        return output.ToArray();
    }
}

public sealed class VerifiedGitRepoDecomposer(VerifiedGitRepository repository)
    : RepoDecomposer(repository), IIgnoresAmbientArtifactManifest;
