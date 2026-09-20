using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Structured;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Cli;

internal static class CookbookCommands
{
    internal static int Run(string[] args)
    {
        if (args.Length == 2 && args[0] == "inspect")
        {
            SemanticSourceRecipe recipe = new LaplaceCookbook().InstallJson(args[1]);
            Console.WriteLine(
                $"recipe={recipe.RecipeId} authority={recipe.Authority} release={recipe.Release} "
                + $"provider={recipe.Provider} syntax={recipe.Syntax} "
                + $"fields={recipe.Fields.Count} structures={recipe.Structures.Count}");
            return 0;
        }

        if (args.Length >= 4 && args[0] == "materialize")
            return Materialize(args);
        if (args.Length >= 3 && args[0] == "ingest")
            return IngestAsync(args).GetAwaiter().GetResult();
        if (args.Length == 3 && args[0] == "inventory")
            return InventoryAsync(args).GetAwaiter().GetResult();
        if (args.Length == 3 && args[0] == "ingest-source")
            return IngestSourceAsync(args).GetAwaiter().GetResult();

        Console.Error.WriteLine(
            "usage: cookbook inspect <source-generation.recipe.json>\n"
            + "       cookbook materialize <recipe.json> <input-file> <output-directory>\n"
            + "       cookbook ingest <recipe.json> <input-file>\n"
            + "       cookbook inventory <generation.json> <source-root>\n"
            + "       cookbook ingest-source <generation.json> <source-root>\n"
            + "         [--source-id <32-hex-bytes> | --source-name <canonical-source-name>]\n"
            + "         [--trust <0..1>] [--record-depth <depth>]");
        return 2;
    }

    private static int Materialize(string[] args)
    {
        SemanticSourceRecipe recipe = new LaplaceCookbook().InstallJson(args[1]);
        var options = ParseOptions(args, 4, recipe);
        Hash128 witness = options.SourceId;
        double trust = options.Trust;
        int recordDepth = options.RecordDepth;
        if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.Load(CliRuntime.ResolveBlob());
        byte[] program = NativeRecipeCompiler.Compile(recipe, recordDepth);
        return Materialize(args, recipe, program, witness, trust, recordDepth);
    }

    private sealed record RecipeOptions(Hash128 SourceId, string SourceName, double Trust, int RecordDepth);

    private static RecipeOptions ParseOptions(string[] args, int start, SemanticSourceRecipe recipe)
    {
        Hash128 witness = Hash128.OfCanonical(recipe.Authority);
        string sourceName = recipe.Authority;
        bool sourceSpecified = false;
        double trust = 1;
        int recordDepth = 2;
        for (int i = start; i < args.Length; i += 2)
        {
            if (i + 1 == args.Length)
                throw new ArgumentException($"Missing value for {args[i]}.");
            switch (args[i])
            {
                case "--source-id":
                case "--source-name":
                    if (sourceSpecified)
                        throw new ArgumentException("Specify one witness source.");
                    sourceSpecified = true;
                    if (args[i] == "--source-name")
                    {
                        sourceName = args[i + 1];
                        witness = SubstrateCanonicalIds.Source(args[i + 1]);
                    }
                    else
                    {
                        byte[] bytes = Convert.FromHexString(args[i + 1]);
                        if (bytes.Length != 16)
                            throw new ArgumentException("Source id must contain exactly 32 hexadecimal characters.");
                        witness = Hash128.FromBytes(bytes);
                    }
                    break;
                case "--trust":
                    trust = double.Parse(args[i + 1], CultureInfo.InvariantCulture);
                    if (!double.IsFinite(trust) || trust < 0 || trust > 1)
                        throw new ArgumentException("Trust must be between zero and one.");
                    break;
                case "--record-depth":
                    recordDepth = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                    if (recordDepth < 0)
                        throw new ArgumentException("Record depth must not be negative.");
                    break;
                default:
                    throw new ArgumentException($"Unknown cookbook option: {args[i]}.");
            }
        }

        return new(witness, sourceName, trust, recordDepth);
    }

    private static int Materialize(string[] args, SemanticSourceRecipe recipe, byte[] program,
        Hash128 witness, double trust, int recordDepth)
    {
        string outputDirectory = Path.GetFullPath(args[3]);
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new IOException($"Output directory is not empty: {outputDirectory}");
        using var input = new FileStream(args[2], FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        using NativeRecipeStream stream = NativeRecipeStream.Open(program, witness, trust);
        Directory.CreateDirectory(outputDirectory);
        using var entities = new TupleOutput(outputDirectory, "entities", IntentStage.CopyColumnList(IntentStageTable.Entities));
        using var physicalities = new TupleOutput(outputDirectory, "physicalities", IntentStage.CopyColumnList(IntentStageTable.Physicalities));
        using var attestations = new TupleOutput(outputDirectory, "attestations", IntentStage.CopyColumnList(IntentStageTable.Attestations));
        using var interpretations = new TupleOutput(outputDirectory, "entity_interpretations", IntentStage.CopyColumnList(IntentStageTable.Entities));
        using var inputHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var stopwatch = Stopwatch.StartNew();
        byte[] buffer = new byte[64 * 1024];
        long inputBytes = 0;
        ulong records = 0;
        long batches = 0;
        bool interpretationsComplete = true;
        bool cancelled = false;
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; Volatile.Write(ref cancelled, true); };
        Console.CancelKeyPress += cancel;
        try
        {
            int read;
            while ((read = input.Read(buffer)) != 0)
            {
                if (Volatile.Read(ref cancelled)) throw new OperationCanceledException("Materialization cancelled.");
                inputHash.AppendData(buffer.AsSpan(0, read));
                inputBytes += read;
                stream.Feed(buffer.AsSpan(0, read), final: false);
                Drain();
            }
            stream.Feed([], final: true);
            Drain();
            var receipt = new
            {
                format = "laplace-native-recipe-tuples/v1",
                completed = true,
                recipeId = Hex(recipe.RecipeId),
                recipe.Authority,
                recipe.Release,
                recipe.Provider,
                recipe.Syntax,
                programSha256 = Convert.ToHexStringLower(SHA256.HashData(program)),
                input = Path.GetFullPath(args[2]),
                inputBytes,
                inputSha256 = Convert.ToHexStringLower(inputHash.GetHashAndReset()),
                witnessSourceId = Hex(witness),
                physicalityProvenanceSourceId = Hex(witness),
                trust,
                recordDepth,
                records,
                batches,
                entityInterpretationsComplete = interpretationsComplete,
                tupleFraming = "PostgreSQL binary COPY tuples without header or trailer",
                elapsedSeconds = stopwatch.Elapsed.TotalSeconds,
                outputs = new[] { entities.Complete(), physicalities.Complete(), attestations.Complete(), interpretations.Complete() }
            };
            string json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(outputDirectory, "receipt.json"), json + Environment.NewLine);
            Console.WriteLine(json);
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }

        void Drain()
        {
            while (true)
            {
                if (Volatile.Read(ref cancelled)) throw new OperationCanceledException("Materialization cancelled.");
                using IntentStage? stage = stream.Drain(32768, 32L * 1024 * 1024, out ulong completed);
                records += completed;
                if (stage is null) return;
                batches++;
                entities.Append(stage.TupleBuffer(IntentStageTable.Entities), stage.EntityCount);
                physicalities.Append(stage.TupleBuffer(IntentStageTable.Physicalities), stage.PhysicalityCount);
                attestations.Append(stage.TupleBuffer(IntentStageTable.Attestations), stage.AttestationCount);
                interpretations.Append(stage.EntityInterpretationTupleBuffer(), stage.EntityInterpretationCount);
                interpretationsComplete &= stage.EntityInterpretationsComplete;
                GC.KeepAlive(stage);
            }
        }
    }

    private static string Hex(Hash128 id) => Convert.ToHexStringLower(id.ToBytes());

    private static async Task<int> IngestAsync(string[] args)
    {
        SemanticSourceRecipe recipe = new LaplaceCookbook().InstallJson(args[1]);
        RecipeOptions options = ParseOptions(args, 3, recipe);
        string inputPath = Path.GetFullPath(args[2]);
        if (!File.Exists(inputPath)) throw new FileNotFoundException("Recipe input does not exist.", inputPath);
        await using var decomposer = new SingleArtifactRecipeDecomposer(recipe,
            new RecipeExecutionOptions(options.SourceId, options.SourceName, options.Trust, options.RecordDepth), inputPath);
        return await IngestCommands.IngestViaRunnerAsync(decomposer, inputPath,
            skipLayerCheck: true, skipSourceCompletion: true).ConfigureAwait(false);
    }

    private static async Task<int> InventoryAsync(string[] args)
    {
        SourceGenerationRecipe recipe = SourceGenerationRecipe.Load(args[1]);
        ResolvedSourceGeneration resolved = await SourceGenerationResolver.ResolveAsync(recipe, args[2]).ConfigureAwait(false);
        RecipeSyntaxProviderRegistry providers = RecipeSyntaxProviderRegistry.CreateDefault();
        var bindings = resolved.Bindings.ToDictionary(static binding => binding.Artifact.Path, StringComparer.Ordinal);
        string[] executionErrors = resolved.ExecutionErrors
            .Concat(resolved.Graph.Artifacts.Where(static artifact => artifact.Disposition == IngestArtifactDisposition.Unsupported)
                .Select(static artifact => $"Unresolved artifact '{artifact.RelativePath}': {artifact.Notes}"))
            .Concat(resolved.Bindings
            .Where(binding => !providers.Supports(binding.Provider))
            .Select(static binding => $"No executable syntax provider is registered for '{binding.Provider}' ({binding.Artifact.RelativePath})."))
            .Distinct(StringComparer.Ordinal).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            generationId = Hex(resolved.GenerationId), recipe.Authority, recipe.Release,
            sourceId = Hex(recipe.SourceId), recipe.SourceName, root = resolved.Root,
            selectedFiles = resolved.Graph.Selected.Count,
            physicalFiles = resolved.Graph.Artifacts.Count(static artifact => artifact.Disposition != IngestArtifactDisposition.Absent),
            bytes = resolved.Graph.Artifacts.Sum(static artifact => artifact.Bytes ?? 0),
            executable = executionErrors.Length == 0,
            executionErrors,
            artifacts = resolved.Graph.Artifacts.Select(artifact =>
            {
                bindings.TryGetValue(artifact.Path, out ResolvedSourceArtifact? binding);
                return new
                {
                    artifact = artifact.RelativePath, disposition = artifact.DispositionName,
                    artifact.Bytes, artifact.Sha256, reason = artifact.Notes,
                    provider = binding?.Provider, recipeId = binding?.Recipe is { } semantic ? Hex(semantic.RecipeId) : null,
                    dependencies = binding?.Dependencies.Select(Hex).ToArray() ?? [],
                };
            }),
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static async Task<int> IngestSourceAsync(string[] args)
    {
        SourceGenerationRecipe recipe = SourceGenerationRecipe.Load(args[1]);
        await using var decomposer = new Laplace.Decomposers.Structured.Decomposer<SourceGenerationRecipe>(recipe, args[2]);
        return await IngestCommands.IngestViaRunnerAsync(decomposer, args[2],
            skipLayerCheck: true, skipSourceCompletion: true).ConfigureAwait(false);
    }

    private sealed class TupleOutput : IDisposable
    {
        private readonly FileStream _stream;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly string _name;
        private readonly string _columns;
        private long _rows;

        internal TupleOutput(string directory, string name, string columns)
        {
            _name = name + ".tuples";
            _columns = columns;
            _stream = new FileStream(Path.Combine(directory, _name), FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        }

        internal unsafe void Append((IntPtr Pointer, long Length) tupleBuffer, int rows)
        {
            byte* pointer = (byte*)tupleBuffer.Pointer;
            long remaining = tupleBuffer.Length;
            while (remaining > 0)
            {
                int length = (int)Math.Min(remaining, 1024 * 1024);
                var bytes = new ReadOnlySpan<byte>(pointer, length);
                _stream.Write(bytes);
                _hash.AppendData(bytes);
                pointer += length;
                remaining -= length;
            }
            _rows += rows;
        }

        internal object Complete()
        {
            _stream.Flush(flushToDisk: true);
            return new { file = _name, columns = _columns, rows = _rows, bytes = _stream.Length,
                sha256 = Convert.ToHexStringLower(_hash.GetHashAndReset()) };
        }

        public void Dispose()
        {
            _stream.Dispose();
            _hash.Dispose();
        }
    }
}
