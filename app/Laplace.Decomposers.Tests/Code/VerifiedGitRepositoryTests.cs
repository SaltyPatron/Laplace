using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Laplace.Decomposers.Code;
using Xunit;

namespace Laplace.Decomposers.Tests.Code;

public sealed class VerifiedGitRepositoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "verified-git-test-" + Guid.NewGuid().ToString("N"));
    private VerifiedGitRepository.Selection selection;

    public VerifiedGitRepositoryTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Git("init", "-q"); Git("config", "user.name", "Corpus fixture"); Git("config", "user.email", "fixture@example.invalid");
        Git("remote", "add", "origin", "https://github.com/official-stockfish/Stockfish.git");
        File.WriteAllText(Path.Combine(root, "src", "sample.cpp"), "int main() { return 0; }\n");
        File.WriteAllText(Path.Combine(root, "COPYING"), "Fixture license bytes\n");
        File.WriteAllBytes(Path.Combine(root, "unknown.bin"), [0, 255, 2]);
        Git("add", "src/sample.cpp", "COPYING", "unknown.bin"); Git("commit", "-qm", "fixture source");
        string binary = Path.Combine(root, "src", OperatingSystem.IsWindows() ? "stockfish.exe" : "stockfish");
        File.WriteAllText(binary, "fixture executable identity only; never executed");
        string receipt = Path.Combine(root, ".git", "laplace-stockfish-build.json");
        string commit = Git("rev-parse", "HEAD");
        File.WriteAllText(receipt, JsonSerializer.Serialize(new {
            recipe = new { commit, arch = "fixture", cpu = "fixture", compiler = "fixture", compiler_version = "fixture" },
            binary_sha256 = VerifiedGitRepository.HashFile(binary), private_secret = "must-not-be-public" }));
        selection = new("https://github.com/official-stockfish/Stockfish", commit, "fixture-license", binary, receipt, "cpp");
    }

    private VerifiedGitRepository Capture() => VerifiedGitRepository.Capture(root, selection,
        (file, bytes) => file.EndsWith(".cpp", StringComparison.Ordinal) ? ("cpp", null) : (null, "No fixture grammar for this artifact."));

    [Fact]
    public void CapturesEveryTrackedEntry_AndSeparatesActualAdmissionFromHashes()
    {
        var snapshot = Capture();
        Assert.Equal(3, snapshot.Entries.Count); Assert.Single(snapshot.Graph.Selected);
        Assert.Equal(2, snapshot.Entries.Count(e => e.Disposition == "unsupported-with-why-not"));
        Assert.All(snapshot.Entries, e => Assert.Equal(64, e.Sha256.Length));
        Assert.DoesNotContain("must-not-be-public", Encoding.UTF8.GetString(snapshot.ProvenanceUtf8));
        Assert.DoesNotContain(root, Encoding.UTF8.GetString(snapshot.ProvenanceUtf8));
        Assert.DoesNotContain(snapshot.Entries, e => e.Path.EndsWith("stockfish", StringComparison.Ordinal));
        Assert.Equal(snapshot.ProvenanceUtf8, Capture().ProvenanceUtf8);
        Assert.False(File.Exists(Path.Combine(root, "MANIFEST.tsv")));
    }

    [Fact]
    public void LocalEditCannotMasqueradeAsCommittedSource()
    {
        File.AppendAllText(Path.Combine(root, "src", "sample.cpp"), "// local change\n");
        Assert.Throws<InvalidDataException>(() => Capture());
    }

    [Fact]
    public void LocalReplacementCommitCannotRedefineOfficialTrackedTree()
    {
        File.AppendAllText(Path.Combine(root, "src", "sample.cpp"), "// local replacement\n");
        Git("add", "src/sample.cpp"); Git("commit", "-qm", "replacement fixture");
        string replacement = Git("rev-parse", "HEAD");
        Git("update-ref", "HEAD", selection.Commit);
        Git("replace", selection.Commit, replacement);
        Assert.Throws<InvalidDataException>(() => Capture());
    }

    [Fact]
    public void ExactRawFileSelectionDoesNotClaimNativeGrammarRepresentation()
    {
        var snapshot = VerifiedGitRepository.Capture(root, selection, (file, bytes) =>
            file.EndsWith(".cpp", StringComparison.Ordinal) ? ("cpp", null)
            : file.EndsWith("COPYING", StringComparison.Ordinal) ? ("text", null)
            : (null, "Unsupported fixture bytes."));
        Assert.Equal(2, snapshot.Graph.Selected.Count);
        Assert.Equal("raw-text", Assert.Single(snapshot.Entries, e => e.Path == "COPYING").Representation);
        Assert.Equal("native-cst", Assert.Single(snapshot.Entries, e => e.Path.EndsWith(".cpp", StringComparison.Ordinal)).Representation);
        Assert.Equal("unadmitted", Assert.Single(snapshot.Entries, e => e.Path == "unknown.bin").Representation);
    }

    [Fact]
    public void MutationAfterObservationRejectsReadbackAndFinalVerification()
    {
        var snapshot = Capture();
        File.AppendAllText(Path.Combine(root, "COPYING"), "changed even though unadmitted\n");
        Assert.Throws<InvalidDataException>(() => snapshot.VerifyUnchanged());
    }

    [Fact]
    public void WrongEngineBytesRejectBuildLink()
    {
        File.AppendAllText(selection.BinaryPath, "changed");
        Assert.Throws<InvalidDataException>(() => Capture());
    }

    [Fact]
    public void WrongCommitRejectsBeforeAdmission()
    {
        selection = selection with { Commit = new string('a', 40) };
        Assert.Throws<InvalidDataException>(() => Capture());
    }

    [Fact]
    public void WrongOriginRejectsWithoutExposingRemoteCredentials()
    {
        Git("remote", "set-url", "origin", "https://secret-user:secret-password@example.invalid/private");
        var error = Assert.Throws<InvalidDataException>(() => Capture());
        Assert.DoesNotContain("secret-password", error.Message);
    }

    [Fact]
    public void CppProviderIsRequiredEvenWhenAnotherGrammarIsAvailable()
    {
        Assert.Throws<InvalidDataException>(() => VerifiedGitRepository.Capture(root, selection,
            (file, bytes) => file.EndsWith("COPYING", StringComparison.Ordinal) ? ("text", null) : (null, "No C++ provider.")));
    }

    [Fact]
    public void ReceiptMustComeFromSelectedCheckoutGitMetadata()
    {
        selection = selection with { BuildReceiptPath = Path.Combine(root, "receipt.json") };
        Assert.Throws<InvalidDataException>(() => Capture());
    }

    [Fact]
    public void PrivateReceiptChangesInvalidateCapturedObservation()
    {
        var snapshot = Capture();
        File.AppendAllText(selection.BuildReceiptPath, "\n");
        Assert.Throws<InvalidDataException>(() => snapshot.VerifyUnchanged());
    }

    [Fact]
    public void RegularFileReplacedBySymlinkIsRejected()
    {
        if (OperatingSystem.IsWindows()) return;
        string file = Path.Combine(root, "src", "sample.cpp");
        File.Delete(file); File.CreateSymbolicLink(file, "../COPYING");
        Assert.Throws<InvalidDataException>(() => Capture());
    }

    [Fact]
    public void UntrackedNotesDoNotChangeSelectedCommitOrProvenance()
    {
        var first = Capture();
        File.WriteAllText(Path.Combine(root, "operator-note.txt"), "outside committed selection");
        Assert.Equal(first.ProvenanceUtf8, Capture().ProvenanceUtf8);
    }

    private string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd();
        process.WaitForExit(); Assert.True(process.ExitCode == 0, error); return output.Trim();
    }
    public void Dispose() => Directory.Delete(root, recursive: true);
}
