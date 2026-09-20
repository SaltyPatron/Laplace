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

        Console.Error.WriteLine(
            "usage: cookbook inspect <source-generation.recipe.json>\n"
            + "       cookbook materialize <recipe.json> <input.xml> <output-directory>\n"
            + "       cookbook ingest <recipe.json> <input.xml>\n"
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
        await using var decomposer = new CookbookXmlDecomposer(recipe, options, inputPath);
        return await IngestCommands.IngestViaRunnerAsync(decomposer, inputPath,
            skipLayerCheck: true, skipSourceCompletion: true).ConfigureAwait(false);
    }

    private sealed class CookbookXmlDecomposer : IDecomposer, IIngestArtifactGraphProvider,
        IIngestInventoryProvider, IIgnoresAmbientArtifactManifest
    {
        private readonly SemanticSourceRecipe _recipe;
        private readonly RecipeOptions _options;
        private readonly string _inputPath;
        private readonly NativeXmlRecipe _runtime;
        private IReadOnlyCollection<string> _canonicalNames = [];
        private FileStream? _input;
        private IngestArtifact? _artifact;

        internal CookbookXmlDecomposer(SemanticSourceRecipe recipe, RecipeOptions options, string inputPath)
        {
            _recipe = recipe;
            _options = options;
            _inputPath = inputPath;
            _runtime = new NativeXmlRecipe(recipe, options.RecordDepth);
            DeclaredRelations = recipe.Fields
                .Where(static field => field.Disposition.HasFlag(SourceFieldDisposition.Testimony))
                .Select(static field => field.RelationName ?? field.PropertyName)
                .Concat(recipe.Fields.Where(static field => field.PreserveLexicalValue && field.LexicalRelationName is not null)
                    .Select(static field => field.LexicalRelationName!))
                .Concat(recipe.Fields.Where(static field => field.RelationParent is not null)
                    .Select(static field => field.RelationParent!))
                .Concat(recipe.ProviderRoutes.Where(static route => route.RangeRelationName is not null || route.RangeRelationProperty is not null)
                    .Select(static route => route.RangeRelationName ?? route.RangeRelationProperty!))
                .Concat(["HAS_PROPERTY", "HAS_VERSION", "IS_TYPED_AS", "HAS_NAME_ALIAS",
                    "IS_A", "CONTAINS", "REQUIRES", "HAS_SOURCE_URL", "HAS_LICENSE", "HAS_CITATION"])
                .Distinct(StringComparer.Ordinal).ToArray();
        }

        public Hash128 SourceId => _options.SourceId;
        public string SourceName => _options.SourceName;
        public int LayerOrder => 0;
        public bool PerFileCompletion => true;
        public Hash128 TrustClassId => SubstrateCanonicalIds.TrustClass("StructuredCorpus");
        public IReadOnlyList<string> DeclaredRelations { get; }
        public IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

        public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
        {
            string[] typeNames = _recipe.Fields.Select(static field => field.ObjectEntityType)
                .Concat(_recipe.ProviderRoutes.Select(static route => route.Subject.EntityType))
                .Concat(_recipe.Structures.Select(static structure => structure.SemanticType))
                .Concat(["Codepoint", "Recipe_Value", "Recipe_Subject"])
                .Distinct(StringComparer.Ordinal).ToArray();
            BootstrapIntentBuilder boot = await SourceVocabularyBootstrap.RegisterAsync(
                context, SourceId, SourceName, TrustClassId, typeNames, DeclaredRelations, ct: ct)
                .ConfigureAwait(false);
            _canonicalNames = boot.CanonicalNames;
        }

        public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
            IDecomposerContext context, DecomposerOptions options,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            IngestArtifact artifact = _artifact ?? throw new InvalidOperationException("Recipe input has not been inventoried.");
            FileStream input = _input ?? throw new InvalidOperationException("Recipe input has not been opened.");
            SourceArtifactIdentity identity = SourceArtifactProvenance.Resolve(artifact);
            string label = artifact.FileLabel;
            string recipeName = $"cookbook/{Hex(_recipe.RecipeId)}/depth/{_options.RecordDepth}"
                + $"/trust/{BitConverter.DoubleToInt64Bits(_options.Trust):x16}";
            Hash128 generationId = SourceArtifactProvenance.RecipeId(
                SourceName, _recipe.Release, recipeName, [identity.ArtifactId, _recipe.RecipeId]);
            var observability = Laplace.Ingestion.IngestObservabilityScope.Current;
            if (!options.ReObservePresent
                && await context.Reader.HasFileCompletedAsync(generationId, SourceId, LayerOrder, ct).ConfigureAwait(false))
            {
                observability.OnFileComposed(SourceName, label, identity.ArtifactId,
                    resumeFingerprint: generationId);
                yield return IngestBatchPipeline.BuildSkippedBoundary(SourceId, label);
                yield break;
            }
            observability.OnFileStarted(SourceName, label, input.Length);
            yield return IngestBatchPipeline.BindFileLabel(
                SourceArtifactProvenance.BuildChange(artifact, SourceId, TrustClassId), label)
                with { CountsAsUnit = false };
            using (var manifest = new SubstrateChangeBuilder(SourceId, $"cookbook/manifest/{Hex(_recipe.RecipeId)}"))
            {
                manifest.AddEntity(_recipe.RecipeId, EntityTier.Document, EntityTypeRegistry.SourceReference, SourceId);
                if (ContentEmitter.Emit(manifest, _recipe.CanonicalForm, SourceId) is { } contentId)
                    manifest.AddAttestation(NativeAttestation.Categorical(
                        _recipe.RecipeId, "HAS_PROPERTY", contentId, SourceId, _options.Trust));
                manifest.DeclareSourcePrior(_options.Trust);
                yield return IngestBatchPipeline.BindFileLabel(manifest.Build() with { CountsAsUnit = false }, label);
            }
            yield return IngestBatchPipeline.BindFileLabel(
                SourceArtifactProvenance.BuildRecipeChange(SourceName, _recipe.Release,
                    recipeName, SourceId, TrustClassId, [identity.ArtifactId, _recipe.RecipeId]), label);
            long records = 0, entities = 0, physicalities = 0, attestations = 0;
            using var parsedHash = SHA256.Create();
            using var parsedInput = new CryptoStream(input, parsedHash, CryptoStreamMode.Read, leaveOpen: true);
            await foreach (SubstrateChange change in _runtime.ReadChangesAsync(
                               parsedInput, SourceId, _options.Trust, label,
                               IngestSizing.ResolveApplyTransactionRows(),
                               IngestSizing.ResolveWorkingSetFlushEnvelopeBytes(),
                               IngestSizing.ResolveSequentialIoBufferBytes(), ct: ct).ConfigureAwait(false))
            {
                records += change.Metadata.InputUnitsConsumed;
                foreach (IntentStage stage in change.IntentStages)
                {
                    entities += stage.EntityCount;
                    physicalities += stage.PhysicalityCount;
                    attestations += stage.AttestationCount;
                }
                yield return IngestBatchPipeline.BindFileLabel(SourceArtifactProvenance.Bind(change, generationId), label);
            }
            if (parsedHash.Hash is not { } parsedDigest
                || !CryptographicOperations.FixedTimeEquals(parsedDigest, Convert.FromHexString(artifact.Sha256)))
                throw new IOException("Recipe input changed while being parsed; file completion was not recorded.");
            observability.OnFileComposed(SourceName, label, identity.ArtifactId,
                records, entities, physicalities, attestations, resumeFingerprint: generationId);
            yield return IngestBatchPipeline.BuildFileCompletion(SourceId, label, generationId, LayerOrder, _canonicalNames);
        }

        public async Task<IngestArtifactGraph?> DescribeArtifactsAsync(string ecosystemPath,
            DecomposerOptions options, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(_inputPath);
            _input = new FileStream(_inputPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            // Keep the exact same open file handle for fingerprinting and native parsing.
            // The exact-byte identity permits completion reuse without conflating releases.
            string sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(_input, ct).ConfigureAwait(false));
            _input.Position = 0;
            _artifact = new IngestArtifact(SourceName, _recipe.Release, info.Name, info.Name, _inputPath,
                    IngestArtifactDisposition.Admitted, UpstreamUrl: "", FetchedAtUtc: "", Bytes: info.Length,
                    Sha256: sha256, UpstreamChecksum: "", MediaType: "application/xml", License: "",
                    Citation: _recipe.Authority, Language: "", Split: "", AnnotationOrigin: _recipe.Provider,
                    Notes: $"Explicit cookbook input; recipe {Hex(_recipe.RecipeId)}", ModifiedAt: info.LastWriteTimeUtc);
            return new IngestArtifactGraph([_artifact]);
        }

        public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
            => Task.FromResult<long?>(null);
        public Task<IngestInventory?> DescribeInputAsync(IDecomposerContext context,
            DecomposerOptions options, CancellationToken ct = default)
        {
            IngestArtifact artifact = _artifact ?? throw new InvalidOperationException("Recipe input has not been inventoried.");
            return Task.FromResult<IngestInventory?>(new IngestInventory("records", 0,
                [new IngestFileSpec(artifact.FileLabel, _inputPath, InputUnits: 0)], TracksFileCompletion: true));
        }
        public ValueTask DisposeAsync() => _input?.DisposeAsync() ?? ValueTask.CompletedTask;
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
