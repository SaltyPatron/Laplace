using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Laplace.Chess.Uci.Lab;

/// <summary>
/// Reads a dependency checkout's actual bytes against its original Git objects, independently of index hints:
/// status is an additional index/untracked check, but its cached stat data, assume-unchanged/skip-worktree flags,
/// filters and core.fileMode are not evidence that a compiler reads the committed source.
/// </summary>
public static class SourceIntegrity
{
    public static ProcessStartInfo Git(string source, params string[] arguments)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "--no-replace-objects", "-c", "safe.directory=" + source, "-c", "core.fsmonitor=false", "-C", source })
            psi.ArgumentList.Add(a);
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        // MSYS2's git re-globs its command line; noglob keeps every argument as given (Git for Windows ignores it).
        if (OperatingSystem.IsWindows())
            psi.Environment["MSYS"] = string.Join(' ', new[] { Environment.GetEnvironmentVariable("MSYS"), "noglob" }.Where(static s => !string.IsNullOrEmpty(s)));
        return psi;
    }

    public static string GitText(string source, params string[] arguments) => Encoding.UTF8.GetString(GitBytes(source, arguments)).Trim();

    public static byte[] GitBytes(string source, params string[] arguments)
    {
        using var p = Process.Start(Git(source, arguments))!;
        p.StandardInput.Close();
        using var ms = new MemoryStream();
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({p.ExitCode}): {err.Result.Trim()}");
        return ms.ToArray();
    }

    /// <summary>Compare actual tracked bytes and modes with the unfiltered committed objects.</summary>
    public static JsonObject VerifyCheckout(string source, string expectedCommit, string label = "Git dependency")
    {
        if (GitText(source, "rev-parse", "HEAD") != expectedCommit)
            throw new InvalidOperationException($"{label} source HEAD changed during verification; preserved");
        byte[] tree = GitBytes(source, "ls-tree", "-rz", "--full-tree", expectedCommit);
        if (tree.Length == 0) throw new InvalidOperationException($"{label} source commit contains no tracked files");
        var entries = Encoding.UTF8.GetString(tree).TrimEnd('\0').Split('\0');
        long totalBytes = 0;
        using (var batch = Process.Start(Git(source, "cat-file", "--batch"))!)
        {
            _ = batch.StandardError.ReadToEndAsync();
            var input = batch.StandardInput.BaseStream;
            var output = new BufferedStream(batch.StandardOutput.BaseStream, 1 << 20);
            try
            {
                foreach (var entry in entries)
                {
                    int tab = entry.IndexOf('\t');
                    var descriptor = entry[..tab].Split(' ');
                    string name = entry[(tab + 1)..];
                    string mode = descriptor[0], kind = descriptor[1], objectId = descriptor[2];
                    if (Path.IsPathRooted(name) || name.Split('/').Contains("..") || kind != "blob" || mode is not ("100644" or "100755" or "120000"))
                        throw new InvalidOperationException($"{label} source contains an unsupported tracked entry");
                    string path = Path.Combine(source, name.Replace('/', Path.DirectorySeparatorChar));
                    string? parent = Path.GetDirectoryName(name.Replace('/', Path.DirectorySeparatorChar));
                    while (!string.IsNullOrEmpty(parent))
                    {
                        if (new DirectoryInfo(Path.Combine(source, parent)).LinkTarget is not null)
                            throw new InvalidOperationException($"{label} source has a changed directory type; preserved: {name}");
                        parent = Path.GetDirectoryName(parent);
                    }
                    bool regular = mode != "120000";
                    var before = new FileInfo(path);
                    bool isLink = before.LinkTarget is not null;
                    if (!before.Exists || (regular && isLink) || (!regular && !isLink)
                        || (regular && !OperatingSystem.IsWindows() && ((File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0) != (mode == "100755")))
                        throw new InvalidOperationException($"{label} source has local file type/mode changes; preserved: {name}");
                    var stamp = (before.Length, before.LastWriteTimeUtc.Ticks, before.Attributes);
                    input.Write(Encoding.ASCII.GetBytes(objectId + "\n"));
                    input.Flush();
                    var header = ReadLine(output).Split(' ');
                    if (header.Length != 3 || header[0] != objectId || header[1] != "blob")
                        throw new InvalidOperationException($"{label} committed source object could not be read");
                    long size = long.Parse(header[2], System.Globalization.CultureInfo.InvariantCulture);
                    totalBytes += size;
                    if (regular)
                    {
                        using var actual = File.OpenRead(path);
                        var expected = new byte[Math.Min(size, 1 << 20)];
                        var got = new byte[expected.Length];
                        long remaining = size;
                        while (remaining > 0)
                        {
                            int take = (int)Math.Min(remaining, expected.Length);
                            output.ReadExactly(expected, 0, take);
                            if (actual.ReadAtLeast(got.AsSpan(0, take), take, throwOnEndOfStream: false) != take || !expected.AsSpan(0, take).SequenceEqual(got.AsSpan(0, take)))
                                throw new InvalidOperationException($"{label} source has local byte changes; preserved: {name}");
                            remaining -= take;
                        }
                        if (actual.ReadByte() != -1) throw new InvalidOperationException($"{label} source has local byte changes; preserved: {name}");
                    }
                    else
                    {
                        var target = new byte[size];
                        output.ReadExactly(target);
                        if (!Encoding.UTF8.GetBytes(before.LinkTarget!).AsSpan().SequenceEqual(target))
                            throw new InvalidOperationException($"{label} source has local symlink changes; preserved: {name}");
                    }
                    if (output.ReadByte() != '\n') throw new InvalidOperationException($"{label} committed source object was truncated");
                    var after = new FileInfo(path);
                    if ((after.Length, after.LastWriteTimeUtc.Ticks, after.Attributes) != stamp)
                        throw new InvalidOperationException($"{label} source changed while reading; preserved: {name}");
                }
                input.Close();
                if (!batch.WaitForExit(10_000) || batch.ExitCode != 0)
                    throw new InvalidOperationException($"{label} committed source reader failed");
            }
            finally
            {
                if (!batch.HasExited) batch.Kill();
            }
        }
        if (GitText(source, "rev-parse", "HEAD") != expectedCommit)
            throw new InvalidOperationException($"{label} source HEAD changed during verification; preserved");
        return new JsonObject
        {
            ["commit"] = expectedCommit, ["tracked_files"] = entries.Length, ["tracked_blob_bytes"] = totalBytes,
            ["verification"] = "raw-committed-blob-bytes", ["replacement_objects"] = "disabled",
            ["executable_mode_check"] = OperatingSystem.IsWindows() ? "not-applicable-on-windows" : "posix-owner-execute-bit",
        };
    }

    private static string ReadLine(Stream stream)
    {
        var bytes = new List<byte>();
        int b;
        while ((b = stream.ReadByte()) is not (-1 or '\n')) bytes.Add((byte)b);
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    /// <summary>A path with every symbolic link resolved (Python's Path.resolve(strict=True)).</summary>
    public static string RealPath(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full)!;
        string current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists) throw new FileNotFoundException(current);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                current = RealPath(target.FullName);
        }
        return current;
    }

    /// <summary>Two JSON values equal as Python dicts are equal: keys in any order.</summary>
    public static bool SameJson(JsonNode? a, JsonNode? b) => Canonical(a) == Canonical(b);

    private static string Canonical(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(",", o.OrderBy(static p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + Canonical(p.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        _ => node.ToJsonString(),
    };
}
