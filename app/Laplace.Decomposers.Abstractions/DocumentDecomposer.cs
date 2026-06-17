using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;





public sealed class DocumentDecomposer : IDecomposer, IIngestInventoryProvider
{
    public Hash128 SourceId     => UserPromptContent.Source;
    
    
    
    public string  SourceName   => "UserPrompt";
    public int     LayerOrder   => 2;
    public Hash128 TrustClassId => UserPromptContent.TrustClass;

    public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        => context.Writer.ApplyAsync(UserPromptContent.BuildBootstrapChange(), ct);

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string root = context.EcosystemPath;
        if (string.IsNullOrEmpty(root)) yield break;

        foreach (string file in EnumerateInputFiles(root))
        {
            ct.ThrowIfCancellationRequested();

            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, ct); }
            catch { continue; }
            if (bytes.Length == 0) continue;

            string label = File.Exists(root)
                ? $"document/{Path.GetFileName(file)}"
                : $"document/{Path.GetRelativePath(root, file).Replace('\\', '/')}";

            if (!UserPromptContent.TryBuildWitnessChange(bytes, label, out var change, out _))
                continue;

            if (!options.DryRun) yield return change;
        }
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var files = EnumerateInputFiles(context.EcosystemPath).ToList();
        if (files.Count == 0) return Task.FromResult<IngestInventory?>(null);

        var specs = files.Select(f => new IngestFileSpec(Path.GetFileName(f), f, 1)).ToList();
        return Task.FromResult<IngestInventory?>(new IngestInventory("documents", files.Count, specs));
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        long n = EnumerateInputFiles(context.EcosystemPath).LongCount();
        return Task.FromResult<long?>(n == 0 ? null : n);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal static IEnumerable<string> EnumerateInputFiles(string path)
    {
        if (string.IsNullOrEmpty(path)) yield break;

        if (File.Exists(path))
        {
            yield return Path.GetFullPath(path);
            yield break;
        }

        if (!Directory.Exists(path)) yield break;

        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                                         .OrderBy(p => p, StringComparer.Ordinal))
            yield return file;
    }
}
