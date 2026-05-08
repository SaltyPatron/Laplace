namespace Laplace.Decomposers.Model;

using System;
using System.IO;
using System.Linq;

/// <summary>
/// Resolve the on-disk content root for a HuggingFace package given any of
/// the three observed layouts (direct dir / HF cache / diffusers
/// multi-component). All other F5 decomposer infrastructure (manifest,
/// per-piece decomposers, per-tensor extractors) operates on the resolved
/// content root, not the user-supplied path.
///
/// Phase 4 / F5 / G5.
/// </summary>
public static class HuggingFacePackageLayoutResolver
{
    public sealed record Resolved(
        string                    PackagePath,
        string                    ContentRoot,
        HuggingFacePackageLayout  Layout);

    /// <summary>
    /// Inspect <paramref name="packagePath"/> on disk and return the layout
    /// + content root to drive subsequent decomposition.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">No directory at packagePath.</exception>
    /// <exception cref="InvalidDataException">HF cache snapshot dir missing.</exception>
    public static Resolved Resolve(string packagePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(packagePath);
        if (!Directory.Exists(packagePath))
        {
            throw new DirectoryNotFoundException($"package path not found: {packagePath}");
        }

        // Layout 2 — HuggingFace cache. Detected by presence of refs/ +
        // snapshots/ subdirs at the package root.
        var refsDir      = Path.Combine(packagePath, "refs");
        var snapshotsDir = Path.Combine(packagePath, "snapshots");
        if (Directory.Exists(refsDir) && Directory.Exists(snapshotsDir))
        {
            var contentRoot = ResolveCacheSnapshot(packagePath, refsDir, snapshotsDir);
            return new Resolved(packagePath, contentRoot, HuggingFacePackageLayout.HuggingFaceCache);
        }

        // Layout 3 — Diffusers multi-component. Detected by presence of
        // model_index.json at the package root.
        if (File.Exists(Path.Combine(packagePath, "model_index.json")))
        {
            return new Resolved(packagePath, packagePath, HuggingFacePackageLayout.DiffusersMultiComponent);
        }

        // Layout 1 — direct directory. Default fallback.
        return new Resolved(packagePath, packagePath, HuggingFacePackageLayout.DirectDirectory);
    }

    /// <summary>
    /// Follow refs/main → snapshots/&lt;sha&gt;/ for HuggingFace cache layouts.
    /// If refs/main is absent or unreadable, fall back to the most recently
    /// modified snapshot directory.
    /// </summary>
    private static string ResolveCacheSnapshot(string packagePath, string refsDir, string snapshotsDir)
    {
        var refsMain = Path.Combine(refsDir, "main");
        if (File.Exists(refsMain))
        {
            var sha = File.ReadAllText(refsMain).Trim();
            if (!string.IsNullOrEmpty(sha))
            {
                var snapshotPath = Path.Combine(snapshotsDir, sha);
                if (Directory.Exists(snapshotPath))
                {
                    return snapshotPath;
                }
            }
        }

        // Fallback: pick most recently modified snapshot subdir.
        var snapshotDirs = Directory.GetDirectories(snapshotsDir)
            .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
            .ToArray();
        if (snapshotDirs.Length == 0)
        {
            throw new InvalidDataException(
                $"HF cache layout detected at {packagePath} but snapshots/ contains no directories");
        }
        return snapshotDirs[0];
    }
}
