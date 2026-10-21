using System.Text;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class CodeToolchainTests
{
    [Fact]
    public async Task CancellationTerminatesCompilerBeforeReturningCancellation()
    {
        var work = Path.Combine(Path.GetTempPath(), "laplace-tool-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        using var cancel = new CancellationTokenSource();
        int pid = 0;
        try
        {
            string tool = OperatingSystem.IsWindows() ? "pwsh" : "/bin/sh";
            string[] args = OperatingSystem.IsWindows()
                ? ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "[IO.File]::WriteAllText('child.pid', [string]$PID); Start-Sleep -Seconds 60"]
                : ["-c", "echo $$ > child.pid; sleep 60"];
            var running = CodeToolchain.RunAsync(tool, args, work, cancel.Token);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var pidFile = Path.Combine(work, "child.pid");
            while (!File.Exists(pidFile)) await Task.Delay(20, deadline.Token);
            pid = int.Parse(await File.ReadAllTextAsync(pidFile, deadline.Token));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await running.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(Exited(pid), "Compiler was left alive after request cancellation.");
        }
        finally
        {
            if (pid > 0 && !Exited(pid))
            {
                using var leaked = System.Diagnostics.Process.GetProcessById(pid);
                leaked.Kill(entireProcessTree: true);
                leaked.WaitForExit(5000);
            }
            Directory.Delete(work, true);
        }
    }

    [Fact]
    public async Task CompilerTimeoutReturnsFailureWithCapturedDiagnostic()
    {
        var result = await CodeToolchain.RunAsync(
            OperatingSystem.IsWindows() ? "pwsh" : "/bin/sh",
            OperatingSystem.IsWindows()
                ? ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "[Console]::Error.WriteLine('compile-fixture'); Start-Sleep -Seconds 60"]
                : ["-c", "echo compile-fixture >&2; sleep 60"],
            Path.GetTempPath(), CancellationToken.None, TimeSpan.FromSeconds(2));
        Assert.True(result.TimedOut);
        Assert.Equal(124, result.ExitCode);
        Assert.Contains("compile-fixture", result.Stderr);
    }

    private static bool Exited(int pid)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    [Fact]
    public async Task JsonVerificationProducesTreeAndToolchainReceipts()
    {
        const string valid = "{\"value\":1}";
        using var validAst = GrammarDecomposer.Parse(Encoding.UTF8.GetBytes(valid), "json");
        var accepted = await CodeToolchain.VerifyAsync(
            valid, "json", validAst.Diagnostics, CancellationToken.None);
        Assert.True(accepted.ToolAvailable);
        Assert.True(accepted.SyntaxComplete);
        Assert.True(accepted.Verified);
        Assert.Equal(0, accepted.ExitCode);
        Assert.Contains("laplace.code-toolchain/v2", accepted.CanonicalJson, StringComparison.Ordinal);

        const string invalid = "{\"value\":}";
        using var invalidAst = GrammarDecomposer.Parse(Encoding.UTF8.GetBytes(invalid), "json");
        var rejected = await CodeToolchain.VerifyAsync(
            invalid, "json", invalidAst.Diagnostics, CancellationToken.None);
        Assert.True(rejected.ToolAvailable);
        Assert.False(rejected.Verified);
        Assert.NotEqual(0, rejected.ExitCode);
        Assert.NotEmpty(rejected.Stderr);
    }

    [Theory]
    [InlineData("py", "python")]
    [InlineData("c#", "c-sharp")]
    [InlineData("rs", "rust")]
    [InlineData("ts", "typescript")]
    public void CodeConstructionUsesNativeGrammarRegistry(string requested, string expected)
    {
        Assert.True(CodePlayerService.TryNormalizeModality(requested, out var modality));
        Assert.Equal(expected, modality);
    }

    [Fact]
    public void CodeCompatibilitySelectorHasGovernedIdWithoutModelCatalogPromotion()
    {
        Assert.True(ModelCatalog.IsCode("laplace-code-001"));
        Assert.DoesNotContain(ModelCatalog.All, model => model.Id == ModelCatalog.Code);
    }
}
