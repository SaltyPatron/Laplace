using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Laplace.Endpoints.OpenAICompat;

internal static partial class CodeToolchain
{
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(30);
    private const int DiagnosticLimit = 16 * 1024;

    internal sealed record Receipt(
        string Modality,
        string Tool,
        bool ToolAvailable,
        bool Verified,
        bool TimedOut,
        int ExitCode,
        string Stdout,
        string Stderr)
    {
        public string CanonicalJson => JsonSerializer.Serialize(new
        {
            schema = "laplace.code-toolchain/v1",
            modality = Modality,
            tool = Tool,
            tool_available = ToolAvailable,
            verified = Verified,
            timed_out = TimedOut,
            exit_code = ExitCode,
            stdout = Stdout,
            stderr = Stderr,
        });
    }

    private sealed record Invocation(
        string Tool,
        string FileName,
        IReadOnlyList<string> Arguments,
        string? BootstrapFileName = null,
        string? BootstrapContents = null,
        IReadOnlyList<string>? BootstrapArguments = null);

    public static async Task<Receipt> VerifyAsync(string source, string modality, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(modality);

        if (string.Equals(modality, "json", StringComparison.Ordinal))
        {
            try
            {
                using var _ = JsonDocument.Parse(source);
                return new Receipt(modality, "System.Text.Json", true, true, false, 0, "", "");
            }
            catch (JsonException ex)
            {
                return new Receipt(modality, "System.Text.Json", true, false, false, 1, "", Trim(ex.Message));
            }
        }

        var invocation = ResolveInvocation(source, modality)
            ?? return new Receipt(modality, "", false, false, false, 127, "",
                $"No compile/syntax verifier is governed for modality '{modality}'.");

        var work = Path.Combine(Path.GetTempPath(), "laplace-code", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(work, invocation.FileName), source, Encoding.UTF8, ct);
            if (invocation.BootstrapFileName is { } bootstrapName && invocation.BootstrapContents is { } bootstrapContents)
            {
                await File.WriteAllTextAsync(Path.Combine(work, bootstrapName), bootstrapContents, Encoding.UTF8, ct);
                if (invocation.BootstrapArguments is { Count: > 0 } bootstrapArgs)
                {
                    var prepared = await RunAsync(invocation.Tool, bootstrapArgs, work, ct);
                    if (!prepared.ToolAvailable || prepared.TimedOut || prepared.ExitCode != 0)
                        return new Receipt(modality, invocation.Tool, prepared.ToolAvailable, false,
                            prepared.TimedOut, prepared.ExitCode, prepared.Stdout, prepared.Stderr);
                }
            }

            var result = await RunAsync(invocation.Tool, invocation.Arguments, work, ct);
            return new Receipt(modality, invocation.Tool, result.ToolAvailable,
                result.ToolAvailable && !result.TimedOut && result.ExitCode == 0,
                result.TimedOut, result.ExitCode, result.Stdout, result.Stderr);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    private static Invocation? ResolveInvocation(string source, string modality) => modality switch
    {
        "python" => new("python3", "candidate.py", ["-I", "-m", "py_compile", "candidate.py"]),
        "c" => new("cc", "candidate.c", ["-std=c17", "-Wall", "-Wextra", "-c", "candidate.c", "-o", "candidate.o"]),
        "cpp" => new("c++", "candidate.cpp", ["-std=c++20", "-Wall", "-Wextra", "-c", "candidate.cpp", "-o", "candidate.o"]),
        "javascript" => new("node", "candidate.js", ["--check", "candidate.js"]),
        "typescript" => new("npx", "candidate.ts", ["--no-install", "tsc", "--pretty", "false", "--noEmit", "--target", "ES2022", "--module", "NodeNext", "--moduleResolution", "NodeNext", "candidate.ts"]),
        "rust" => new("rustc", "candidate.rs", ["--edition=2021", "--crate-type=lib", "candidate.rs", "-o", "candidate.rlib"]),
        "go" => new("go", "candidate.go", ["tool", "compile", "candidate.go"]),
        "bash" => new("bash", "candidate.sh", ["-n", "candidate.sh"]),
        "ruby" => new("ruby", "candidate.rb", ["-c", "candidate.rb"]),
        "php" => new("php", "candidate.php", ["-l", "candidate.php"]),
        "java" => JavaInvocation(source),
        "c-sharp" => new(
            "dotnet", "Candidate.cs",
            ["build", "candidate.csproj", "--no-restore", "--nologo", "--verbosity", "quiet"],
            "candidate.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>",
            ["restore", "candidate.csproj", "--ignore-failed-sources", "--nologo"]),
        "cuda" => new("nvcc", "candidate.cu", ["-std=c++17", "-c", "candidate.cu", "-o", "candidate.o"]),
        "fortran" => new("gfortran", "candidate.f90", ["-fsyntax-only", "candidate.f90"]),
        "swift" => new("swiftc", "candidate.swift", ["-parse", "candidate.swift"]),
        "zig" => new("zig", "candidate.zig", ["ast-check", "candidate.zig"]),
        "llvm" => new("llvm-as", "candidate.ll", ["candidate.ll", "-o", "candidate.bc"]),
        "ispc" => new("ispc", "candidate.ispc", ["--syntax-only", "candidate.ispc"]),
        "nasm" => new("nasm", "candidate.asm", ["-f", "elf64", "candidate.asm", "-o", "candidate.o"]),
        "asm" => new("cc", "candidate.s", ["-c", "candidate.s", "-o", "candidate.o"]),
        _ => null,
    };

    private static Invocation JavaInvocation(string source)
    {
        var match = PublicJavaType().Match(source);
        var file = match.Success ? match.Groups[1].Value + ".java" : "Candidate.java";
        return new Invocation("javac", file, ["-proc:none", "-d", "out", file]);
    }

    [GeneratedRegex(@"\bpublic\s+(?:class|interface|record|enum)\s+([A-Za-z_$][A-Za-z0-9_$]*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex PublicJavaType();

    private sealed record ProcessReceipt(bool ToolAvailable, bool TimedOut, int ExitCode, string Stdout, string Stderr);

    private static async Task<ProcessReceipt> RunAsync(string tool, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken ct)
    {
        var start = new ProcessStartInfo(tool)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["npm_config_yes"] = "false";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";

        Process? process;
        try { process = Process.Start(start); }
        catch (Win32Exception)
        {
            return new ProcessReceipt(false, false, 127, "", $"Tool '{tool}' is not installed or not on PATH.");
        }
        if (process is null)
            return new ProcessReceipt(false, false, 127, "", $"Tool '{tool}' did not start.");

        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(CompileTimeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                await Task.WhenAll(stdoutTask, stderrTask);
                return new ProcessReceipt(true, true, 124, Trim(await stdoutTask), Trim(await stderrTask));
            }

            await Task.WhenAll(stdoutTask, stderrTask);
            return new ProcessReceipt(true, false, process.ExitCode, Trim(await stdoutTask), Trim(await stderrTask));
        }
    }

    private static string Trim(string value)
    {
        value = value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (value.Length <= DiagnosticLimit) return value;
        return value[..DiagnosticLimit] + "\n…[truncated]";
    }
}
