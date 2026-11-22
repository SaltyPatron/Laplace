using Laplace.Engine.Core;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed record FoundryExportResult(
    string OutputPath,
    long Bytes,
    string Format,
    uint NVocab = 0,
    uint NEdges = 0,
    uint HiddenDim = 0,
    ulong RawWeightBytesRetained = 0,
    string Scheme = "laplace-blake3-v1");

internal interface IFoundryExportService
{
    Task<FoundryExportResult> ExportAsync(
        string? recipeJson,
        string? recipeIdPrefix,
        string? tokenizerDir,
        string format,
        string? filename,
        CancellationToken ct,
        EngineAuthorityScope? authority = null,
        string? modelName = null,
        uint maxVocab = 0,
        uint hiddenDim = 0,
        uint numLayers = 0,
        uint numHeads = 0,
        bool f16Weights = true);
}

/// <summary>
/// Exports clean <c>laplace-blake3-v1</c> GGUF/SafeTensors checkpoints natively in-process
/// via <see cref="ILaplaceEngineRuntime.MoldCheckpoint"/> (<c>lp_engine_mold_checkpoint</c>)
/// with <c>raw_weight_bytes_retained = 0</c>.
/// </summary>
internal sealed class NativeFoundryExportService : IFoundryExportService
{
    /// <summary>Formats this route writes; any other requested format is refused.</summary>
    internal static readonly HashSet<string> WritableFormats =
        new(StringComparer.OrdinalIgnoreCase) { "gguf", "safetensors" };

    private readonly EngineRuntimeService _runtime;
    private readonly IRecipeCompileService _recipes;

    public NativeFoundryExportService() : this(new EngineRuntimeService(), new RecipeCompileService()) { }

    public NativeFoundryExportService(ILaplaceEngineRuntime engine, IRecipeCompileService? recipes = null)
        : this(new EngineRuntimeService(engine), recipes) { }

    public NativeFoundryExportService(EngineRuntimeService runtime, IRecipeCompileService? recipes = null)
    {
        _runtime = runtime;
        _recipes = recipes ?? new RecipeCompileService();
    }

    public async Task<FoundryExportResult> ExportAsync(
        string? recipeJson,
        string? recipeIdPrefix,
        string? tokenizerDir,
        string format,
        string? filename,
        CancellationToken ct,
        EngineAuthorityScope? authority = null,
        string? modelName = null,
        uint maxVocab = 0,
        uint hiddenDim = 0,
        uint numLayers = 0,
        uint numHeads = 0,
        bool f16Weights = true)
    {
        string requested = (format ?? "").Trim();
        if (requested.Length == 0) requested = "gguf";
        if (!WritableFormats.Contains(requested))
            throw new ArgumentException(
                $"foundry export cannot produce '{requested}'. Writable formats: "
                + string.Join(", ", WritableFormats.Order(StringComparer.Ordinal))
                + ".",
                nameof(format));
        var ext = requested.ToLowerInvariant();

        string effectiveModel = string.IsNullOrWhiteSpace(modelName) ? "laplace-molded" : modelName.Trim();
        uint effHidden = hiddenDim;
        uint effLayers = numLayers;

        if (!string.IsNullOrWhiteSpace(recipeJson))
        {
            var compiled = _recipes.Compile(recipeJson);
            if (string.IsNullOrWhiteSpace(modelName))
                effectiveModel = compiled.Name;
            if (effHidden == 0 && uint.TryParse(compiled.HiddenSize, out var parsedHidden))
                effHidden = parsedHidden;
            if (effLayers == 0 && compiled.NumLayers > 0)
                effLayers = (uint)compiled.NumLayers;
        }
        else if (string.IsNullOrWhiteSpace(recipeIdPrefix))
        {
            throw new ArgumentException("Field 'recipe' is required unless 'recipe_id_prefix' is set.");
        }

        var engine = _runtime.Runtime
            ?? throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(_runtime.Unavailable)
                    ? "Native Laplace engine runtime is unavailable."
                    : "Native Laplace engine runtime unavailable: " + _runtime.Unavailable);

        var outputName = string.IsNullOrWhiteSpace(filename) ? $"laplace-export.{ext}" : filename.Trim();
        if (Path.GetFileName(outputName) != outputName || outputName is "." or "..")
            throw new ArgumentException("filename must be a filename inside the export directory.", nameof(filename));

        var workDir = Path.Combine(Path.GetTempPath(), $"laplace-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var outputPath = Path.Combine(workDir, outputName);

        var moldReq = new EngineMoldRequest(
            OutPath: outputPath,
            ModelName: effectiveModel,
            Authority: authority,
            MaxVocab: maxVocab,
            HiddenDim: effHidden,
            NLayers: effLayers,
            NHeads: numHeads,
            F16Weights: f16Weights);

        ct.ThrowIfCancellationRequested();
        var res = await Task.Run(() => engine.MoldCheckpoint(moldReq), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        if (res.StatusCode != 0)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(res.Error) ? $"Native mold export failed (status {res.StatusCode})." : res.Error);

        if (res.RawWeightBytesRetained != 0)
            throw new InvalidOperationException(
                $"Synthesis invariant violated: raw_weight_bytes_retained={res.RawWeightBytesRetained} (must be 0).");

        if (!File.Exists(outputPath))
            throw new InvalidOperationException("Native mold export produced no artifact.");

        long bytes = new FileInfo(outputPath).Length;
        return new FoundryExportResult(
            OutputPath: outputPath,
            Bytes: bytes,
            Format: ext,
            NVocab: res.NVocab,
            NEdges: res.NEdges,
            HiddenDim: res.HiddenDim,
            RawWeightBytesRetained: res.RawWeightBytesRetained,
            Scheme: "laplace-blake3-v1");
    }
}
