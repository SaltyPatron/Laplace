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
    IAsyncEnumerable<SubstrateChange> ReadChangesAsync(Stream input, RecipeExecutionOptions options,
        string artifactLabel, CancellationToken ct = default);
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
        .Register("laplace/native-streaming-xml-recipe/v1", static binding => new NativeXmlExecutor(binding, false))
        .Register("native-xml", static binding => new NativeXmlExecutor(binding, false))
        .Register("laplace/native-streaming-delimited-recipe/v1", static binding => new NativeXmlExecutor(binding, true));

    private sealed class NativeXmlExecutor : IRecipeSyntaxExecutor
    {
        private readonly NativeSourceRecipe _runtime;
        internal NativeXmlExecutor(RecipeProviderBinding binding, bool delimited)
        {
            SemanticSourceRecipe recipe = binding.Recipe
                ?? throw new InvalidDataException("The native syntax provider requires a semantic recipe.");
            if ((recipe.DelimitedSyntax is not null) != delimited)
                throw new InvalidDataException("The selected syntax provider does not match the semantic recipe's syntax configuration.");
            string expected = delimited ? "laplace/native-streaming-delimited-recipe/v1"
                : "laplace/native-streaming-xml-recipe/v1";
            if (!string.Equals(recipe.Provider, expected, StringComparison.Ordinal))
                throw new InvalidDataException($"The selected syntax provider requires recipe provider '{expected}', received '{recipe.Provider}'.");
            if (binding.Configuration.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
                && (binding.Configuration.ValueKind != JsonValueKind.Object || binding.Configuration.EnumerateObject().Any()))
                throw new InvalidDataException("Native XML/delimited provider configuration belongs in the semantic recipe; unknown execution configuration was supplied.");
            _runtime = new NativeSourceRecipe(recipe, binding.RecordDepth);
        }

        public IAsyncEnumerable<SubstrateChange> ReadChangesAsync(Stream input, RecipeExecutionOptions options,
            string artifactLabel, CancellationToken ct = default)
            => _runtime.ReadChangesAsync(input, options.SourceId, options.Trust, artifactLabel,
                IngestSizing.ResolveApplyTransactionRows(), IngestSizing.ResolveWorkingSetFlushEnvelopeBytes(),
                IngestSizing.ResolveSequentialIoBufferBytes(), ct: ct);
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
            .Concat(["HAS_PROPERTY", "HAS_VERSION", "IS_TYPED_AS", "HAS_NAME_ALIAS",
                "IS_A", "CONTAINS", "REQUIRES", "HAS_SOURCE_URL", "HAS_LICENSE", "HAS_CITATION"])
            .Distinct(StringComparer.Ordinal).ToArray();

    public Hash128 SourceId => _options.SourceId;
    public string SourceName => _options.SourceName;
    public int LayerOrder => _options.LayerOrder;
    public bool PerFileCompletion => true;
    public Hash128 TrustClassId => SubstrateCanonicalIds.TrustClass(_options.TrustClass);
    public IReadOnlyList<string> DeclaredRelations { get; }
    public IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        string[] typeNames = TypesFor(_recipe);
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
        SourceArtifactIdentity identity = SourceArtifactProvenance.Resolve(artifact);
        string label = artifact.FileLabel;
        Hash128 recipeIdentity = _recipe?.RecipeId ?? _generationRoot
            ?? throw new InvalidOperationException("Recipe execution has no generation identity.");
        string recipeName = $"cookbook/{Hex(recipeIdentity)}/depth/{_options.RecordDepth}"
            + $"/trust/{BitConverter.DoubleToInt64Bits(_options.Trust):x16}";
        Hash128[] requires = new[] { identity.ArtifactId, recipeIdentity }
            .Concat(_generationRoot is { } generation ? [generation] : Array.Empty<Hash128>())
            .Concat(_dependencies).Distinct().ToArray();
        Hash128 generationId = SourceArtifactProvenance.RecipeId(SourceName, _release, recipeName, requires);
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
        if (_recipe is not null && _generationRoot is null)
        {
            using var manifest = new SubstrateChangeBuilder(SourceId, $"cookbook/manifest/{Hex(_recipe.RecipeId)}");
            manifest.AddEntity(_recipe.RecipeId, EntityTier.Document, EntityTypeRegistry.SourceReference, SourceId);
            if (ContentEmitter.Emit(manifest, _recipe.CanonicalForm, SourceId) is { } contentId)
                manifest.AddAttestation(NativeAttestation.Categorical(
                    _recipe.RecipeId, "HAS_PROPERTY", contentId, SourceId, _options.Trust));
            manifest.DeclareSourcePrior(_options.Trust);
            yield return IngestBatchPipeline.BindFileLabel(manifest.Build() with { CountsAsUnit = false }, label);
        }
        yield return IngestBatchPipeline.BindFileLabel(
            SourceArtifactProvenance.BuildRecipeChange(SourceName, _release,
                recipeName, SourceId, TrustClassId, requires), label);
        long records = 0, entities = 0, physicalities = 0, attestations = 0;
        byte[] expectedDigest = Convert.FromHexString(artifact.Sha256);
        Stream parseInput = input;
        ZipArchive? archive = null;
        Stream? entry = null;
        IncrementalHash? passHash = null;
        if (_inputPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            byte[] zipDigest = await SHA256.HashDataAsync(input, ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(zipDigest, expectedDigest))
                throw new IOException("Recipe input changed while being parsed; file completion was not recorded.");
            input.Position = 0;
            archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            ZipArchiveEntry xmlEntry = archive.Entries.Count == 1
                ? archive.Entries[0]
                : archive.Entries.First(static item => item.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
            entry = xmlEntry.Open();
            parseInput = new BlockingReadStream(entry);
        }
        else
        {
            passHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            parseInput = new HashingReadStream(input, passHash);
        }
        try
        {
            await foreach (SubstrateChange change in _runtime.ReadChangesAsync(
                               parseInput, _options, label, ct).ConfigureAwait(false))
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
            entry?.Dispose();
            archive?.Dispose();
        }
        if (passHash is not null)
        {
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
    private readonly RecipeSyntaxProviderRegistry _providers;
    private ResolvedSourceGeneration? _resolved;
    private IReadOnlyCollection<string> _canonicalNames = [];
    private IReadOnlyList<string> _relations = [];
    private readonly Dictionary<Hash128, IRecipeSyntaxExecutor> _executors = [];
    private readonly List<string> _executionErrors = [];

    public Decomposer(TRecipe recipe, string root, RecipeSyntaxProviderRegistry? providers = null)
    {
        _recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        _root = Path.GetFullPath(root);
        _providers = providers ?? RecipeSyntaxProviderRegistry.CreateDefault();
    }

    public override Hash128 SourceId => _recipe.SourceId;
    public override string SourceName => _recipe.SourceName;
    public override int LayerOrder => _recipe.LayerOrder;
    public override Hash128 TrustClassId => SubstrateCanonicalIds.TrustClass(_recipe.TrustClass);
    public bool PerFileCompletion => true;
    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames;
    public override IReadOnlyList<string> DeclaredRelations => _relations;

    public async Task<ResolvedSourceGeneration> ResolveAsync(CancellationToken ct = default)
        => _resolved ??= await SourceGenerationResolver.ResolveAsync(_recipe, _root, ct).ConfigureAwait(false);

    public async Task<IngestArtifactGraph?> DescribeArtifactsAsync(string ecosystemPath,
        DecomposerOptions options, CancellationToken ct = default)
        => (await ResolveAsync(ct).ConfigureAwait(false)).Graph;

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
        BootstrapIntentBuilder bootstrap = await SourceVocabularyBootstrap.RegisterAsync(
            context, SourceId, SourceName, TrustClassId, types, _relations, ct: ct).ConfigureAwait(false);
        _canonicalNames = bootstrap.CanonicalNames;

        var placed = new List<(Hash128 Id, double X, double Y, double Z, double M)>();
        foreach (IngestArtifact artifact in resolved.Graph.Artifacts)
        {
            SubstrateChange provenance = SourceArtifactProvenance.BuildChange(artifact, SourceId, TrustClassId);
            await context.Writer.ApplyAsync(provenance with { CountsAsUnit = false }, ct).ConfigureAwait(false);
            Hash128 artifactId = SourceArtifactProvenance.Resolve(artifact).ArtifactId;
            foreach (PhysicalityRow row in provenance.Physicalities)
            {
                if (row.EntityId != artifactId) continue;
                placed.Add((artifactId, row.CoordX, row.CoordY, row.CoordZ, row.CoordM));
                break;
            }
        }

        using var declaration = new SubstrateChangeBuilder(SourceId, $"cookbook/generation/{Hex(resolved.GenerationId)}");
        declaration.DeclareSourcePrior(_recipe.Trust);
        PlaceShell(declaration, resolved.GenerationId, placed);
        if (ContentEmitter.Emit(declaration, _recipe.CanonicalForm, SourceId) is { } configRoot)
            declaration.AddAttestation(NativeAttestation.Categorical(
                resolved.GenerationId, "HAS_PROPERTY", configRoot, SourceId, _recipe.Trust));
        foreach (IngestArtifact artifact in resolved.Graph.Artifacts)
        {
            Hash128 artifactId = SourceArtifactProvenance.Resolve(artifact).ArtifactId;
            declaration.AddAttestation(NativeAttestation.Categorical(
                resolved.GenerationId, "REQUIRES", artifactId, SourceId, _recipe.Trust));
        }
        foreach (SemanticSourceRecipe recipe in recipes)
        {
            PlaceShell(declaration, recipe.RecipeId, placed);
            declaration.AddAttestation(NativeAttestation.Categorical(
                resolved.GenerationId, "REQUIRES", recipe.RecipeId, SourceId, _recipe.Trust));
            if (ContentEmitter.Emit(declaration, recipe.CanonicalForm, SourceId) is { } recipeRoot)
                declaration.AddAttestation(NativeAttestation.Categorical(
                    recipe.RecipeId, "HAS_PROPERTY", recipeRoot, SourceId, _recipe.Trust));
        }
        await context.Writer.ApplyAsync(declaration.Build() with { CountsAsUnit = false }, ct).ConfigureAwait(false);
    }

    private void PlaceShell(
        SubstrateChangeBuilder declaration,
        Hash128 id,
        List<(Hash128 Id, double X, double Y, double Z, double M)> placed)
    {
        if (placed.Count == 0)
        {
            // No artifact coordinate is available. The shell is still a source-owned
            // entity, so it takes the governed-name projection rather than remaining unrealized.
            CanonicalNamedIdentity.Declare(
                declaration, id, EntityTier.Document, EntityTypeRegistry.SourceReference,
                $"cookbook/shell/{Hex(id)}", SourceId);
            return;
        }
        double x = 0, y = 0, z = 0, m = 0;
        var ids = new Hash128[placed.Count];
        for (int i = 0; i < placed.Count; i++)
        {
            x += placed[i].X;
            y += placed[i].Y;
            z += placed[i].Z;
            m += placed[i].M;
            ids[i] = placed[i].Id;
        }
        double n = placed.Count;
        x /= n;
        y /= n;
        z /= n;
        m /= n;
        Span<double> coord = stackalloc double[4] { x, y, z, m };
        declaration.AddEntity(id, EntityTier.Document, EntityTypeRegistry.SourceReference, SourceId);
        declaration.AddPhysicality(new PhysicalityRow(
            PhysicalityId.Compute(id, PhysicalityType.Content),
            id,
            SourceId,
            PhysicalityType.Content,
            x, y, z, m,
            Hilbert128.Encode(coord),
            Trajectory.Build(ids),
            ids.Length,
            null,
            null,
            0));
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
