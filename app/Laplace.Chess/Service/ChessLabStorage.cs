namespace Laplace.Chess.Service;

internal static class ChessLabStorage
{
    private const string SpoolEnvironmentVariable = "LAPLACE_CHESS_SPOOL_DIR";

    internal static string ResolveSpoolRoot()
    {
        var configured = Environment.GetEnvironmentVariable(SpoolEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured.Trim());

        if (!OperatingSystem.IsWindows() && Directory.Exists("/pgtemp"))
            return "/pgtemp/laplace/chess-lab";

        return Path.Combine(Path.GetTempPath(), "laplace-chess-lab-spool");
    }

    internal static string CreateJobSpool(string jobId)
    {
        var root = ResolveSpoolRoot();
        var path = Path.Combine(root, jobId);
        Directory.CreateDirectory(path);
        return path;
    }

    internal static bool PersistPgn(
        IReadOnlyDictionary<string, string> config,
        bool defaultValue = false)
        => config.TryGetValue("persistPgn", out var raw)
            ? bool.TryParse(raw, out var value) && value
            : defaultValue;

    internal static bool PersistTranscript(IReadOnlyDictionary<string, string> config)
        => config.TryGetValue("persistTranscript", out var raw)
            && bool.TryParse(raw, out var value)
            && value;

    internal static string PersistArtifact(
        string sourcePath,
        string labDir,
        string jobId,
        string name)
    {
        var targetDir = Path.Combine(labDir, jobId);
        Directory.CreateDirectory(targetDir);
        var target = Path.Combine(targetDir, name);
        File.Move(sourcePath, target, overwrite: true);
        return target;
    }

    internal static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A failed cleanup must not turn a completed/recorded chess job into a false failure.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static void DeleteDirectoryIfEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
