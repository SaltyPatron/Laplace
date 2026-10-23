using System.Text;
using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Tests.Structured;

internal sealed class NativeCorpus : IDisposable
{
    private readonly string _parent = Path.GetFullPath(Path.GetTempPath());
    public string Root { get; } = Directory.CreateTempSubdirectory("laplace-native-corpus-").FullName;
    public NativeRecipeExecution Execution { get; } = NativeRecipeExecution.Configured();

    public string Write(string name, string content)
    {
        string path = Path.GetFullPath(Path.Combine(Root, name));
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Fixture path leaves its directory.", nameof(name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false, true));
        return path;
    }

    public Task<Result> Claims(string source, string path, int threads = 2)
        => Run(Execution, NativeRecipeMode.Claims, source, path, threads);

    public static async Task<Result> Run(NativeRecipeExecution execution, NativeRecipeMode mode,
        string source, string path, int threads = 2)
    {
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        int code = await execution.ExecuteAsync(new NativeRecipeRequest(mode, source, [path],
            "host=127.0.0.1 port=1 dbname=unavailable connect_timeout=1", threads), output, error);
        return new(code, Encoding.UTF8.GetString(output.ToArray()), Encoding.UTF8.GetString(error.ToArray()));
    }

    public void Dispose()
    {
        string resolved = Path.GetFullPath(Root);
        if (!string.Equals(Directory.GetParent(resolved)!.FullName, _parent.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("laplace-native-corpus-", StringComparison.Ordinal))
            throw new InvalidOperationException("Native fixture directory is outside its temporary parent.");
        Directory.Delete(resolved, recursive: true);
    }

    internal sealed record Result(int ExitCode, string Output, string Error)
    {
        public string[] Lines(string kind) => Output.Split('\n').Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith(kind + "\t", StringComparison.Ordinal)).ToArray();
        public string[] Claims()
        {
            Assert.True(ExitCode == 0, Error + "\n" + Output);
            Assert.True(Output.Contains("unresolved obligations: 0", StringComparison.Ordinal), Error + "\n" + Output);
            return Lines("claim");
        }
    }
}
