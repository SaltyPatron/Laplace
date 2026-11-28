using System.Diagnostics;
using Laplace.Engine.Core;
using Laplace.Ops;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Resolves and runs the installed native C <c>laplace</c> CLI binary for transactional witness
/// and ingestion operations (<c>laplace witness</c>). Never invokes external shell interpreters.
/// </summary>
internal static class NativeEngineCli
{
    public static string? ResolveBinary()
    {
        var explicitExe = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_ENGINE_EXE")
            ?? LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_ENGINE_BIN");
        var exeName = OperatingSystem.IsWindows() ? "laplace.exe" : "laplace";
        if (!string.IsNullOrWhiteSpace(explicitExe))
            return File.Exists(explicitExe.Trim()) ? Path.GetFullPath(explicitExe.Trim()) : null;

        var bundled = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(bundled)) return Path.GetFullPath(bundled);

        var binDir = Path.Combine(AppContext.BaseDirectory, "bin", exeName);
        if (File.Exists(binDir)) return Path.GetFullPath(binDir);

        var prefix = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_INSTALL_PREFIX");
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            var installed = Path.Combine(prefix.Trim(), "bin", exeName);
            if (File.Exists(installed)) return Path.GetFullPath(installed);
        }

        var declaredBuild = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_BUILD");
        if (!string.IsNullOrWhiteSpace(declaredBuild))
        {
            foreach (var sub in new[]
            {
                Path.Combine("Laplace-Engine", "icx-release", exeName),
                Path.Combine("Laplace-Engine", "icx-release", "src", exeName),
                Path.Combine("Laplace-Engine", "release", exeName),
                Path.Combine("Laplace-Engine", "build", exeName),
            })
            {
                var candidate = Path.Combine(declaredBuild.Trim(), sub);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }

        if (LaplaceInstall.TryDefaultBuildRoot(out var buildRoot))
        {
            foreach (var sub in new[]
            {
                Path.Combine("icx-release", exeName),
                Path.Combine("build-win", exeName),
                Path.Combine("engine", exeName),
            })
            {
                var candidate = Path.Combine(buildRoot, sub);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    public static async Task<CapturedProcess.Result> RunWitnessAsync(
        string witnessName,
        string trustClass,
        string rootDir,
        IReadOnlyList<string> files,
        string? lineage = null,
        CancellationToken ct = default)
    {
        var exe = ResolveBinary()
            ?? throw new InvalidOperationException(
                "Native C engine executable ('laplace') not found; set LAPLACE_ENGINE_EXE or LAPLACE_BUILD.");

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("witness");
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(LaplaceEngineRuntime.ResolveLibpqConnInfo());
        psi.ArgumentList.Add("--as");
        psi.ArgumentList.Add(witnessName);
        psi.ArgumentList.Add("--class");
        psi.ArgumentList.Add(trustClass);
        if (!string.IsNullOrWhiteSpace(lineage))
        {
            psi.ArgumentList.Add("--lineage");
            psi.ArgumentList.Add(lineage);
        }
        psi.ArgumentList.Add("--root");
        psi.ArgumentList.Add(rootDir);
        foreach (var file in files)
            psi.ArgumentList.Add(file);

        using var proc = CapturedProcess.Start(psi);
        var result = await proc.Completion.WaitAsync(ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
            throw new InvalidOperationException(
                $"Native witness command exited with code {result.ExitCode}: {detail.Trim()}");
        }
        return result;
    }

    public static async Task<CapturedProcess.Result> RunIngestSourceFileAsync(
        string sourceName,
        string filePath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var exe = ResolveBinary()
            ?? throw new InvalidOperationException(
                "Native C engine executable ('laplace') not found; set LAPLACE_ENGINE_EXE or LAPLACE_BUILD.");

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("ingest");
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(LaplaceEngineRuntime.ResolveLibpqConnInfo());
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(sourceName.Trim());
        psi.ArgumentList.Add(Path.GetFullPath(filePath));

        using var proc = CapturedProcess.Start(psi);
        var result = await proc.Completion.WaitAsync(ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
            throw new InvalidOperationException(
                $"Native ingest command (-s {sourceName}) exited with code {result.ExitCode}: {detail.Trim()}");
        }
        return result;
    }
}
