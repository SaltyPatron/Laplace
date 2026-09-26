using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Structured;

public sealed record RecipeExecutionOptions(Hash128 SourceId, string SourceName, double Trust,
    int RecordDepth = 2, string TrustClass = "StructuredCorpus", int LayerOrder = 0);

public sealed record RecipeProviderBinding(SemanticSourceRecipe? Recipe, int RecordDepth,
    JsonElement Configuration = default);

/// <summary>A provider owns syntax recovery; the shared decomposer owns file execution and persistence.</summary>
public interface IRecipeSyntaxExecutor
{
    /// <param name="openPrescan">Opens an independent read of the same artifact bytes, for
    /// recipes that collect the source's identity tables before lowering.</param>
    IAsyncEnumerable<SubstrateChange> ReadChangesAsync(Stream input, RecipeExecutionOptions options,
        string artifactLabel, Func<Stream>? openPrescan = null, CancellationToken ct = default);

    /// <summary>The entries of a zip artifact to parse, each as its own file (UCA's
    /// CollationTest.zip); empty reads the archive's single entry.</summary>
    IReadOnlyList<string> ZipEntries => [];
}

public sealed class RecipeSyntaxProviderRegistry
{
    private readonly Dictionary<string, Func<RecipeProviderBinding, IRecipeSyntaxExecutor>> _factories =
        new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Providers => _factories.Keys;

    public RecipeSyntaxProviderRegistry Register(string provider,
        Func<RecipeProviderBinding, IRecipeSyntaxExecutor> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(factory);
        if (!_factories.TryAdd(provider, factory))
            throw new InvalidOperationException($"Recipe provider is already registered: {provider}");
        return this;
    }

    public bool Supports(string provider) => _factories.ContainsKey(provider);

    public IRecipeSyntaxExecutor Create(string provider, RecipeProviderBinding binding)
        => _factories.TryGetValue(provider, out var factory) ? factory(binding)
            : throw new NotSupportedException($"No executable syntax provider is registered for '{provider}'.");

    public static RecipeSyntaxProviderRegistry CreateDefault() => new RecipeSyntaxProviderRegistry()
        .Register("laplace/native-streaming-xml-recipe/v1", static binding => new NativeXmlExecutor(binding, NativeSyntax.Xml))
        .Register("native-xml", static binding => new NativeXmlExecutor(binding, NativeSyntax.Xml))
        .Register("laplace/native-streaming-delimited-recipe/v1", static binding => new NativeXmlExecutor(binding, NativeSyntax.Delimited))
        .Register("laplace/native-streaming-turtle-recipe/v1", static binding => new NativeXmlExecutor(binding, NativeSyntax.Turtle));

    private enum NativeSyntax { Xml, Delimited, Turtle }

    private sealed class NativeXmlExecutor : IRecipeSyntaxExecutor
    {
        private readonly NativeSourceRecipe? _runtime;
        private readonly SemanticSourceRecipe _recipe;
        private readonly int _recordDepth;
        private readonly IReadOnlyList<(string Name, System.Text.RegularExpressions.Regex Pattern)> _pathConstants = [];
        public IReadOnlyList<string> ZipEntries { get; } = [];
        internal NativeXmlExecutor(RecipeProviderBinding binding, NativeSyntax syntax)
        {
            SemanticSourceRecipe recipe = binding.Recipe
                ?? throw new InvalidDataException("The native syntax provider requires a semantic recipe.");
            NativeSyntax declared = recipe.DelimitedSyntax is not null ? NativeSyntax.Delimited
                : recipe.TurtleSyntax is not null ? NativeSyntax.Turtle : NativeSyntax.Xml;
            if (declared != syntax)
                throw new InvalidDataException("The selected syntax provider does not match the semantic recipe's syntax configuration.");
            bool delimited = syntax == NativeSyntax.Delimited;
            string expected = syntax switch
            {
                NativeSyntax.Delimited => "laplace/native-streaming-delimited-recipe/v1",
                NativeSyntax.Turtle => "laplace/native-streaming-turtle-recipe/v1",
                _ => "laplace/native-streaming-xml-recipe/v1",
            };
            if (!string.Equals(recipe.Provider, expected, StringComparison.Ordinal))
                throw new InvalidDataException($"The selected syntax provider requires recipe provider '{expected}', received '{recipe.Provider}'.");
            _recipe = recipe;
            _recordDepth = binding.RecordDepth;
            if (binding.Configuration.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            {
                // Execution configuration: per-artifact record constants recovered from the
                // artifact path (a treebank's language code in its file name):
                // {"constants": {"name": {"pathPattern": "..."}}}; and the entries of a zip
                // artifact, each parsed as its own file: {"entries": ["a.txt", ...]}.
                var constants = new List<(string, System.Text.RegularExpressions.Regex)>();
                foreach (JsonProperty property in binding.Configuration.EnumerateObject())
                {
                    if (property.Name == "entries")
                    {
                        ZipEntries = property.Value.EnumerateArray()
                            .Select(static entry => entry.GetString()
                                ?? throw new InvalidDataException("A zip entry name is empty."))
                            .ToArray();
                        continue;
                    }
                    if (property.Name != "constants" || !delimited)
                        throw new InvalidDataException(
                            $"Native provider configuration '{property.Name}' is not an execution setting.");
                    foreach (JsonProperty constant in property.Value.EnumerateObject())
                    {
                        string pattern = constant.Value.GetProperty("pathPattern").GetString()
                            ?? throw new InvalidDataException($"Constant '{constant.Name}' has no path pattern.");
                        constants.Add((constant.Name, new System.Text.RegularExpressions.Regex(
                            pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant)));
                    }
                }
                _pathConstants = constants;
            }
            if (_pathConstants.Count == 0) _runtime = new NativeSourceRecipe(recipe, binding.RecordDepth);
        }

        public IAsyncEnumerable<SubstrateChange> ReadChangesAsync(Stream input, RecipeExecutionOptions options,
            string artifactLabel, Func<Stream>? openPrescan = null, CancellationToken ct = default)
            => (_runtime ?? ForArtifact(artifactLabel)).ReadChangesAsync(input, options.SourceId, options.Trust, artifactLabel,
                IngestSizing.ResolveApplyTransactionRows(), IngestSizing.ResolveWorkingSetFlushEnvelopeBytes(),
                IngestSizing.ResolveSequentialIoBufferBytes(), openPrescan: openPrescan, ct: ct);

        private NativeSourceRecipe ForArtifact(string artifactLabel)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach ((string name, System.Text.RegularExpressions.Regex pattern) in _pathConstants)
            {
                var match = pattern.Match(artifactLabel.Replace('\\', '/'));
                if (!match.Success || match.Groups.Count < 2 || match.Groups[1].Value.Length == 0)
                    throw new InvalidDataException(
                        $"Artifact '{artifactLabel}' does not carry record constant '{name}' ({pattern}).");
                values[name] = match.Groups[1].Value;
            }
            SourceDelimitedSyntax syntax = _recipe.DelimitedSyntax!;
            var recipe = _recipe.WithDelimitedSyntax(syntax with { Constants = values });
            return new NativeSourceRecipe(recipe, _recordDepth);
        }
    }
}

public sealed class SingleArtifactRecipeDecomposer : IDecomposer, IIngestArtifactGraphProvider,
    IIngestInventoryProvider, IIgnoresAmbientArtifactManifest
{
    private readonly SemanticSourceRecipe? _recipe;
    private readonly string _release;
    private readonly RecipeExecutionOptions _options;
    private readonly string _inputPath;
    private readonly IRecipeSyntaxExecutor _runtime;
    private readonly Hash128? _generationRoot;
    private readonly IReadOnlyList<Hash128> _dependencies;
    private IReadOnlyCollection<string> _canonicalNames = [];
    private FileStream? _input;
    private IngestArtifact? _artifact;

    public SingleArtifactRecipeDecomposer(SemanticSourceRecipe recipe, RecipeExecutionOptions options, string inputPath,
        RecipeSyntaxProviderRegistry? providers = null)
    {
        _recipe = recipe;
        _release = recipe.Release;
        _options = options;
        _inputPath = inputPath;
        _runtime = (providers ?? RecipeSyntaxProviderRegistry.CreateDefault()).Create(recipe.Provider,
            new RecipeProviderBinding(recipe, options.RecordDepth));
        _dependencies = [];
        DeclaredRelations = RelationsFor(recipe);
    }

    internal SingleArtifactRecipeDecomposer(SemanticSourceRecipe? recipe, RecipeExecutionOptions options,
        IngestArtifact artifact, IRecipeSyntaxExecutor runtime, Hash128 generationRoot,
        IReadOnlyList<Hash128> dependencies)
    {
        _recipe = recipe;
        _release = artifact.Release;
        _options = options;
        _inputPath = artifact.Path;
        _artifact = artifact;
        _runtime = runtime;
        _generationRoot = generationRoot;
        _dependencies = dependencies;
        DeclaredRelations = RelationsFor(recipe);
    }

    internal static IReadOnlyList<string> RelationsFor(SemanticSourceRecipe? recipe) => (recipe?.Fields ?? [])
            .Where(static field => field.Disposition.HasFlag(SourceFieldDisposition.Testimony))
            .Select(static field => field.RelationName ?? field.PropertyName)
            .Concat((recipe?.Fields ?? []).Where(static field => field.PreserveLexicalValue && field.LexicalRelationName is not null)
                .Select(static field => field.LexicalRelationName!))
            .Concat((recipe?.Fields ?? []).Where(static field => field.RelationParent is not null)
                .Select(static field => field.RelationParent!))
            .Concat((recipe?.ProviderRoutes ?? []).Where(static route => route.RangeRelationName is not null || route.RangeRelationProperty is not null)
                .Select(static route => route.RangeRelationName ?? route.RangeRelationProperty!))
            .Concat(["HAS_PROPERTY", "HAS_VERSION", "HAS_NAME",
                "IS_A", "CONTAINS", "REQUIRES", "HAS_SOURCE_URL", "HAS_LICENSE", "HAS_CITATION"])
            .Distinct(StringComparer.Ordinal).ToArray();

    public Hash128 SourceId => _options.SourceId;
    public string SourceName => _options.SourceName;
    public int LayerOrder => _options.LayerOrder;
    public bool PerFileCompletion => true;
    public Hash128 TrustClassId => TrustClassRegistry.Id(_options.TrustClass);
    public IReadOnlyList<string> DeclaredRelations { get; }
    public IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        string[] typeNames = TypesFor(_recipe);
        await SourceVocabularyBootstrap.RegisterAsync(
            context, SourceId, SourceName, TrustClassId, typeNames, DeclaredRelations, ct: ct)
            .ConfigureAwait(false);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        IngestArtifact artifact = _artifact ?? throw new InvalidOperationException("Recipe input has not been inventoried.");
        SourceArtifactIdentity identity = SourceArtifactProvenance.Resolve(artifact);
        string label = artifact.FileLabel;
        Hash128 recipeIdentity = _recipe?.RecipeId ?? _generationRoot
            ?? throw new InvalidOperationException("Recipe execution has no generation identity.");
        string recipeName = $"cookbook/{Hex(recipeIdentity)}/depth/{_options.RecordDepth}"
            + $"/trust/{BitConverter.DoubleToInt64Bits(_options.Trust):x16}";
        Hash128[] requires = new[] { identity.ArtifactId, recipeIdentity }
            .Concat(_generationRoot is { } generation ? [generation] : Array.Empty<Hash128>())
            .Concat(_dependencies).Distinct().ToArray();
        Hash128 recipeUse = SourceArtifactProvenance.RecipeId(SourceName, _release, recipeName, requires);
        // One file's execution is the composition of the artifact content and the recipe
        // applied to it. Files sharing a recipe (every treebank of one CoNLL-U recipe)
        // therefore complete, resume and bind independently.
        Hash128 generationId = Hash128.Merkle(EntityTier.Document, [identity.ArtifactId, recipeUse]);
        var observability = Laplace.Ingestion.IngestObservabilityScope.Current;
        if (!options.ReObservePresent
            && await context.Reader.HasFileCompletedAsync(generationId, SourceId, LayerOrder, ct).ConfigureAwait(false))
        {
            observability.OnFileComposed(SourceName, label, identity.ArtifactId,
                resumeFingerprint: generationId);
            yield return IngestBatchPipeline.BuildSkippedBoundary(SourceId, label);
            yield break;
        }
        _input ??= new FileStream(_inputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        FileStream input = _input;
        observability.OnFileStarted(SourceName, label, input.Length);
        yield return IngestBatchPipeline.BindFileLabel(
            SourceArtifactProvenance.BuildChange(artifact, SourceId, TrustClassId), label)
            with { CountsAsUnit = false };
        yield return IngestBatchPipeline.BindFileLabel(
            SourceArtifactProvenance.BuildRecipeChange(SourceName, _release,
                recipeName, SourceId, TrustClassId, requires), label);
        long records = 0, entities = 0, physicalities = 0, attestations = 0;
        byte[] expectedDigest = Convert.FromHexString(artifact.Sha256);
        ZipArchive? archive = null;
        IncrementalHash? passHash = null;
        // The parses of this artifact: the file itself, or the declared entries of a zip
        // archive (each its own file, labelled artifact!entry), or its single entry.
        var parses = new List<(string Label, Func<Stream> Open)>();
        var opened = new List<Stream>();
        if (_inputPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            byte[] zipDigest = await SHA256.HashDataAsync(input, ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(zipDigest, expectedDigest))
                throw new IOException("Recipe input changed while being parsed; file completion was not recorded.");
            input.Position = 0;
            archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            IReadOnlyList<string> named = _runtime.ZipEntries;
            IEnumerable<ZipArchiveEntry> entries = named.Count > 0
                ? named.Select(name => archive.GetEntry(name)
                    ?? throw new InvalidDataException($"Zip artifact '{label}' has no entry '{name}'."))
                : [archive.Entries.Count == 1
                    ? archive.Entries[0]
                    : archive.Entries.First(static item => item.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))];
            foreach (ZipArchiveEntry zipEntry in entries)
                parses.Add((named.Count > 0 ? $"{label}!{zipEntry.FullName}" : label, () =>
                {
                    Stream stream = zipEntry.Open();
                    opened.Add(stream);
                    return new BlockingReadStream(stream);
                }));
        }
        else
        {
            passHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            parses.Add((label, () =>
            {
                Stream stream = new HashingReadStream(input, passHash);
                // A gzip member is the same artifact bytes; the digest still covers the file.
                if (IsGzip(_inputPath)) { stream = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true); opened.Add(stream); }
                return stream;
            }));
        }
        try
        {
            foreach ((string parseLabel, Func<Stream> open) in parses)
            await foreach (SubstrateChange change in _runtime.ReadChangesAsync(
                               open(), _options, parseLabel, () => OpenIndependentRead(_inputPath), ct).ConfigureAwait(false))
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
        }
        finally
        {
            foreach (Stream stream in opened) stream.Dispose();
            archive?.Dispose();
        }
        if (passHash is not null)
        {
            // Bytes past the last decoded member still belong to the artifact digest.
            byte[] rest = new byte[64 * 1024];
            var tail = new HashingReadStream(input, passHash);
            while (await tail.ReadAsync(rest, ct).ConfigureAwait(false) > 0) { }
            byte[] parsedDigest = passHash.GetHashAndReset();
            passHash.Dispose();
            if (!CryptographicOperations.FixedTimeEquals(parsedDigest, expectedDigest))
                throw new IOException("Recipe input changed while being parsed; file completion was not recorded.");
        }
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
        SemanticSourceRecipe recipe = _recipe ?? throw new InvalidOperationException("Single-file inventory requires a semantic recipe.");
        _artifact = new IngestArtifact(SourceName, _release, info.Name, info.Name, _inputPath,
                IngestArtifactDisposition.Admitted, UpstreamUrl: "", FetchedAtUtc: "", Bytes: info.Length,
                Sha256: sha256, UpstreamChecksum: "", MediaType: "application/xml", License: "",
                Citation: recipe.Authority, Language: "", Split: "", AnnotationOrigin: recipe.Provider,
                Notes: $"Explicit cookbook input; recipe {Hex(recipe.RecipeId)}", ModifiedAt: info.LastWriteTimeUtc);
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
    private static string Hex(Hash128 id) => Convert.ToHexStringLower(id.ToBytes());

    private static bool IsGzip(string path) => path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);

    // An independent read of the artifact for recipes that collect identity tables first.
    private static Stream OpenIndependentRead(string path)
    {
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Identity-table prescan is not available for zip artifact '{path}'.");
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return IsGzip(path) ? new GZipStream(file, CompressionMode.Decompress, leaveOpen: false) : file;
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly Stream _inner;
        public BlockingReadStream(Stream inner) => _inner = inner;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => new(_inner.Read(buffer.Span));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HashingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly IncrementalHash _hash;
        public HashingReadStream(Stream inner, IncrementalHash hash)
        {
            _inner = inner;
            _hash = hash;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            if (read > 0) _hash.AppendData(buffer.AsSpan(offset, read));
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0) _hash.AppendData(buffer.Span[..read]);
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal static string[] TypesFor(SemanticSourceRecipe? recipe) => (recipe?.Fields ?? [])
        .Select(static field => field.ObjectEntityType)
        .Concat((recipe?.ProviderRoutes ?? []).Select(static route => route.Subject.EntityType))
        .Where(static type => !string.IsNullOrEmpty(type))
        .Concat((recipe?.Structures ?? []).Select(static structure => structure.SemanticType))
        .Concat(["Codepoint", "Recipe_Value", "Recipe_Subject"])
        .Distinct(StringComparer.Ordinal).ToArray();
}

/// <summary>
/// Executes a selected source generation. Artifact enumeration, dependency waves,
/// bounded workers, provenance, completion, and writer handoff are shared across providers.
/// </summary>
public sealed class Decomposer<TRecipe> : DecomposerMultiPhase, IDecomposer,
    IIngestArtifactGraphProvider, IIngestInventoryProvider, IIgnoresAmbientArtifactManifest
    where TRecipe : SourceGenerationRecipe
{
    private readonly TRecipe _recipe;
    private readonly string _root;
    private readonly string? _scope;
    private readonly RecipeSyntaxProviderRegistry _providers;
    private ResolvedSourceGeneration? _resolved;
    private IReadOnlyCollection<string> _canonicalNames = [];
    private IReadOnlyList<string> _relations = [];
    private readonly Dictionary<Hash128, IRecipeSyntaxExecutor> _executors = [];
    private readonly List<string> _executionErrors = [];

    public Decomposer(TRecipe recipe, string root, RecipeSyntaxProviderRegistry? providers = null,
        string? scope = null)
    {
        _recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        _root = Path.GetFullPath(root);
        _scope = string.IsNullOrWhiteSpace(scope) ? null : Path.GetFullPath(scope);
        _providers = providers ?? RecipeSyntaxProviderRegistry.CreateDefault();
    }

    public override Hash128 SourceId => _recipe.SourceId;
    public override string SourceName => _recipe.SourceName;
    public override int LayerOrder => _recipe.LayerOrder;
    public override Hash128 TrustClassId => TrustClassRegistry.Id(_recipe.TrustClass);
    public bool PerFileCompletion => true;

    /// <summary>
    /// A scoped run executes the admitted artifacts beneath its scope: it commits those
    /// files but does not establish the source's layer.
    /// </summary>
    public bool IsScoped => _scope is not null;
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;
    public override IReadOnlyList<string> DeclaredRelations => _relations;

    public async Task<ResolvedSourceGeneration> ResolveAsync(CancellationToken ct = default)
        => _resolved ??= await SourceGenerationResolver.ResolveAsync(_recipe, _root, _scope, ct).ConfigureAwait(false);

    public async Task<IngestArtifactGraph?> DescribeArtifactsAsync(string ecosystemPath,
        DecomposerOptions options, CancellationToken ct = default)
    {
        ResolvedSourceGeneration resolved = await ResolveAsync(ct).ConfigureAwait(false);
        if (_scope is null) return resolved.Graph;
        // A scoped run's inventory is the admitted artifacts beneath its scope (and their
        // dependencies). Admitted artifacts outside it belong to another run of the same
        // generation, so they are not part of this run's selected set.
        var executed = resolved.Bindings.Select(static binding => binding.Artifact.Path)
            .ToHashSet(StringComparer.Ordinal);
        return new IngestArtifactGraph(resolved.Graph.Artifacts
            .Where(artifact => !artifact.IsSelected || executed.Contains(artifact.Path)));
    }

    public async Task<IngestInventory?> DescribeInputAsync(IDecomposerContext context,
        DecomposerOptions options, CancellationToken ct = default)
    {
        ResolvedSourceGeneration resolved = await ResolveAsync(ct).ConfigureAwait(false);
        return new IngestInventory("records", 0, resolved.Bindings.Select(static binding =>
            new IngestFileSpec(binding.Artifact.FileLabel, binding.Artifact.Path, InputUnits: 0)).ToArray(),
            TracksFileCompletion: true);
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(null);

    public override async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        ResolvedSourceGeneration resolved = await ResolveAsync(ct).ConfigureAwait(false);
        _executionErrors.AddRange(resolved.ExecutionErrors);
        _executionErrors.AddRange(resolved.Graph.Artifacts
            .Where(static artifact => artifact.Disposition == IngestArtifactDisposition.Unsupported)
            .Select(static artifact => $"Unresolved artifact '{artifact.RelativePath}': {artifact.Notes}"));
        var compiled = new Dictionary<string, IRecipeSyntaxExecutor>(StringComparer.Ordinal);
        foreach (ResolvedSourceArtifact binding in resolved.Bindings)
        {
            string key = $"{binding.Provider}/{binding.Recipe?.RecipeId}/{binding.RecordDepth}/"
                + binding.Rule.Configuration.GetRawText();
            try
            {
                if (!compiled.TryGetValue(key, out var executor))
                {
                    executor = _providers.Create(binding.Provider,
                        new RecipeProviderBinding(binding.Recipe, binding.RecordDepth, binding.Rule.Configuration));
                    compiled.Add(key, executor);
                }
                _executors.Add(binding.ArtifactId, executor);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
            {
                _executionErrors.Add($"Artifact '{binding.Artifact.RelativePath}': {ex.Message}");
            }
        }
        SemanticSourceRecipe[] recipes = resolved.Bindings.Where(static b => b.Recipe is not null)
            .Select(static b => b.Recipe!).DistinctBy(static r => r.RecipeId).ToArray();
        _relations = recipes.SelectMany(SingleArtifactRecipeDecomposer.RelationsFor)
            .Concat(SingleArtifactRecipeDecomposer.RelationsFor(null)).Distinct(StringComparer.Ordinal).ToArray();
        string[] types = recipes.SelectMany(SingleArtifactRecipeDecomposer.TypesFor)
            .Concat(SingleArtifactRecipeDecomposer.TypesFor(null)).Distinct(StringComparer.Ordinal).ToArray();
        // A generation without an explicit source id is its own witness [authority, release].
        (string, string)? witness = SourceId == SourceWitness.Id(_recipe.Authority, _recipe.Release)
            ? (_recipe.Authority, _recipe.Release) : null;
        await SourceVocabularyBootstrap.RegisterAsync(
            context, SourceId, SourceName, TrustClassId, types, _relations, ct: ct, witness: witness)
            .ConfigureAwait(false);

        foreach (IngestArtifact artifact in resolved.Graph.Artifacts)
        {
            SubstrateChange provenance = SourceArtifactProvenance.BuildChange(artifact, SourceId, TrustClassId);
            await context.Writer.ApplyAsync(provenance with { CountsAsUnit = false }, ct).ConfigureAwait(false);
        }
    }

    protected override async IAsyncEnumerable<SubstrateChange> RunIngestAsync(IDecomposerContext context,
        DecomposerOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        ResolvedSourceGeneration resolved = await ResolveAsync(ct).ConfigureAwait(false);
        var unavailable = resolved.Bindings.Where(binding => !_executors.ContainsKey(binding.ArtifactId))
            .Select(static binding => binding.ArtifactId).ToHashSet();
        bool changed;
        do
        {
            changed = false;
            foreach (ResolvedSourceArtifact binding in resolved.Bindings)
                if (!unavailable.Contains(binding.ArtifactId) && binding.Dependencies.Any(unavailable.Contains))
                {
                    unavailable.Add(binding.ArtifactId);
                    _executionErrors.Add($"Artifact '{binding.Artifact.RelativePath}' requires an unavailable provider result.");
                    changed = true;
                }
        } while (changed);
        var pending = resolved.Bindings.Where(binding => !unavailable.Contains(binding.ArtifactId))
            .ToDictionary(static binding => binding.ArtifactId);
        int level = 0;
        while (pending.Count > 0)
        {
            ResolvedSourceArtifact[] ready = pending.Values
                .Where(binding => binding.Dependencies.All(dependency => !pending.ContainsKey(dependency)))
                .OrderByDescending(static binding => binding.Artifact.Bytes ?? 0)
                .ThenBy(static binding => binding.Artifact.Path, StringComparer.Ordinal).ToArray();
            if (ready.Length == 0)
                throw new InvalidDataException("Source generation contains a cyclic artifact dependency.");
            if (level++ > 0)
                await foreach (SubstrateChange barrier in ApplyBarrierAsync(
                                   $"cookbook/{Hex(resolved.GenerationId)}/level/{level}", ct).ConfigureAwait(false))
                    yield return barrier;
            int workers = options.MaxInputUnits > 0 ? 1
                : Math.Min(ready.Length, Math.Max(1, IngestTopology.Current.FileWorkers));
            await foreach (SubstrateChange change in ParallelIngestWork.RunAsync(
                               ready, workers, Execute, ct).ConfigureAwait(false))
                yield return change;
            foreach (ResolvedSourceArtifact binding in ready) pending.Remove(binding.ArtifactId);
        }
        if (_executionErrors.Count > 0)
        {
            await foreach (SubstrateChange barrier in ApplyBarrierAsync(
                               $"cookbook/{Hex(resolved.GenerationId)}/supported-artifacts-applied", ct).ConfigureAwait(false))
                yield return barrier;
            string[] fatal = _executionErrors
                .Where(static error => !error.StartsWith("Unresolved artifact ", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (fatal.Length > 0)
                throw new InvalidDataException(string.Join(Environment.NewLine, fatal));
        }

        async IAsyncEnumerable<SubstrateChange> Execute(ResolvedSourceArtifact binding,
            [EnumeratorCancellation] CancellationToken token)
        {
            string label = ClaimArtifact(context, binding.Artifact.Path, binding.Artifact.FileLabel);
            IngestArtifact artifact = binding.Artifact with { JournalLabel = label };
            await using var executor = new SingleArtifactRecipeDecomposer(binding.Recipe,
                new RecipeExecutionOptions(SourceId, SourceName, _recipe.Trust,
                    binding.RecordDepth, _recipe.TrustClass, LayerOrder), artifact,
                _executors[binding.ArtifactId], resolved.GenerationId, binding.Dependencies);
            await foreach (SubstrateChange change in executor.DecomposeAsync(context, options, token).ConfigureAwait(false))
                yield return change;
        }
    }

    private static string Hex(Hash128 id) => Convert.ToHexStringLower(id.ToBytes());
}
