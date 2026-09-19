using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Laplace.Api.Contracts;
using Laplace.Engine.Core;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Static target-machine accounting for already-built artifacts. The uploaded image is
/// never executed: llvm-objdump recovers machine instructions and llvm-mca applies the
/// target CPU's LLVM scheduling model. The receipt deliberately distinguishes a
/// linearized executable-section schedule from a control-flow-weighted whole-program
/// cost; branch/loop/input counts that were not supplied are not invented.
/// </summary>
internal static partial class MachineCostAnalyzer
{
    public const long MaxArtifactBytes = 64L * 1024 * 1024;
    public const int MaxIterations = 1_000_000;
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(60);
    private const int DiagnosticLimit = 64 * 1024;

    private sealed record Tool(string Command, string Version);
    private sealed record ProcessReceipt(
        bool ToolAvailable, bool TimedOut, int ExitCode, string Stderr);
    private sealed record ExtractedAssembly(
        string ObjectFormat, long InstructionCount, long UnsupportedInstructionCount);
    private sealed record McaSummary(
        long ScheduledInstructions,
        long TotalCycles,
        long? TotalUops,
        int? DispatchWidth,
        double? UopsPerCycle,
        double? Ipc,
        double? BlockRThroughput,
        IReadOnlyList<MachineCostResourcePressure> ResourcePressure);

    public static async Task<MachineCostResponse> AnalyzeAsync(
        Stream artifact,
        string artifactName,
        string cpu,
        double clockHz,
        string? targetTriple,
        string? symbol,
        int iterations,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(cpu);
        if (!double.IsFinite(clockHz) || clockHz <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(clockHz));
        if (iterations < 1 || iterations > MaxIterations)
            throw new ArgumentOutOfRangeException(nameof(iterations));

        string workRoot = ResolveWorkRoot();
        Directory.CreateDirectory(workRoot);
        string job = Path.Combine(workRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);

        try
        {
            string imagePath = Path.Combine(job, "artifact.bin");
            (long bytes, string sha256) = await CopyAndHashAsync(artifact, imagePath, ct);
            if (bytes == 0)
                throw new MachineCostAnalysisException(
                    "artifact_required", "Artifact body is empty.", serviceUnavailable: false);

            Tool objdump = await RequireToolAsync(
                "LAPLACE_LLVM_OBJDUMP", "llvm-objdump", job, ct);
            Tool mca = await RequireToolAsync(
                "LAPLACE_LLVM_MCA", "llvm-mca", job, ct);

            string disassemblyPath = Path.Combine(job, "disassembly.txt");
            var objdumpArguments = new List<string>
            {
                "--no-show-raw-insn",
                $"--mcpu={cpu.Trim()}",
                string.IsNullOrWhiteSpace(symbol) ? "--disassemble" : $"--disassemble={symbol.Trim()}",
                imagePath
            };
            ProcessReceipt disassembly = await RunToFileAsync(
                objdump.Command,
                objdumpArguments,
                job, disassemblyPath, ToolTimeout, ct);
            if (!disassembly.ToolAvailable)
                throw ToolUnavailable(objdump.Command);
            if (disassembly.TimedOut)
                throw new MachineCostAnalysisException(
                    "disassembler_timeout", "LLVM disassembly exceeded the analysis deadline.",
                    serviceUnavailable: true);
            if (disassembly.ExitCode != 0)
                throw new MachineCostAnalysisException(
                    "invalid_machine_artifact",
                    $"LLVM could not disassemble the artifact: {TrimDiagnostic(disassembly.Stderr)}",
                    serviceUnavailable: false);

            string assemblyPath = Path.Combine(job, "instructions.s");
            ExtractedAssembly extracted = await ExtractAssemblyAsync(
                disassemblyPath, assemblyPath, ct);
            if (extracted.InstructionCount == 0)
                throw new MachineCostAnalysisException(
                    "no_machine_instructions",
                    "No machine instructions were recovered from executable sections.",
                    serviceUnavailable: false);
            if (extracted.UnsupportedInstructionCount != 0)
                throw new MachineCostAnalysisException(
                    "undecoded_machine_instructions",
                    $"Disassembly contained {extracted.UnsupportedInstructionCount} undecoded instruction(s); refusing to publish an incomplete cycle receipt.",
                    serviceUnavailable: false);

            string triple = string.IsNullOrWhiteSpace(targetTriple)
                ? InferTriple(extracted.ObjectFormat)
                : targetTriple.Trim();
            if (string.IsNullOrWhiteSpace(triple))
                throw new MachineCostAnalysisException(
                    "target_triple_required",
                    $"Could not infer a target triple from object format '{extracted.ObjectFormat}'. Supply ?triple= explicitly.",
                    serviceUnavailable: false);

            string mcaPath = Path.Combine(job, "mca.txt");
            ProcessReceipt scheduled = await RunToFileAsync(
                mca.Command,
                [$"-iterations={iterations}", $"-mtriple={triple}", $"-mcpu={cpu}", assemblyPath],
                job, mcaPath, ToolTimeout, ct);
            if (!scheduled.ToolAvailable)
                throw ToolUnavailable(mca.Command);
            if (scheduled.TimedOut)
                throw new MachineCostAnalysisException(
                    "machine_schedule_timeout",
                    "LLVM machine scheduling exceeded the analysis deadline.",
                    serviceUnavailable: true);
            if (scheduled.ExitCode != 0)
                throw new MachineCostAnalysisException(
                    "machine_model_rejected",
                    $"The declared target/cpu could not schedule the recovered instruction stream: {TrimDiagnostic(scheduled.Stderr)}",
                    serviceUnavailable: false);

            McaSummary summary = ParseMcaSummary(mcaPath);
            if (summary.TotalCycles <= 0)
                throw new MachineCostAnalysisException(
                    "machine_schedule_incomplete",
                    "LLVM completed without a positive total-cycle receipt.",
                    serviceUnavailable: false);

            double seconds = summary.TotalCycles / clockHz;
            string safeName = Path.GetFileName(artifactName.Trim());
            if (safeName.Length == 0) safeName = "artifact.bin";

            return new MachineCostResponse(
                Schema: "laplace.machine-cost/v1",
                ArtifactSha256: sha256,
                ArtifactBytes: bytes,
                ArtifactName: safeName,
                Symbol: string.IsNullOrWhiteSpace(symbol) ? null : symbol.Trim(),
                ObjectFormat: extracted.ObjectFormat,
                TargetTriple: triple,
                Cpu: cpu.Trim(),
                ClockHz: clockHz,
                Iterations: iterations,
                StaticInstructionCount: extracted.InstructionCount,
                ScheduledInstructionInstances: summary.ScheduledInstructions,
                TotalCycles: summary.TotalCycles,
                TotalUops: summary.TotalUops,
                DispatchWidth: summary.DispatchWidth,
                UopsPerCycle: summary.UopsPerCycle,
                Ipc: summary.Ipc,
                BlockRThroughputCycles: summary.BlockRThroughput,
                CalculatedSeconds: seconds,
                CalculatedNanoseconds: seconds * 1_000_000_000.0,
                Scope: string.IsNullOrWhiteSpace(symbol)
                    ? "linearized executable-section machine schedule"
                    : "linearized named-symbol machine schedule",
                ControlFlowWeighted: false,
                ResourcePressure: summary.ResourcePressure,
                ObjdumpVersion: objdump.Version,
                McaVersion: mca.Version,
                Assumptions:
                [
                    "The uploaded artifact is disassembled and analyzed but never executed.",
                    "The recovered executable-section instruction stream is scheduled in file order; runtime branch/loop/input execution counts are not guessed.",
                    "Instruction latency, throughput and execution-resource constraints come from LLVM's scheduling model for the declared target triple and CPU.",
                    "calculated_seconds = total_cycles / clock_hz. Cache contents, OS scheduling, I/O service time and concurrent interference remain separate state unless explicitly modeled."
                ],
                CalculationId: null,
                WitnessId: null);
        }
        finally
        {
            try { Directory.Delete(job, recursive: true); } catch { }
        }
    }

    private static async Task<(long Bytes, string Sha256)> CopyAndHashAsync(
        Stream source, string destination, CancellationToken ct)
    {
        await using var output = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        long total = 0;

        while (true)
        {
            int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0) break;
            total = checked(total + read);
            if (total > MaxArtifactBytes)
                throw new MachineCostAnalysisException(
                    "artifact_too_large",
                    $"Artifact exceeds the {MaxArtifactBytes} byte analysis limit.",
                    serviceUnavailable: false);
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        await output.FlushAsync(ct);
        return (total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task<Tool> RequireToolAsync(
        string environmentKey, string baseName, string workingDirectory, CancellationToken ct)
    {
        string? configured = Environment.GetEnvironmentVariable(environmentKey);
        IEnumerable<string> candidates = string.IsNullOrWhiteSpace(configured)
            ? ToolCandidates(baseName)
            : [configured.Trim()];

        foreach (string candidate in candidates)
        {
            var probe = await RunCapturedAsync(
                candidate, ["--version"], workingDirectory, TimeSpan.FromSeconds(5), ct);
            if (!probe.ToolAvailable || probe.TimedOut || probe.ExitCode != 0) continue;
            string version = probe.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim() ?? candidate;
            return new Tool(candidate, version);
        }

        throw new MachineCostAnalysisException(
            "machine_cost_toolchain_unavailable",
            $"No usable {baseName} was found. Set {environmentKey} to the installed LLVM tool.",
            serviceUnavailable: true);
    }

    private static IEnumerable<string> ToolCandidates(string baseName)
    {
        yield return baseName;
        for (int version = 22; version >= 17; version--)
            yield return $"{baseName}-{version}";
    }

    private sealed record CapturedReceipt(
        bool ToolAvailable, bool TimedOut, int ExitCode, string Stdout, string Stderr);

    private static async Task<CapturedReceipt> RunCapturedAsync(
        string tool, IReadOnlyList<string> arguments, string workingDirectory,
        TimeSpan timeoutValue, CancellationToken ct)
    {
        var start = ProcessStart(tool, arguments, workingDirectory);
        Process? process;
        try { process = Process.Start(start); }
        catch (Win32Exception)
        {
            return new CapturedReceipt(false, false, 127, "", $"Tool '{tool}' is not installed or not on PATH.");
        }
        if (process is null)
            return new CapturedReceipt(false, false, 127, "", $"Tool '{tool}' did not start.");

        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            timeout.CancelAfter(timeoutValue);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                await Task.WhenAll(stdoutTask, stderrTask);
                return new CapturedReceipt(
                    true, true, 124,
                    TrimDiagnostic(await stdoutTask), TrimDiagnostic(await stderrTask));
            }
            catch
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            await Task.WhenAll(stdoutTask, stderrTask);
            return new CapturedReceipt(
                true, false, process.ExitCode,
                TrimDiagnostic(await stdoutTask), TrimDiagnostic(await stderrTask));
        }
    }

    private static async Task<ProcessReceipt> RunToFileAsync(
        string tool, IReadOnlyList<string> arguments, string workingDirectory,
        string outputPath, TimeSpan timeoutValue, CancellationToken ct)
    {
        var start = ProcessStart(tool, arguments, workingDirectory);
        Process? process;
        try { process = Process.Start(start); }
        catch (Win32Exception)
        {
            return new ProcessReceipt(false, false, 127, $"Tool '{tool}' is not installed or not on PATH.");
        }
        if (process is null)
            return new ProcessReceipt(false, false, 127, $"Tool '{tool}' did not start.");

        using (process)
        await using (var output = new FileStream(
            outputPath, FileMode.Create, FileAccess.Write, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            Task stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            timeout.CancelAfter(timeoutValue);
            bool timedOut = false;
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            catch
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            try { await stdoutTask; } catch when (timedOut) { }
            string stderr = await stderrTask;
            await output.FlushAsync(CancellationToken.None);
            return new ProcessReceipt(
                true, timedOut, timedOut ? 124 : process.ExitCode,
                TrimDiagnostic(stderr));
        }
    }

    private static ProcessStartInfo ProcessStart(
        string tool, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var start = new ProcessStartInfo(tool)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static async Task<ExtractedAssembly> ExtractAssemblyAsync(
        string disassemblyPath, string assemblyPath, CancellationToken ct)
    {
        using var input = new StreamReader(disassemblyPath);
        await using var output = new StreamWriter(assemblyPath, append: false);
        string objectFormat = "unknown";
        long instructions = 0;
        long unsupported = 0;

        while (await input.ReadLineAsync() is { } line)
        {
            ct.ThrowIfCancellationRequested();

            int formatAt = line.IndexOf("file format ", StringComparison.OrdinalIgnoreCase);
            if (formatAt >= 0)
                objectFormat = line[(formatAt + "file format ".Length)..].Trim();

            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string address = line[..colon].Trim();
            if (!IsHex(address)) continue;

            string instruction = line[(colon + 1)..].Trim();
            if (instruction.Length == 0) continue;
            instruction = TrailingSymbolRegex().Replace(instruction, "");
            if (instruction.Contains("<unknown>", StringComparison.OrdinalIgnoreCase)
                || instruction.StartsWith(".word", StringComparison.OrdinalIgnoreCase)
                || instruction.StartsWith(".long", StringComparison.OrdinalIgnoreCase)
                || instruction.StartsWith(".byte", StringComparison.OrdinalIgnoreCase))
            {
                unsupported++;
                continue;
            }

            await output.WriteLineAsync(instruction);
            instructions++;
        }

        await output.FlushAsync();
        return new ExtractedAssembly(objectFormat, instructions, unsupported);
    }

    private static bool IsHex(string value)
    {
        if (value.Length == 0) return false;
        foreach (char c in value)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    private static string InferTriple(string objectFormat)
    {
        string f = objectFormat.Trim().ToLowerInvariant();
        if (f.Contains("elf64") && (f.Contains("aarch64") || f.Contains("arm64")))
            return "aarch64-unknown-linux-gnu";
        if (f.Contains("elf32") && f.Contains("arm"))
            return "armv7-unknown-linux-gnueabihf";
        if (f.Contains("elf64") && f.Contains("x86-64"))
            return "x86_64-unknown-linux-gnu";
        if (f.Contains("elf32") && (f.Contains("i386") || f.Contains("x86")))
            return "i386-unknown-linux-gnu";
        if (f.Contains("elf64") && f.Contains("riscv"))
            return "riscv64-unknown-linux-gnu";
        if (f.Contains("elf32") && f.Contains("riscv"))
            return "riscv32-unknown-linux-gnu";
        if (f.Contains("pei-x86-64"))
            return "x86_64-pc-windows-msvc";
        if (f.Contains("pei-i386"))
            return "i686-pc-windows-msvc";
        if (f.Contains("mach-o") && (f.Contains("arm64") || f.Contains("aarch64")))
            return "arm64-apple-darwin";
        if (f.Contains("mach-o") && f.Contains("x86-64"))
            return "x86_64-apple-darwin";
        return "";
    }

    private static McaSummary ParseMcaSummary(string path)
    {
        long instructions = 0;
        long cycles = 0;
        long? uops = null;
        int? dispatch = null;
        double? uopsPerCycle = null;
        double? ipc = null;
        double? throughput = null;
        var resourceNames = new Dictionary<int, string>();
        List<int>? pressureColumns = null;
        IReadOnlyList<MachineCostResourcePressure> pressure = [];
        bool needPressureHeader = false;
        bool needPressureValues = false;

        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();

            if (TryLong(line, "Instructions:", out long instructionValue))
                instructions = instructionValue;
            else if (TryLong(line, "Total Cycles:", out long cycleValue))
                cycles = cycleValue;
            else if (TryLong(line, "Total uOps:", out long uopsValue))
                uops = uopsValue;
            else if (TryInt(line, "Dispatch Width:", out int dispatchValue))
                dispatch = dispatchValue;
            else if (TryDouble(line, "uOps Per Cycle:", out double upcValue))
                uopsPerCycle = upcValue;
            else if (TryDouble(line, "IPC:", out double ipcValue))
                ipc = ipcValue;
            else if (TryDouble(line, "Block RThroughput:", out double throughputValue))
                throughput = throughputValue;

            Match resource = ResourceDefinitionRegex().Match(line);
            if (resource.Success
                && int.TryParse(resource.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int resourceIndex))
                resourceNames[resourceIndex] = resource.Groups[2].Value.Trim();

            if (line.Equals("Resource pressure per iteration:", StringComparison.Ordinal))
            {
                needPressureHeader = true;
                needPressureValues = false;
                continue;
            }

            if (needPressureHeader && line.Contains('[', StringComparison.Ordinal))
            {
                pressureColumns = ResourceIndexRegex().Matches(line)
                    .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                    .ToList();
                needPressureHeader = false;
                needPressureValues = pressureColumns.Count > 0;
                continue;
            }

            if (needPressureValues && line.Length > 0 && pressureColumns is not null)
            {
                string[] values = line.Split(
                    (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (values.Length >= pressureColumns.Count)
                {
                    var parsed = new List<MachineCostResourcePressure>(pressureColumns.Count);
                    for (int i = 0; i < pressureColumns.Count; i++)
                    {
                        double value = values[i] == "-"
                            ? 0.0
                            : double.Parse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture);
                        int index = pressureColumns[i];
                        parsed.Add(new MachineCostResourcePressure(
                            resourceNames.TryGetValue(index, out string? name) ? name : $"resource[{index}]",
                            value));
                    }
                    pressure = parsed;
                }
                needPressureValues = false;
            }
        }

        return new McaSummary(
            instructions, cycles, uops, dispatch, uopsPerCycle, ipc, throughput, pressure);
    }

    private static bool TryLong(string line, string label, out long value)
    {
        value = default;
        return line.StartsWith(label, StringComparison.Ordinal)
            && long.TryParse(line[label.Length..].Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value);
    }

    private static bool TryInt(string line, string label, out int value)
    {
        value = default;
        return line.StartsWith(label, StringComparison.Ordinal)
            && int.TryParse(line[label.Length..].Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value);
    }

    private static bool TryDouble(string line, string label, out double value)
    {
        value = default;
        return line.StartsWith(label, StringComparison.Ordinal)
            && double.TryParse(line[label.Length..].Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value);
    }

    private static string ResolveWorkRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("LAPLACE_WORK_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(Path.Combine(configured.Trim(), "machine-cost"));

        string? processScratch = Environment.GetEnvironmentVariable("TMPDIR");
        if (!OperatingSystem.IsWindows()
            && !string.IsNullOrWhiteSpace(processScratch)
            && Path.IsPathRooted(processScratch))
            return Path.GetFullPath(Path.Combine(processScratch.Trim(), "machine-cost"));

        return OperatingSystem.IsWindows()
            ? Path.Combine(LaplaceInstall.DefaultBuildRoot, "work", "machine-cost")
            : "/build/laplace/work/api/machine-cost";
    }

    private static MachineCostAnalysisException ToolUnavailable(string tool) =>
        new("machine_cost_toolchain_unavailable",
            $"Required LLVM tool '{tool}' is unavailable.",
            serviceUnavailable: true);

    private static string TrimDiagnostic(string value)
    {
        value = value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return value.Length <= DiagnosticLimit ? value : value[..DiagnosticLimit] + "\n[truncated]";
    }

    [GeneratedRegex(@"\s+<[^>\r\n]+>\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingSymbolRegex();

    [GeneratedRegex(@"^\[(\d+)\]\s+-\s+(.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceDefinitionRegex();

    [GeneratedRegex(@"\[(\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceIndexRegex();
}

internal sealed class MachineCostAnalysisException(
    string code, string message, bool serviceUnavailable) : Exception(message)
{
    public string Code { get; } = code;
    public bool ServiceUnavailable { get; } = serviceUnavailable;
}
