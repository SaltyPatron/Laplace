using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using Laplace.Engine.Core;
using Laplace.Ops;
using Microsoft.Extensions.Logging;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed record EstateRecipeSummary(
    string Name,
    string FileName,
    string RelativePath,
    string Grammar,
    string Format,
    string Like,
    IReadOnlyList<string> Matches);

internal sealed record EstateSourceEntry(
    int Order,
    string Name,
    IReadOnlyList<string> Aliases,
    string Witness,
    string Called,
    string? WitnessId,
    string Lineage,
    string TrustClass,
    string Held,
    IReadOnlyList<string> Roots,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> After,
    IReadOnlyList<string> Reads,
    double? Room,
    bool StagedOnDisk,
    string StageStatus,
    string? MatchedPath,
    IReadOnlyList<EstateRecipeSummary> Recipes);

internal sealed record EstateReport(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("cli_available")] bool CliAvailable,
    [property: JsonPropertyName("cli_path")] string? CliPath,
    [property: JsonPropertyName("recipes_dir")] string? RecipesDir,
    [property: JsonPropertyName("grammars_dir")] string? GrammarsDir,
    [property: JsonPropertyName("data_dir")] string? DataDir,
    [property: JsonPropertyName("compiled_grammars")] IReadOnlyList<string> CompiledGrammars,
    [property: JsonPropertyName("native_grammars")] IReadOnlyList<string> NativeGrammars,
    [property: JsonPropertyName("sources")] IReadOnlyList<EstateSourceEntry> Sources);

internal sealed record StudioRecipeCatalogItem(
    string Name,
    string? Source,
    [property: JsonPropertyName("relative_path")] string RelativePath,
    string Grammar,
    string Format,
    string Like,
    IReadOnlyList<string> Matches,
    string Content);

internal sealed record RecipeStudioCatalog(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("cli_available")] bool CliAvailable,
    [property: JsonPropertyName("recipes_dir")] string? RecipesDir,
    [property: JsonPropertyName("grammars_dir")] string? GrammarsDir,
    [property: JsonPropertyName("compiled_grammars")] IReadOnlyList<string> CompiledGrammars,
    [property: JsonPropertyName("native_grammars")] IReadOnlyList<string> NativeGrammars,
    [property: JsonPropertyName("formats")] IReadOnlyList<string> Formats,
    [property: JsonPropertyName("trust_classes")] IReadOnlyList<string> TrustClasses,
    [property: JsonPropertyName("recipes")] IReadOnlyList<StudioRecipeCatalogItem> Recipes);

internal sealed record RecipeLintSummary(
    string? Name,
    IReadOnlyList<string> Matches,
    string? Grammar,
    string? Format,
    string? Like,
    [property: JsonPropertyName("trust_class")] string? TrustClass,
    [property: JsonPropertyName("has_layout")] bool HasLayout,
    [property: JsonPropertyName("has_content")] bool HasContent,
    [property: JsonPropertyName("has_thing")] bool HasThing,
    [property: JsonPropertyName("has_attest")] bool HasAttest,
    [property: JsonPropertyName("directive_count")] int DirectiveCount);

internal sealed record RecipeLintResult(
    bool Valid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    RecipeLintSummary Summary);

internal sealed record StudioCliResult(
    bool Ok,
    [property: JsonPropertyName("exit_code")] int? ExitCode,
    [property: JsonPropertyName("timed_out")] bool TimedOut,
    [property: JsonPropertyName("elapsed_ms")] double ElapsedMs,
    string Cli,
    IReadOnlyList<string> Arguments,
    string Stdout,
    string Stderr,
    [property: JsonPropertyName("output_incomplete")] bool OutputIncomplete);

internal sealed record InspectRecordPreview(
    [property: JsonPropertyName("record_index")] int RecordIndex,
    IReadOnlyList<string> Held,
    IReadOnlyList<string> Claims);

internal sealed record RecipeInspectResult(
    [property: JsonPropertyName("object")] string Object,
    string Filename,
    RecipeLintResult Lint,
    bool Executed,
    bool Ok,
    [property: JsonPropertyName("exit_code")] int? ExitCode,
    [property: JsonPropertyName("timed_out")] bool TimedOut,
    [property: JsonPropertyName("elapsed_ms")] double ElapsedMs,
    [property: JsonPropertyName("records_count")] int RecordsCount,
    [property: JsonPropertyName("claims_count")] int ClaimsCount,
    [property: JsonPropertyName("unresolved_obligations_count")] long? UnresolvedObligationsCount,
    [property: JsonPropertyName("compositions_count")] long? CompositionsCount,
    IReadOnlyList<InspectRecordPreview> Records,
    IReadOnlyList<string> Claims,
    IReadOnlyList<string> Obligations,
    string Stdout,
    string Stderr);

internal sealed record RecipeDraftResult(
    [property: JsonPropertyName("object")] string Object,
    string Filename,
    [property: JsonPropertyName("recipe_dsl")] string RecipeDsl,
    string? Grammar,
    string? Format,
    RecipeLintResult Lint);

internal sealed record RecipeAdmitResult(
    [property: JsonPropertyName("object")] string Object,
    string Filename,
    string Source,
    string Witness,
    [property: JsonPropertyName("trust_class")] string TrustClass,
    RecipeLintResult Lint,
    bool Executed,
    bool Ok,
    [property: JsonPropertyName("exit_code")] int? ExitCode,
    [property: JsonPropertyName("timed_out")] bool TimedOut,
    [property: JsonPropertyName("elapsed_ms")] double ElapsedMs,
    string Stdout,
    string Stderr);

/// <summary>
/// Discovers the corpus estate (<c>recipes/order</c>, <c>source</c>, <c>*.recipe</c>, grammars,
/// and <c>LAPLACE_DATA</c> staging), runs safe native CLI <c>--plan</c> / <c>forget</c> / <c>sweep</c>,
/// enforces Recording Law lint checks, drafts <c>.recipe</c> configurations from sample files, and
/// executes isolated offline <c>laplace ingest --claims --no-load</c> inspections.
/// </summary>
internal sealed class RecipeStudioService(ILogger? logger = null)
{
    private static readonly TimeSpan PlanTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InspectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaintenanceTimeout = TimeSpan.FromSeconds(180);

    private static readonly string[] NativeGrammarNames =
        ["text", "layout", "pgn", "fen", "git", "image", "audio", "video"];

    private static readonly string[] DefaultTrustClasses =
    [
        "SubstrateMandate",
        "StandardsDerived",
        "AcademicCurated",
        "AcademicCuratedWithUserInput",
        "StructuredCorpus",
        "UserCuratedResource",
        "AIModelProbe",
        "AppDerived",
        "UserPromptContent",
        "ResponseContent",
        "AdversarialUntrusted",
        "DerivedCalculation",
        "AgentTranscript",
        "ToolResultContent",
        "ToolchainVerification",
        "InternalMonologue",
    ];

    private static readonly HashSet<string> KnownDirectives = new(StringComparer.Ordinal)
    {
        "name", "match", "interpreter", "grammar", "format", "product-format", "input", "like",
        "class", "witness", "lineage", "trust", "deviation",
        "tier", "names", "header", "skip", "note", "comment", "remark", "quoted", "padded", "is", "continued", "escaped",
        "empty", "trim", "levels", "split", "resolve", "records", "columns", "node", "part",
        "scope", "scope-space", "scope-misc", "scope-prefix", "scope-first", "framing", "layout-prefix",
        "remark-content", "counted-shape", "record-names", "quotes", "separator",
        "content", "key", "refer", "index", "type", "value", "relation", "dependence", "default", "unless_equal",
        "admit", "metadata", "omit", "perfcache", "own", "codepoints", "range",
        "thing", "attest", "relate", "pair", "holds", "itself", "voices", "voice", "together",
        "games", "score", "strict", "where", "when", "always", "stem", "itself-mark", "about", "line",
        "surface", "layers", "tree", "types", "keyed", "alias", "maps",
    };

    private static readonly HashSet<string> LayoutDirectives = new(StringComparer.Ordinal)
    {
        "tier", "format", "product-format", "records", "columns", "node", "part", "scope",
    };

    private static readonly HashSet<string> EphemeralPointerTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "sent_id", "row_id", "byte_offset", "line_num", "line_number", "line_no",
        "char_offset", "token_offset", "file_offset", "row_index", "record_index",
        "newdoc_id", "newpar_id", "original_sent_id",
    };

    private static readonly Regex SafeSourceRegex = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly Regex BareIdentifierRegex = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, string> ExtensionGrammars =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".c"] = "c", [".h"] = "c",
            [".cc"] = "cpp", [".cpp"] = "cpp", [".cxx"] = "cpp", [".hpp"] = "cpp", [".hh"] = "cpp",
            [".cs"] = "c_sharp", [".csx"] = "c_sharp",
            [".py"] = "python", [".pyi"] = "python",
            [".js"] = "javascript", [".mjs"] = "javascript", [".cjs"] = "javascript",
            [".ts"] = "typescript", [".mts"] = "typescript",
            [".tsx"] = "tsx", [".jsx"] = "tsx",
            [".rs"] = "rust", [".go"] = "go", [".java"] = "java", [".kt"] = "kotlin",
            [".rb"] = "ruby", [".php"] = "php", [".sh"] = "bash", [".bash"] = "bash",
            [".ps1"] = "powershell", [".sql"] = "sql", [".css"] = "css", [".html"] = "html",
            [".htm"] = "html", [".xml"] = "xml", [".ttl"] = "turtle", [".nt"] = "turtle",
            [".n3"] = "turtle", [".json"] = "json", [".jsonl"] = "json", [".ndjson"] = "json",
            [".csv"] = "csv", [".tsv"] = "tsv", [".tab"] = "tsv", [".psv"] = "psv",
            [".pgn"] = "pgn", [".zig"] = "zig", [".jl"] = "julia", [".f90"] = "fortran",
            [".cu"] = "cuda", [".glsl"] = "glsl", [".hlsl"] = "hlsl", [".wgsl"] = "wgsl",
            [".ll"] = "llvm", [".mlir"] = "mlir", [".asm"] = "nasm", [".dockerfile"] = "dockerfile",
            [".cmake"] = "cmake", [".mk"] = "make",
        };

    public EstateReport GetEstate()
    {
        string? recipesDir = ResolveRecipesDir();
        string? grammarsDir = ResolveGrammarsDir();
        string? dataDir = ResolveDataDir();
        string? cliPath = ResolveNativeCliPath();
        var compiledGrammars = EnumerateCompiledGrammars(grammarsDir);
        var entries = new List<EstateSourceEntry>();

        if (recipesDir is not null)
        {
            var ordered = ReadOrderFile(recipesDir);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var sourceDirs = new List<string>(ordered);
            foreach (var dir in Directory.EnumerateDirectories(recipesDir).OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                var dirName = Path.GetFileName(dir);
                if (File.Exists(Path.Combine(dir, "source")) && !ordered.Contains(dirName, StringComparer.Ordinal))
                    sourceDirs.Add(dirName);
            }

            int ordinal = 0;
            foreach (var dirName in sourceDirs)
            {
                if (!seen.Add(dirName)) continue;
                var sourceDir = Path.Combine(recipesDir, dirName);
                var sourceFile = Path.Combine(sourceDir, "source");
                if (!File.Exists(sourceFile)) continue;

                ordinal++;
                var parsed = ParseSourceFile(sourceFile, dirName);
                var recipes = EnumerateRecipesIn(sourceDir, recipesDir);
                string witnessLabel = ResolveWitnessLabel(parsed);
                string? witnessId = TryComputeWitnessId(witnessLabel);
                var (staged, status, matchedPath) = EvaluateStaging(parsed, dataDir);

                entries.Add(new EstateSourceEntry(
                    Order: ordinal,
                    Name: parsed.Name,
                    Aliases: parsed.Aliases,
                    Witness: parsed.Witness,
                    Called: parsed.Called,
                    WitnessId: witnessId,
                    Lineage: parsed.Lineage,
                    TrustClass: parsed.TrustClass,
                    Held: parsed.Held,
                    Roots: parsed.Roots,
                    Files: parsed.Files,
                    After: parsed.After,
                    Reads: parsed.Reads,
                    Room: parsed.Room,
                    StagedOnDisk: staged,
                    StageStatus: status,
                    MatchedPath: matchedPath,
                    Recipes: recipes));
            }
        }

        return new EstateReport(
            Object: "ingest.estate",
            Host: Environment.MachineName,
            CliAvailable: cliPath is not null,
            CliPath: cliPath,
            RecipesDir: recipesDir,
            GrammarsDir: grammarsDir,
            DataDir: dataDir,
            CompiledGrammars: compiledGrammars,
            NativeGrammars: NativeGrammarNames,
            Sources: entries);
    }

    public RecipeStudioCatalog GetCatalog()
    {
        string? recipesDir = ResolveRecipesDir();
        string? grammarsDir = ResolveGrammarsDir();
        string? cliPath = ResolveNativeCliPath();
        var compiledGrammars = EnumerateCompiledGrammars(grammarsDir);
        var formats = new List<string>();
        var items = new List<StudioRecipeCatalogItem>();

        if (recipesDir is not null)
        {
            var formatsDir = Path.Combine(recipesDir, "formats");
            if (Directory.Exists(formatsDir))
            {
                formats.AddRange(Directory.EnumerateFiles(formatsDir, "*.format")
                    .Select(p => Path.GetFileNameWithoutExtension(p))
                    .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase));
            }

            foreach (var file in Directory.EnumerateFiles(recipesDir, "*.recipe")
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(ReadCatalogItem(file, recipesDir, sourceName: null));
            }

            foreach (var dir in Directory.EnumerateDirectories(recipesDir)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var srcName = Path.GetFileName(dir);
                if (!File.Exists(Path.Combine(dir, "source"))) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.recipe")
                             .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    items.Add(ReadCatalogItem(file, recipesDir, srcName));
                }
            }
        }

        return new RecipeStudioCatalog(
            Object: "recipe_studio.catalog",
            CliAvailable: cliPath is not null,
            RecipesDir: recipesDir,
            GrammarsDir: grammarsDir,
            CompiledGrammars: compiledGrammars,
            NativeGrammars: NativeGrammarNames,
            Formats: formats,
            TrustClasses: LoadTrustClasses(),
            Recipes: items);
    }

    public Task<StudioCliResult> PlanSourceAsync(string source, CancellationToken ct = default)
    {
        var clean = ValidateSourceSelector(source);
        return RunNativeCliAsync(["ingest", "--plan", clean], offlineConnInfo: true, PlanTimeout, ct);
    }

    public Task<StudioCliResult> ForgetSourceAsync(string source, CancellationToken ct = default)
    {
        var clean = ValidateSourceSelector(source);
        return RunNativeCliAsync(["forget", clean], offlineConnInfo: false, MaintenanceTimeout, ct);
    }

    public Task<StudioCliResult> SweepAsync(CancellationToken ct = default)
    {
        return RunNativeCliAsync(["sweep"], offlineConnInfo: false, MaintenanceTimeout, ct);
    }

    public RecipeLintResult LintRecipe(string dsl, string? filename = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(dsl))
        {
            errors.Add("Recipe DSL is empty.");
            return new RecipeLintResult(false, errors, warnings,
                new RecipeLintSummary(null, [], null, null, null, null, false, false, false, false, 0));
        }

        var trustClasses = LoadTrustClasses();
        string? name = null, grammar = null, format = null, like = null, trustClass = null;
        var matches = new List<string>();
        var declaredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var omittedNames = new HashSet<string>(StringComparer.Ordinal);
        var keyedNames = new HashSet<string>(StringComparer.Ordinal);
        bool hasLayout = false, hasContent = false, hasThing = false, hasAttest = false;
        bool hasCuratedOnlyRule = false;
        int directives = 0;

        var lines = dsl.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            int lineNo = lineIdx + 1;
            var stripped = StripRecipeComment(lines[lineIdx]).Trim();
            if (stripped.Length == 0) continue;

            if (!TryTokenizeLine(stripped, out var tokens))
            {
                errors.Add($"Line {lineNo}: unterminated double quote.");
                continue;
            }
            if (tokens.Count == 0) continue;

            directives++;
            string directive = tokens[0];
            if (!KnownDirectives.Contains(directive))
                errors.Add($"Line {lineNo}: unknown directive '{directive}'.");

            if (LayoutDirectives.Contains(directive))
                hasLayout = true;

            switch (directive)
            {
                case "name":
                    if (tokens.Count < 2 || string.IsNullOrWhiteSpace(tokens[1]))
                        errors.Add($"Line {lineNo}: 'name' requires an identifier.");
                    else
                        name = tokens[1];
                    break;
                case "match":
                    if (tokens.Count < 2)
                        errors.Add($"Line {lineNo}: 'match' requires at least one glob pattern.");
                    else
                        matches.AddRange(tokens.Skip(1));
                    break;
                case "grammar":
                    if (tokens.Count < 2)
                        errors.Add($"Line {lineNo}: 'grammar' requires a grammar name.");
                    else
                        grammar = tokens[1];
                    break;
                case "format":
                    if (tokens.Count < 2)
                        errors.Add($"Line {lineNo}: 'format' requires a format name.");
                    else
                        format = tokens[1];
                    break;
                case "like":
                    if (tokens.Count < 2)
                        errors.Add($"Line {lineNo}: 'like' requires a base recipe name.");
                    else
                        like = tokens[1];
                    break;
                case "class":
                    if (tokens.Count < 2)
                    {
                        errors.Add($"Line {lineNo}: 'class' requires a trust class from manifest/trust_classes.toml.");
                    }
                    else
                    {
                        trustClass = tokens[1];
                        if (!trustClasses.Contains(trustClass, StringComparer.Ordinal))
                            errors.Add($"Line {lineNo}: unknown trust class '{trustClass}' (must be declared in manifest/trust_classes.toml).");
                    }
                    break;
                case "trust":
                case "deviation":
                    errors.Add($"Line {lineNo}: '{directive}' numeric trust override is forbidden; use 'class NAME' from manifest/trust_classes.toml.");
                    break;
                case "names":
                    foreach (var t in tokens.Skip(1))
                        declaredNames.Add(t);
                    break;
                case "content":
                    hasContent = true;
                    FlagEphemeralTokens(lineNo, directive, tokens.Skip(1), warnings);
                    break;
                case "metadata":
                    FlagEphemeralTokens(lineNo, directive, tokens.Skip(1), warnings);
                    break;
                case "omit":
                    foreach (var t in tokens.Skip(1))
                        omittedNames.Add(t);
                    break;
                case "key":
                    hasCuratedOnlyRule = true;
                    if (tokens.Count >= 3)
                    {
                        keyedNames.Add(tokens[2]);
                        if (stripped.Contains($"\"{tokens[2]}\"", StringComparison.Ordinal) && tokens[2].Contains(' ', StringComparison.Ordinal) == false)
                            warnings.Add($"Line {lineNo}: 'key' takes a field name ({tokens[2]}), not a quoted KVP wrapper literal.");
                        if (tokens[1].Equals("\"", StringComparison.Ordinal) || stripped.Contains("key \"", StringComparison.Ordinal))
                            warnings.Add($"Line {lineNo}: suspicious KVP wrapper literal in 'key' directive (Recording Law: resolve internal pointers with 'key TIER FIELD within SCOPE', never KVP wrapper keys).");
                    }
                    else
                    {
                        errors.Add($"Line {lineNo}: 'key' requires 'key TIER FIELD [within SCOPE]'.");
                    }
                    break;
                case "thing":
                    hasThing = true;
                    hasCuratedOnlyRule = true;
                    if (tokens.Count < 3)
                    {
                        errors.Add($"Line {lineNo}: 'thing' requires 'thing TIER NAME...'.");
                    }
                    else
                    {
                        for (int i = 2; i < tokens.Count; i++)
                        {
                            if (tokens[i] == "literal" && i + 1 < tokens.Count)
                            {
                                var lit = tokens[++i];
                                if (LooksLikeKvpWrapper(lit, declaredNames))
                                    warnings.Add($"Line {lineNo}: 'thing ... literal \"{lit}\"' looks like a KVP column-header/tag wrapper literal; prefer content, thing, or documented relation wording.");
                            }
                            else if (tokens[i] is "within" or "within-file" or "resolved" or "required" or "preserve" or "span" or "inclusive" or "of")
                            {
                                continue;
                            }
                            else if (tokens[i] is "matching" or "product" or "space")
                            {
                                i++;
                            }
                            else
                            {
                                FlagEphemeralToken(lineNo, directive, tokens[i], warnings);
                            }
                        }
                    }
                    break;
                case "attest":
                case "relate":
                case "pair":
                case "holds":
                case "itself":
                    hasAttest = true;
                    hasCuratedOnlyRule = true;
                    FlagEphemeralTokens(lineNo, directive, tokens.Skip(2), warnings);
                    break;
                case "line":
                    if (stripped.Contains("::", StringComparison.Ordinal))
                        hasAttest = true;
                    hasCuratedOnlyRule = true;
                    break;
                case "relation":
                    if (tokens.Count < 3)
                    {
                        errors.Add($"Line {lineNo}: 'relation' requires 'relation \"TEXT\" FIELD...'.");
                    }
                    else
                    {
                        string relText = tokens[1];
                        foreach (var field in tokens.Skip(2))
                        {
                            if (field == "{file}")
                                errors.Add($"Line {lineNo}: 'relation' may not use '{{file}}' as a field name.");
                            string bare = field.Contains('.', StringComparison.Ordinal) ? field[(field.LastIndexOf('.') + 1)..] : field;
                            if (string.Equals(relText, field, StringComparison.Ordinal) || string.Equals(relText, bare, StringComparison.Ordinal))
                                errors.Add($"Line {lineNo}: relation \"{relText}\" repeats field name '{field}' (a column/markup name is never its relation wording).");
                            else if (LooksLikeKvpWrapper(relText, declaredNames))
                                warnings.Add($"Line {lineNo}: relation \"{relText}\" looks like a raw KVP column/field identifier rather than documented natural-language relation wording.");
                            FlagEphemeralToken(lineNo, directive, field, warnings);
                        }
                    }
                    break;
                case "value":
                    if (tokens.Count < 3)
                    {
                        errors.Add($"Line {lineNo}: 'value' requires 'value LIST|- FIELD...'.");
                    }
                    else
                    {
                        foreach (var field in tokens.Skip(2))
                        {
                            if (field == "{file}")
                                errors.Add($"Line {lineNo}: 'value' may not use '{{file}}' as a field name.");
                            FlagEphemeralToken(lineNo, directive, field, warnings);
                        }
                    }
                    break;
                case "refer":
                case "type":
                case "index":
                case "surface":
                case "layers":
                case "tree":
                    hasCuratedOnlyRule = true;
                    break;
            }
        }

        // Check declared column names that look like ephemeral pointers and were neither omitted nor keyed.
        foreach (var col in declaredNames)
        {
            if (IsEphemeralPointer(col) && !omittedNames.Contains(col) && !keyedNames.Contains(col))
                warnings.Add($"Column '{col}' is an ephemeral row/byte/line offset and is neither omitted ('omit {col}') nor declared as a scoped 'key'.");
        }

        if (string.IsNullOrWhiteSpace(name))
            errors.Add("Missing required 'name <id>' declaration.");
        if (matches.Count == 0)
            errors.Add("Missing required 'match <glob/file>' declaration.");

        if (hasCuratedOnlyRule && !hasLayout && string.IsNullOrWhiteSpace(like))
            errors.Add("Curated dispositions ('thing', 'attest', 'key', 'relate', ...) require a layout ('tier ...', 'format ...', 'node ...', or 'like ...').");

        bool pureSyntaxTreeGrammar = !string.IsNullOrWhiteSpace(grammar) && !hasLayout && !hasCuratedOnlyRule && string.IsNullOrWhiteSpace(like);
        if (string.IsNullOrWhiteSpace(like) && !pureSyntaxTreeGrammar)
        {
            if (!hasContent)
                warnings.Add("Recording Law warning: no 'content' directive declared. Shared entities will not be decomposed as canonical content unless marked.");
            if (!hasThing)
                warnings.Add("Recording Law warning: no 'thing' directive declared. Records need a primary subject ('thing TIER FIELD').");
            if (!hasAttest)
                warnings.Add("Recording Law warning: no 'attest', 'relate', 'pair', or 'holds' directive declared. No semantic claims will be emitted.");
        }

        if (string.IsNullOrWhiteSpace(grammar) && string.IsNullOrWhiteSpace(format) && string.IsNullOrWhiteSpace(like) && !hasLayout)
            warnings.Add("No 'grammar', 'format', 'tier', or 'like' directive found; the engine defaults to plain UAX #29 'text'.");

        if (!string.IsNullOrWhiteSpace(filename) && matches.Count > 0)
        {
            string baseName = Path.GetFileName(filename.Trim());
            if (baseName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && baseName.Length > 3)
                baseName = baseName[..^3];
            if (!matches.Any(pat => GlobMatches(pat, baseName)))
                warnings.Add($"Sample filename '{Path.GetFileName(filename)}' does not match any declared 'match' pattern ({string.Join(", ", matches)}).");
        }

        return new RecipeLintResult(
            Valid: errors.Count == 0,
            Errors: errors,
            Warnings: warnings,
            Summary: new RecipeLintSummary(
                Name: name,
                Matches: matches,
                Grammar: grammar,
                Format: format,
                Like: like,
                TrustClass: trustClass,
                HasLayout: hasLayout,
                HasContent: hasContent,
                HasThing: hasThing,
                HasAttest: hasAttest,
                DirectiveCount: directives));
    }

    public async Task<RecipeInspectResult> InspectAsync(
        string filename,
        string sampleText,
        string recipeDsl,
        CancellationToken ct = default)
    {
        var (cleanName, lint, cli) = await RunSampleRecipeCliAsync(
            filename,
            sampleText,
            recipeDsl,
            sourceName: "studio",
            witnessLabel: "Recipe Studio",
            trustClass: "UserCuratedResource",
            maxBytes: 2 * 1024 * 1024,
            offlineConnInfo: true,
            dryRunClaims: true,
            timeout: InspectTimeout,
            skipOnLintError: false,
            ct).ConfigureAwait(false);

        var parsed = ParseInspectStdout(cli!.Stdout);
        return new RecipeInspectResult(
            Object: "recipe_studio.inspect",
            Filename: cleanName,
            Lint: lint,
            Executed: true,
            Ok: cli.Ok,
            ExitCode: cli.ExitCode,
            TimedOut: cli.TimedOut,
            ElapsedMs: cli.ElapsedMs,
            RecordsCount: parsed.RecordsCount,
            ClaimsCount: parsed.ClaimsCount,
            UnresolvedObligationsCount: parsed.UnresolvedObligationsCount,
            CompositionsCount: parsed.CompositionsCount,
            Records: parsed.Records,
            Claims: parsed.Claims,
            Obligations: parsed.Obligations,
            Stdout: cli.Stdout,
            Stderr: cli.Stderr);
    }

    public async Task<RecipeAdmitResult> AdmitAsync(
        string filename,
        string sampleText,
        string recipeDsl,
        string? sourceName = null,
        string? witnessName = null,
        CancellationToken ct = default)
    {
        string cleanName = SanitizeSampleFilename(filename);
        var preLint = LintRecipe(recipeDsl ?? "", cleanName);
        string rawSlug = !string.IsNullOrWhiteSpace(sourceName)
            ? sourceName.Trim()
            : (!string.IsNullOrWhiteSpace(preLint.Summary.Name) ? preLint.Summary.Name.Trim() : "studio");
        string safeSlug = SafeSourceRegex.IsMatch(rawSlug)
            ? rawSlug
            : Regex.Replace(rawSlug.ToLowerInvariant(), "[^a-z0-9._-]+", "-").Trim('-', '.');
        if (string.IsNullOrWhiteSpace(safeSlug) || !SafeSourceRegex.IsMatch(safeSlug))
            safeSlug = "studio";

        string witnessLabel = !string.IsNullOrWhiteSpace(witnessName)
            ? witnessName.Trim().Replace('\r', ' ').Replace('\n', ' ')
            : $"Recipe Studio ({safeSlug})";
        string trustClass = !string.IsNullOrWhiteSpace(preLint.Summary.TrustClass)
            ? preLint.Summary.TrustClass.Trim()
            : "UserCuratedResource";

        var (_, lint, cli) = await RunSampleRecipeCliAsync(
            cleanName,
            sampleText,
            recipeDsl ?? "",
            sourceName: safeSlug,
            witnessLabel: witnessLabel,
            trustClass: trustClass,
            maxBytes: 8 * 1024 * 1024,
            offlineConnInfo: false,
            dryRunClaims: false,
            timeout: TimeSpan.FromMinutes(2),
            skipOnLintError: true,
            ct).ConfigureAwait(false);

        if (cli is null)
        {
            return new RecipeAdmitResult(
                Object: "recipe_studio.admit",
                Filename: cleanName,
                Source: safeSlug,
                Witness: witnessLabel,
                TrustClass: trustClass,
                Lint: lint,
                Executed: false,
                Ok: false,
                ExitCode: null,
                TimedOut: false,
                ElapsedMs: 0,
                Stdout: "",
                Stderr: string.Join('\n', lint.Errors));
        }

        return new RecipeAdmitResult(
            Object: "recipe_studio.admit",
            Filename: cleanName,
            Source: safeSlug,
            Witness: witnessLabel,
            TrustClass: trustClass,
            Lint: lint,
            Executed: true,
            Ok: cli.Ok,
            ExitCode: cli.ExitCode,
            TimedOut: cli.TimedOut,
            ElapsedMs: cli.ElapsedMs,
            Stdout: cli.Stdout,
            Stderr: cli.Stderr);
    }

    private async Task<(string CleanName, RecipeLintResult Lint, StudioCliResult? Cli)> RunSampleRecipeCliAsync(
        string filename,
        string sampleText,
        string recipeDsl,
        string sourceName,
        string witnessLabel,
        string trustClass,
        int maxBytes,
        bool offlineConnInfo,
        bool dryRunClaims,
        TimeSpan timeout,
        bool skipOnLintError,
        CancellationToken ct)
    {
        string cleanName = SanitizeSampleFilename(filename);
        if (string.IsNullOrWhiteSpace(recipeDsl))
            throw new ArgumentException("Recipe DSL is required.", nameof(recipeDsl));
        if (sampleText is null)
            throw new ArgumentException("Sample content is required.", nameof(sampleText));
        if (Encoding.UTF8.GetByteCount(sampleText) > maxBytes)
            throw new ArgumentException($"Sample content exceeds the {maxBytes / (1024 * 1024)} MiB studio limit.", nameof(sampleText));

        var lint = LintRecipe(recipeDsl, cleanName);
        if (skipOnLintError && !lint.Valid)
            return (cleanName, lint, null);

        string tempRoot = Path.Combine(Path.GetTempPath(), "laplace-recipe-studio-" + Guid.NewGuid().ToString("N"));
        try
        {
            string tempRecipes = Path.Combine(tempRoot, "recipes");
            string tempSourceDir = Path.Combine(tempRecipes, sourceName);
            string tempSampleDir = Path.Combine(tempRoot, "sample");
            string tempSampleFile = Path.Combine(tempSampleDir, cleanName);

            Directory.CreateDirectory(tempSourceDir);
            Directory.CreateDirectory(tempSampleDir);

            MirrorSharedRecipeAssets(tempRecipes);

            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            await File.WriteAllTextAsync(
                Path.Combine(tempSourceDir, "source"),
                $"name {sourceName}\nwitness {witnessLabel}\nclass {trustClass}\n",
                utf8NoBom,
                ct).ConfigureAwait(false);

            string normalizedDsl = recipeDsl.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!normalizedDsl.EndsWith('\n')) normalizedDsl += '\n';
            await File.WriteAllTextAsync(
                Path.Combine(tempSourceDir, $"{sourceName}.recipe"),
                normalizedDsl,
                utf8NoBom,
                ct).ConfigureAwait(false);

            await File.WriteAllTextAsync(tempSampleFile, sampleText, utf8NoBom, ct).ConfigureAwait(false);

            List<string> args = dryRunClaims
                ? ["ingest", "--claims", "--no-load", "-r", tempRecipes, "-s", sourceName, tempSampleFile]
                : ["ingest", "-r", tempRecipes, "-s", sourceName, tempSampleFile];

            var cli = await RunNativeCliAsync(
                args,
                offlineConnInfo,
                timeout,
                ct,
                workingDirectory: tempRoot,
                recipesOverride: tempRecipes).ConfigureAwait(false);

            return (cleanName, lint, cli);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch (IOException ex)
            {
                logger?.LogWarning(ex, "Recipe studio temp cleanup failed for {TempRoot}", tempRoot);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger?.LogWarning(ex, "Recipe studio temp cleanup denied for {TempRoot}", tempRoot);
            }
        }
    }

    public RecipeDraftResult DraftRecipe(string filename, string sampleText)
    {
        string cleanName = SanitizeSampleFilename(filename);
        string ext = Path.GetExtension(cleanName).ToLowerInvariant();
        string baseStem = Regex.Replace(Path.GetFileNameWithoutExtension(cleanName).ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-');
        if (string.IsNullOrEmpty(baseStem)) baseStem = "custom-source";
        string matchPattern = string.IsNullOrEmpty(ext) ? cleanName : "*" + ext;

        string? detectedGrammar = null;
        string? detectedFormat = null;
        string draftDsl;

        if (ext is ".tsv" or ".tab" or ".psv" or ".csv")
        {
            char sep = ext == ".csv" ? ',' : ext == ".psv" ? '|' : '\t';
            string sepToken = sep == '\t' ? "\\t" : sep.ToString();
            draftDsl = DraftTabularRecipe(baseStem, matchPattern, sep, sepToken, sampleText, out detectedFormat);
        }
        else if (ext is ".jsonl" or ".ndjson")
        {
            detectedFormat = "json";
            detectedGrammar = "json";
            draftDsl = DraftJsonLinesRecipe(baseStem, matchPattern, sampleText);
        }
        else if (ext == ".json")
        {
            detectedGrammar = "json";
            draftDsl = DraftJsonObjectOrTreeRecipe(baseStem, matchPattern, sampleText, out detectedFormat);
        }
        else if (ext == ".xml")
        {
            detectedFormat = "xml";
            detectedGrammar = "xml";
            draftDsl = DraftXmlRecipe(baseStem, matchPattern, sampleText);
        }
        else if (ext == ".pgn")
        {
            detectedGrammar = "pgn";
            draftDsl = $"name {baseStem}\nmatch {matchPattern}\nclass StructuredCorpus\ngrammar pgn\nrecords played\n";
        }
        else if (ext == ".conllu")
        {
            detectedFormat = "conllu";
            detectedGrammar = "conllu";
            draftDsl = $"name {baseStem}\nmatch {matchPattern}\nlike conllu\n";
        }
        else if (ExtensionGrammars.TryGetValue(ext, out var mappedGrammar))
        {
            detectedGrammar = mappedGrammar;
            draftDsl = $"name {baseStem}\nmatch {matchPattern}\nclass UserCuratedResource\ngrammar {mappedGrammar}\n";
        }
        else if (LooksTabSeparated(sampleText))
        {
            draftDsl = DraftTabularRecipe(baseStem, matchPattern, '\t', "\\t", sampleText, out detectedFormat);
        }
        else if (LooksLikeJsonLines(sampleText))
        {
            detectedFormat = "json";
            detectedGrammar = "json";
            draftDsl = DraftJsonLinesRecipe(baseStem, matchPattern, sampleText);
        }
        else
        {
            detectedGrammar = "text";
            draftDsl = $"name {baseStem}\nmatch {matchPattern}\nclass UserCuratedResource\ngrammar text\n";
        }

        var lint = LintRecipe(draftDsl, cleanName);
        return new RecipeDraftResult(
            Object: "recipe_studio.draft",
            Filename: cleanName,
            RecipeDsl: draftDsl,
            Grammar: detectedGrammar,
            Format: detectedFormat,
            Lint: lint);
    }

    private string DraftTabularRecipe(
        string recipeName,
        string matchPattern,
        char separator,
        string sepToken,
        string sampleText,
        out string? format)
    {
        format = null;
        var lines = sampleText.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.TrimStart().StartsWith('#'))
            .Take(6)
            .ToArray();

        if (lines.Length == 0)
        {
            return $"name {recipeName}\nmatch {matchPattern}\ngrammar {(separator == ',' ? "csv" : "tsv")}\n";
        }

        var firstRow = SplitTabularLine(lines[0], separator);
        bool hasHeader = lines.Length > 1 && firstRow.Count >= 2 && firstRow.All(IsValidHeaderName);
        var columns = hasHeader
            ? firstRow.Select(SanitizeFieldToken).ToList()
            : Enumerable.Range(1, firstRow.Count).Select(i => $"col_{i}").ToList();

        var omitted = columns.Where(IsEphemeralPointer).ToList();
        var usable = columns.Where(c => !IsEphemeralPointer(c)).ToList();
        if (usable.Count == 0)
            usable = [.. columns];

        string subject = usable[0];
        var attributes = usable.Skip(1).ToList();
        bool hasQuotes = lines.Any(l => l.Contains('"', StringComparison.Ordinal));

        var sb = new StringBuilder();
        sb.AppendLine($"name {recipeName}");
        sb.AppendLine($"match {matchPattern}");
        sb.AppendLine();
        sb.AppendLine("tier row by \\n");
        if (hasHeader) sb.AppendLine("header");
        else sb.AppendLine("names " + string.Join(' ', columns.Select(QuoteIfSpaced)));
        if (hasQuotes) sb.AppendLine("quoted");
        sb.AppendLine($"tier field by {sepToken}");
        sb.AppendLine();
        sb.AppendLine($"thing row {QuoteIfSpaced(subject)}");
        sb.AppendLine("content " + string.Join(' ', usable.Select(QuoteIfSpaced)));
        if (omitted.Count > 0)
            sb.AppendLine("omit " + string.Join(' ', omitted.Select(QuoteIfSpaced)));

        foreach (var attr in attributes)
        {
            if (IsCategoricalHint(attr))
                sb.AppendLine($"value - {QuoteIfSpaced(attr)}");
            else
                sb.AppendLine($"relation \"documented {HumanizeColumn(attr)} of the record\" {QuoteIfSpaced(attr)}");
        }
        if (attributes.Count > 0)
            sb.AppendLine("attest row " + string.Join(' ', attributes.Select(QuoteIfSpaced)));

        return sb.ToString();
    }

    private static string DraftJsonLinesRecipe(string recipeName, string matchPattern, string sampleText)
    {
        var keys = new List<string>();
        foreach (var raw in sampleText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || !trimmed.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        keys.Add(prop.Name);
                    break;
                }
            }
            catch (JsonException) { }
        }

        if (keys.Count == 0)
            return $"name {recipeName}\nmatch {matchPattern}\nclass UserCuratedResource\ngrammar json\n";

        var omitted = keys.Where(IsEphemeralPointer).ToList();
        var usable = keys.Where(k => !IsEphemeralPointer(k)).ToList();
        if (usable.Count == 0) usable = [.. keys];
        string subject = usable.FirstOrDefault(k => k is "name" or "title" or "term" or "word" or "subject" or "lemma" or "label" or "id") ?? usable[0];
        var attrs = usable.Where(k => !string.Equals(k, subject, StringComparison.Ordinal)).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"name {recipeName}");
        sb.AppendLine($"match {matchPattern}");
        sb.AppendLine("format json");
        sb.AppendLine("tier record by \\n");
        sb.AppendLine();
        sb.AppendLine($"thing record {QuoteIfSpaced(subject)}");
        if (omitted.Count > 0)
            sb.AppendLine("omit " + string.Join(' ', omitted.Select(QuoteIfSpaced)));
        sb.AppendLine("content *");
        foreach (var attr in attrs)
        {
            if (IsCategoricalHint(attr))
                sb.AppendLine($"value - {QuoteIfSpaced(attr)}");
            else
                sb.AppendLine($"relation \"documented {HumanizeColumn(attr)} of the entry\" {QuoteIfSpaced(attr)}");
        }
        if (attrs.Count > 0)
            sb.AppendLine("attest record " + string.Join(' ', attrs.Select(QuoteIfSpaced)));
        return sb.ToString();
    }

    private static string DraftJsonObjectOrTreeRecipe(string recipeName, string matchPattern, string sampleText, out string? format)
    {
        format = null;
        if (LooksLikeJsonLines(sampleText))
        {
            format = "json";
            return DraftJsonLinesRecipe(recipeName, matchPattern, sampleText);
        }
        return $"name {recipeName}\nmatch {matchPattern}\nclass UserCuratedResource\ngrammar json\n";
    }

    private static string DraftXmlRecipe(string recipeName, string matchPattern, string sampleText)
    {
        string? recordElement = null;
        var childNames = new List<string>();
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true, IgnoreWhitespace = true };
            using var sr = new StringReader(sampleText);
            using var reader = XmlReader.Create(sr, settings);
            int rootDepth = -1;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (rootDepth < 0) { rootDepth = reader.Depth; continue; }
                if (recordElement is null && reader.Depth == rootDepth + 1)
                {
                    recordElement = reader.Name;
                    for (int i = 0; i < reader.AttributeCount; i++)
                    {
                        reader.MoveToAttribute(i);
                        if (!reader.Name.StartsWith("xmlns", StringComparison.Ordinal))
                            childNames.Add(reader.Name);
                    }
                    reader.MoveToElement();
                }
                else if (recordElement is not null && reader.Depth == rootDepth + 2 && !childNames.Contains(reader.Name))
                {
                    childNames.Add(reader.Name);
                }
            }
        }
        catch (XmlException) { }

        if (recordElement is null || childNames.Count == 0)
            return $"name {recipeName}\nmatch {matchPattern}\nclass UserCuratedResource\ngrammar xml\n";

        var omitted = childNames.Where(IsEphemeralPointer).ToList();
        var usable = childNames.Where(c => !IsEphemeralPointer(c)).ToList();
        if (usable.Count == 0) usable = [.. childNames];
        string subject = usable[0];
        var attrs = usable.Skip(1).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"name {recipeName}");
        sb.AppendLine($"match {matchPattern}");
        sb.AppendLine("format xml");
        sb.AppendLine($"records {recordElement}");
        sb.AppendLine();
        sb.AppendLine($"thing {recordElement} {QuoteIfSpaced(subject)}");
        if (omitted.Count > 0)
            sb.AppendLine("omit " + string.Join(' ', omitted.Select(QuoteIfSpaced)));
        sb.AppendLine("content " + string.Join(' ', usable.Select(QuoteIfSpaced)));
        foreach (var attr in attrs)
            sb.AppendLine($"relation \"documented {HumanizeColumn(attr)} of the entry\" {QuoteIfSpaced(attr)}");
        if (attrs.Count > 0)
            sb.AppendLine($"attest {recordElement} " + string.Join(' ', attrs.Select(QuoteIfSpaced)));
        return sb.ToString();
    }

    private async Task<StudioCliResult> RunNativeCliAsync(
        IReadOnlyList<string> args,
        bool offlineConnInfo,
        TimeSpan timeout,
        CancellationToken ct,
        string? workingDirectory = null,
        string? recipesOverride = null)
    {
        var cliPath = ResolveNativeCliPath()
            ?? throw new FileNotFoundException(
                "Native C engine executable ('laplace') not found. Set LAPLACE_ENGINE, LAPLACE_ENGINE_EXE, or LAPLACE_INSTALL_PREFIX.");

        var psi = new ProcessStartInfo(cliPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(cliPath) ?? AppContext.BaseDirectory,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        ConfigureCliEnvironment(psi, offlineConnInfo, recipesOverride);

        var clock = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        using var proc = CapturedProcess.Start(psi, outputLimit: 256 * 1024);
        try
        {
            var result = await proc.Completion.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            clock.Stop();
            return new StudioCliResult(
                Ok: result.ExitCode == 0,
                ExitCode: result.ExitCode,
                TimedOut: false,
                ElapsedMs: Math.Round(clock.Elapsed.TotalMilliseconds, 1),
                Cli: cliPath,
                Arguments: args,
                Stdout: result.Stdout,
                Stderr: result.Stderr,
                OutputIncomplete: result.OutputIncomplete);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            clock.Stop();
            string stdout = "", stderr = "";
            bool incomplete = true;
            try
            {
                await proc.TerminateAsync().ConfigureAwait(false);
                var partial = await proc.Completion.ConfigureAwait(false);
                stdout = partial.Stdout;
                stderr = partial.Stderr;
                incomplete = partial.OutputIncomplete;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Timed-out CLI termination failed for {CliPath}", cliPath);
            }
            return new StudioCliResult(
                Ok: false,
                ExitCode: null,
                TimedOut: true,
                ElapsedMs: Math.Round(clock.Elapsed.TotalMilliseconds, 1),
                Cli: cliPath,
                Arguments: args,
                Stdout: stdout,
                Stderr: string.IsNullOrEmpty(stderr) ? $"Timed out after {timeout.TotalSeconds:0}s." : stderr,
                OutputIncomplete: incomplete);
        }
    }

    private void ConfigureCliEnvironment(ProcessStartInfo psi, bool offlineConnInfo, string? recipesOverride)
    {
        var recipesDir = recipesOverride ?? ResolveRecipesDir();
        if (!string.IsNullOrWhiteSpace(recipesDir))
            psi.Environment["LAPLACE_RECIPES"] = recipesDir;

        var grammarsDir = ResolveGrammarsDir();
        if (!string.IsNullOrWhiteSpace(grammarsDir))
            psi.Environment["LAPLACE_GRAMMARS"] = grammarsDir;

        var dataDir = ResolveDataDir();
        if (!string.IsNullOrWhiteSpace(dataDir))
            psi.Environment["LAPLACE_DATA"] = dataDir;

        TrySetEnvFile(psi, "LAPLACE_TIER0", () => LaplaceInstall.ResolveT0Perfcache());
        TrySetEnvFile(psi, "LAPLACE_HIGHWAY", () => LaplaceInstall.ResolveHighwayPerfcache());

        var flags = ResolveAuxiliaryShareFile("LAPLACE_FLAGS", "tier0.flags");
        if (!string.IsNullOrWhiteSpace(flags))
            psi.Environment["LAPLACE_FLAGS"] = flags;

        var manifest = ResolveManifestDir();
        if (!string.IsNullOrWhiteSpace(manifest))
            psi.Environment["LAPLACE_MANIFEST"] = manifest;

        psi.Environment["HOME"] = Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home
            ? home
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        psi.Environment["LAPLACE_CONNINFO"] = offlineConnInfo
            ? "host=127.0.0.1 port=1 dbname=laplace_studio_offline connect_timeout=1"
            : LaplaceEngineRuntime.ResolveLibpqConnInfo();
    }

    private static void TrySetEnvFile(ProcessStartInfo psi, string key, Func<string> resolver)
    {
        try
        {
            var val = resolver();
            if (!string.IsNullOrWhiteSpace(val))
                psi.Environment[key] = val;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or DirectoryNotFoundException)
        {
        }
    }

    private void MirrorSharedRecipeAssets(string targetRecipesDir)
    {
        var sourceRecipesDir = ResolveRecipesDir();
        if (sourceRecipesDir is null || !Directory.Exists(sourceRecipesDir)) return;

        foreach (var file in Directory.EnumerateFiles(sourceRecipesDir, "*.recipe"))
        {
            File.Copy(file, Path.Combine(targetRecipesDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var sub in new[] { "formats", "vocabularies" })
        {
            var srcSub = Path.Combine(sourceRecipesDir, sub);
            if (!Directory.Exists(srcSub)) continue;
            var dstSub = Path.Combine(targetRecipesDir, sub);
            Directory.CreateDirectory(dstSub);
            foreach (var file in Directory.EnumerateFiles(srcSub))
                File.Copy(file, Path.Combine(dstSub, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static (
        int RecordsCount,
        int ClaimsCount,
        long? UnresolvedObligationsCount,
        long? CompositionsCount,
        IReadOnlyList<InspectRecordPreview> Records,
        IReadOnlyList<string> Claims,
        IReadOnlyList<string> Obligations) ParseInspectStdout(string stdout)
    {
        var records = new List<InspectRecordPreview>();
        var claims = new List<string>();
        var obligations = new List<string>();
        int recordCount = 0, claimCount = 0;
        long? unresolved = null, compositions = null;

        int? currentRecIndex = null;
        var currentHeld = new List<string>();
        var currentClaims = new List<string>();
        bool inObligationsSection = false;

        void FlushRecord()
        {
            if (currentRecIndex is null) return;
            if (records.Count < 100)
                records.Add(new InspectRecordPreview(currentRecIndex.Value, currentHeld.ToArray(), currentClaims.ToArray()));
            currentHeld.Clear();
            currentClaims.Clear();
        }

        foreach (var raw in stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;

            if (trimmed.StartsWith("-- record ", StringComparison.Ordinal))
            {
                inObligationsSection = false;
                FlushRecord();
                if (int.TryParse(trimmed["-- record ".Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                    currentRecIndex = idx;
                else
                    currentRecIndex = recordCount + 1;
                recordCount++;
                continue;
            }

            if (trimmed.StartsWith("== unresolved obligations:", StringComparison.Ordinal))
            {
                FlushRecord();
                currentRecIndex = null;
                inObligationsSection = true;
                var m = Regex.Match(trimmed, @"^== unresolved obligations:\s*([0-9,]+)");
                if (m.Success && long.TryParse(m.Groups[1].Value.Replace(",", "", StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out long u))
                    unresolved = u;
                continue;
            }

            if (trimmed.StartsWith("== ", StringComparison.Ordinal))
            {
                FlushRecord();
                currentRecIndex = null;
                inObligationsSection = false;
                continue;
            }

            if (inObligationsSection)
            {
                if (obligations.Count < 100) obligations.Add(trimmed);
                continue;
            }

            var compMatch = Regex.Match(trimmed, @"^([0-9,]+)\s+compositions,\s+([0-9,]+)\s+reused;\s+([0-9,]+)\s+claims said");
            if (compMatch.Success && long.TryParse(compMatch.Groups[1].Value.Replace(",", "", StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out long comps))
            {
                compositions = comps;
                continue;
            }

            if (currentRecIndex is not null && trimmed.StartsWith("+ ", StringComparison.Ordinal))
            {
                if (currentHeld.Count < 32)
                    currentHeld.Add(trimmed[2..].Trim());
                continue;
            }

            if (trimmed.StartsWith('[') && !trimmed.StartsWith("[earlier output", StringComparison.Ordinal))
            {
                claimCount++;
                if (claims.Count < 200) claims.Add(trimmed);
                if (currentRecIndex is not null && currentClaims.Count < 64)
                    currentClaims.Add(trimmed);
            }
        }

        FlushRecord();
        return (recordCount, claimCount, unresolved, compositions, records, claims, obligations);
    }

    public static string? ResolveNativeCliPath()
    {
        string exeName = OperatingSystem.IsWindows() ? "laplace.exe" : "laplace";

        foreach (var envKey in new[] { "LAPLACE_ENGINE", "LAPLACE_ENGINE_EXE", "LAPLACE_ENGINE_BIN" })
        {
            var explicitExe = LaplaceInstall.TryGetProcessOrMachineSetting(envKey);
            if (!string.IsNullOrWhiteSpace(explicitExe))
            {
                var trimmed = explicitExe.Trim();
                if (File.Exists(trimmed)) return Path.GetFullPath(trimmed);
                if (Directory.Exists(trimmed))
                {
                    var inside = Path.Combine(trimmed, exeName);
                    if (File.Exists(inside)) return Path.GetFullPath(inside);
                }
            }
        }

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "cli", exeName),
            Path.Combine(AppContext.BaseDirectory, exeName),
            Path.Combine(AppContext.BaseDirectory, "bin", exeName),
            Path.Combine(AppContext.BaseDirectory, "native", "recipe-engine", "bin", exeName),
        };

        var prefix = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_INSTALL_PREFIX");
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            candidates.Add(Path.Combine(prefix.Trim(), "bin", exeName));
            candidates.Add(Path.Combine(prefix.Trim(), "cli", exeName));
        }

        var pkg = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_ENGINE_PACKAGE");
        if (!string.IsNullOrWhiteSpace(pkg))
            candidates.Add(Path.Combine(pkg.Trim(), "bin", exeName));

        var declaredBuild = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_BUILD");
        if (!string.IsNullOrWhiteSpace(declaredBuild))
        {
            foreach (var sub in new[]
            {
                Path.Combine("Laplace-Engine", "icx-release", exeName),
                Path.Combine("Laplace-Engine", "icx-release", "src", exeName),
                Path.Combine("Laplace-Engine", "release", exeName),
                Path.Combine("Laplace-Engine", "build", exeName),
            })
            {
                candidates.Add(Path.Combine(declaredBuild.Trim(), sub));
            }
        }

        if (LaplaceInstall.TryDefaultBuildRoot(out var buildRoot))
        {
            candidates.Add(Path.Combine(buildRoot, "icx-release", exeName));
            candidates.Add(Path.Combine(buildRoot, "build-win", exeName));
            candidates.Add(Path.Combine(buildRoot, "engine", exeName));
        }

        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists)
            ?? NativeEngineCli.ResolveBinary();
    }

    public static string? ResolveRecipesDir()
    {
        var configured = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_RECIPES");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured.Trim()))
            return Path.GetFullPath(configured.Trim());

        var candidates = new List<string>();
        var prefix = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_INSTALL_PREFIX");
        if (!string.IsNullOrWhiteSpace(prefix))
            candidates.Add(Path.Combine(prefix.Trim(), "share", "laplace", "recipes"));

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "share", "laplace", "recipes"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "native", "recipe-engine", "share", "laplace", "recipes"));

        var pkg = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_ENGINE_PACKAGE");
        if (!string.IsNullOrWhiteSpace(pkg))
            candidates.Add(Path.Combine(pkg.Trim(), "share", "laplace", "recipes"));

        var src = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_SRC");
        if (!string.IsNullOrWhiteSpace(src))
            candidates.Add(Path.Combine(src.Trim(), "Laplace-Engine", "recipes"));

        if (LaplaceInstall.TryRepoRoot(out var repoRoot))
        {
            var parent = Path.GetDirectoryName(repoRoot);
            if (parent is not null)
                candidates.Add(Path.Combine(parent, "Laplace-Engine", "recipes"));
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName, "Laplace-Engine", "recipes"));

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "recipes"));

        return candidates.Select(Path.GetFullPath).FirstOrDefault(d => File.Exists(Path.Combine(d, "order")))
            ?? candidates.Select(Path.GetFullPath).FirstOrDefault(Directory.Exists);
    }

    public static string? ResolveGrammarsDir()
    {
        var configured = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_GRAMMARS");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured.Trim()))
            return Path.GetFullPath(configured.Trim());

        var candidates = new List<string>();
        var prefix = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_INSTALL_PREFIX");
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            candidates.Add(Path.Combine(prefix.Trim(), "lib", "grammars"));
            candidates.Add(Path.Combine(prefix.Trim(), "grammars"));
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "cli", "grammars"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "grammars"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "lib", "grammars"));

        var pkg = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_ENGINE_PACKAGE");
        if (!string.IsNullOrWhiteSpace(pkg))
        {
            candidates.Add(Path.Combine(pkg.Trim(), "lib", "grammars"));
            candidates.Add(Path.Combine(pkg.Trim(), "grammars"));
        }

        var declaredBuild = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_BUILD");
        if (!string.IsNullOrWhiteSpace(declaredBuild))
        {
            candidates.Add(Path.Combine(declaredBuild.Trim(), "Laplace-Engine", "icx-release", "grammars"));
            candidates.Add(Path.Combine(declaredBuild.Trim(), "grammars"));
        }

        var cliPath = ResolveNativeCliPath();
        if (cliPath is not null)
        {
            var cliDir = Path.GetDirectoryName(cliPath)!;
            candidates.Add(Path.Combine(cliDir, "grammars"));
            candidates.Add(Path.Combine(cliDir, "..", "lib", "grammars"));
        }

        return candidates.Select(Path.GetFullPath).FirstOrDefault(Directory.Exists);
    }

    public static string? ResolveDataDir()
    {
        var configured = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_DATA");
        return string.IsNullOrWhiteSpace(configured) ? null : Path.GetFullPath(configured.Trim());
    }

    private static string? ResolveAuxiliaryShareFile(string envKey, string fileName)
    {
        var fromEnv = LaplaceInstall.TryGetProcessOrMachineSetting(envKey);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv.Trim()))
            return Path.GetFullPath(fromEnv.Trim());

        var bundled = Path.Combine(AppContext.BaseDirectory, "share", "laplace", fileName);
        if (File.Exists(bundled)) return Path.GetFullPath(bundled);

        var prefix = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_INSTALL_PREFIX");
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            var p = Path.Combine(prefix.Trim(), "share", "laplace", fileName);
            if (File.Exists(p)) return Path.GetFullPath(p);
        }

        try
        {
            var t0Dir = Path.GetDirectoryName(LaplaceInstall.ResolveT0Perfcache());
            if (t0Dir is not null)
            {
                var sibling = Path.Combine(t0Dir, fileName);
                if (File.Exists(sibling)) return Path.GetFullPath(sibling);
            }
        }
        catch (Exception) { }

        return null;
    }

    private static string? ResolveManifestDir()
    {
        var fromEnv = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_MANIFEST");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv.Trim()))
            return Path.GetFullPath(fromEnv.Trim());

        var bundled = Path.Combine(AppContext.BaseDirectory, "share", "laplace", "manifest");
        if (Directory.Exists(bundled)) return Path.GetFullPath(bundled);

        var prefix = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_INSTALL_PREFIX");
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            var p = Path.Combine(prefix.Trim(), "share", "laplace", "manifest");
            if (Directory.Exists(p)) return Path.GetFullPath(p);
        }

        if (LaplaceInstall.TryRepoRoot(out var repoRoot))
        {
            var parent = Path.GetDirectoryName(repoRoot);
            if (parent is not null)
            {
                var nativeManifest = Path.Combine(parent, "Laplace-Native", "manifest");
                if (Directory.Exists(nativeManifest)) return Path.GetFullPath(nativeManifest);
            }
        }

        return null;
    }

    private static IReadOnlyList<string> LoadTrustClasses()
    {
        var manifestDir = ResolveManifestDir();
        if (manifestDir is not null)
        {
            var tomlPath = Path.Combine(manifestDir, "trust_classes.toml");
            if (File.Exists(tomlPath))
            {
                var parsed = new List<string>();
                foreach (var raw in File.ReadLines(tomlPath))
                {
                    var line = raw.Split('#', 2)[0].Trim();
                    if (line.Length == 0 || line.StartsWith('[')) continue;
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                        parsed.Add(line[..eq].Trim());
                }
                if (parsed.Count > 0) return parsed;
            }
        }
        return DefaultTrustClasses;
    }

    private static IReadOnlyList<string> EnumerateCompiledGrammars(string? grammarsDir)
    {
        if (grammarsDir is null || !Directory.Exists(grammarsDir))
            return [];

        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(grammarsDir))
        {
            var name = Path.GetFileName(file);
            string? trimmed = null;
            if (name.StartsWith("libtree-sitter-", StringComparison.OrdinalIgnoreCase))
                trimmed = name["libtree-sitter-".Length..];
            else if (name.StartsWith("tree-sitter-", StringComparison.OrdinalIgnoreCase))
                trimmed = name["tree-sitter-".Length..];
            if (trimmed is null) continue;

            if (trimmed.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[..^4];
            else if (trimmed.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[..^3];
            else if (trimmed.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[..^6];
            else
                continue;

            if (trimmed.Length > 0)
                result.Add(trimmed);
        }
        return result.ToArray();
    }

    private static IReadOnlyList<string> ReadOrderFile(string recipesDir)
    {
        var orderPath = Path.Combine(recipesDir, "order");
        if (!File.Exists(orderPath)) return [];
        var list = new List<string>();
        foreach (var raw in File.ReadLines(orderPath))
        {
            var name = StripRecipeComment(raw).Trim();
            if (name.Length > 0) list.Add(name);
        }
        return list;
    }

    private sealed class ParsedSource
    {
        public string Name = "";
        public List<string> Aliases = [];
        public string Witness = "";
        public string Called = "";
        public string Lineage = "";
        public string TrustClass = "";
        public string Held = "";
        public List<string> Roots = [];
        public List<string> Files = [];
        public List<string> After = [];
        public List<string> Reads = [];
        public double? Room;
    }

    private static ParsedSource ParseSourceFile(string path, string fallbackName)
    {
        var src = new ParsedSource { Name = fallbackName };
        foreach (var raw in File.ReadLines(path))
        {
            var line = StripRecipeComment(raw).Trim();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            string key = parts[0];
            string rest = parts.Length > 1 ? parts[1].Trim() : "";
            switch (key)
            {
                case "name":
                    if (rest.Length > 0) src.Name = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
                    break;
                case "aliases":
                    src.Aliases.AddRange(rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    break;
                case "witness":
                    src.Witness = rest;
                    break;
                case "called":
                    src.Called = rest;
                    break;
                case "lineage":
                    src.Lineage = rest;
                    break;
                case "class":
                    if (rest.Length > 0) src.TrustClass = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
                    break;
                case "held":
                    src.Held = rest;
                    break;
                case "root":
                    if (rest.Length > 0) src.Roots.Add(rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0]);
                    break;
                case "files":
                    if (rest.Length > 0) src.Files.Add(rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0]);
                    break;
                case "after":
                    src.After.AddRange(rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    break;
                case "reads":
                    src.Reads.AddRange(rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    break;
                case "room":
                    if (double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out double r))
                        src.Room = r;
                    break;
            }
        }
        return src;
    }

    private static IReadOnlyList<EstateRecipeSummary> EnumerateRecipesIn(string dir, string recipesRoot)
    {
        var list = new List<EstateRecipeSummary>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.recipe").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var meta = ReadRecipeHeader(file);
            list.Add(new EstateRecipeSummary(
                Name: meta.Name,
                FileName: Path.GetFileName(file),
                RelativePath: Path.GetRelativePath(recipesRoot, file).Replace('\\', '/'),
                Grammar: meta.Grammar,
                Format: meta.Format,
                Like: meta.Like,
                Matches: meta.Matches));
        }
        return list;
    }

    private static StudioRecipeCatalogItem ReadCatalogItem(string file, string recipesRoot, string? sourceName)
    {
        var meta = ReadRecipeHeader(file);
        return new StudioRecipeCatalogItem(
            Name: meta.Name,
            Source: sourceName,
            RelativePath: Path.GetRelativePath(recipesRoot, file).Replace('\\', '/'),
            Grammar: meta.Grammar,
            Format: meta.Format,
            Like: meta.Like,
            Matches: meta.Matches,
            Content: File.ReadAllText(file));
    }

    private static (string Name, string Grammar, string Format, string Like, IReadOnlyList<string> Matches) ReadRecipeHeader(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        string grammar = "", format = "", like = "";
        var matches = new List<string>();
        foreach (var raw in File.ReadLines(path))
        {
            var stripped = StripRecipeComment(raw).Trim();
            if (stripped.Length == 0 || !TryTokenizeLine(stripped, out var tokens) || tokens.Count == 0)
                continue;
            switch (tokens[0])
            {
                case "name" when tokens.Count > 1:
                    name = tokens[1];
                    break;
                case "grammar" when tokens.Count > 1:
                    grammar = tokens[1];
                    break;
                case "format" when tokens.Count > 1:
                    format = tokens[1];
                    break;
                case "like" when tokens.Count > 1:
                    like = tokens[1];
                    break;
                case "match" when tokens.Count > 1:
                    matches.AddRange(tokens.Skip(1));
                    break;
            }
        }
        return (name, grammar, format, like, matches);
    }

    private static string ResolveWitnessLabel(ParsedSource src)
    {
        if (!string.IsNullOrWhiteSpace(src.Witness) && !src.Witness.Contains('{', StringComparison.Ordinal))
            return src.Witness;
        if (!string.IsNullOrWhiteSpace(src.Called))
            return src.Called;
        return src.Name;
    }

    private static string? TryComputeWitnessId(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        try
        {
            return Hash128.Text(label).ToHex();
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or FileNotFoundException)
        {
            return null;
        }
    }

    private static (bool Staged, string Status, string? MatchedPath) EvaluateStaging(ParsedSource src, string? dataDir)
    {
        bool needsData = src.Roots.Concat(src.Files).Any(p => p.Contains("$LAPLACE_DATA", StringComparison.Ordinal));
        if (needsData && string.IsNullOrWhiteSpace(dataDir))
            return (false, !string.IsNullOrWhiteSpace(src.Held) ? "held" : "not_staged", null);

        if (src.Files.Count > 0)
        {
            foreach (var rawPattern in src.Files)
            {
                var hit = FindFirstExpandedMatch(rawPattern, dataDir, requireNonEmptyFile: true);
                if (hit is not null)
                    return (true, "staged_on_disk", hit);
            }
            return (false, !string.IsNullOrWhiteSpace(src.Held) ? "held" : "not_staged", null);
        }

        foreach (var rawPattern in src.Roots)
        {
            var hit = FindFirstExpandedMatch(rawPattern, dataDir, requireNonEmptyFile: false);
            if (hit is not null)
                return (true, "staged_on_disk", hit);
        }

        return (false, !string.IsNullOrWhiteSpace(src.Held) ? "held" : "not_staged", null);
    }

    private static string? FindFirstExpandedMatch(string pattern, string? dataDir, bool requireNonEmptyFile)
    {
        bool missingEnv = false;
        string expanded = Regex.Replace(pattern, @"\$([A-Za-z_][A-Za-z0-9_]*)", m =>
        {
            string varName = m.Groups[1].Value;
            string? value = varName switch
            {
                "LAPLACE_DATA" => dataDir,
                "HOME" => Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } h
                    ? h
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "LAPLACE_SRC" => LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_SRC")
                    ?? (LaplaceInstall.TryRepoRoot(out var r) ? Path.GetDirectoryName(r) : null),
                _ => LaplaceInstall.TryGetProcessOrMachineSetting(varName),
            };
            if (string.IsNullOrWhiteSpace(value))
            {
                missingEnv = true;
                return "";
            }
            return value.Trim();
        });

        if (missingEnv || string.IsNullOrWhiteSpace(expanded))
            return null;

        expanded = expanded.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        foreach (var candidate in ExpandGlobPaths(expanded).Take(8))
        {
            if (File.Exists(candidate))
            {
                if (!requireNonEmptyFile || new FileInfo(candidate).Length > 0)
                    return candidate;
            }
            else if (Directory.Exists(candidate))
            {
                try
                {
                    if (Directory.EnumerateFileSystemEntries(candidate).Any())
                        return candidate;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return null;
    }

    private static IEnumerable<string> ExpandGlobPaths(string path)
    {
        if (path.IndexOfAny(['*', '?', '[']) < 0)
        {
            if (File.Exists(path) || Directory.Exists(path))
                yield return path;
            yield break;
        }

        string? root = Path.GetPathRoot(path);
        string remainder = string.IsNullOrEmpty(root) ? path : path[root.Length..];
        var segments = remainder.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        IEnumerable<string> current = [string.IsNullOrEmpty(root) ? "." : root];
        foreach (var seg in segments)
        {
            bool isWild = seg.IndexOfAny(['*', '?', '[']) >= 0;
            if (!isWild)
            {
                current = current
                    .Select(dir => Path.Combine(dir, seg))
                    .Where(p => Directory.Exists(p) || File.Exists(p));
            }
            else
            {
                var regex = GlobSegmentToRegex(seg);
                current = current.SelectMany(dir =>
                {
                    if (!Directory.Exists(dir)) return [];
                    try
                    {
                        return Directory.EnumerateFileSystemEntries(dir)
                            .Where(entry => regex.IsMatch(Path.GetFileName(entry)));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return [];
                    }
                });
            }
        }

        foreach (var hit in current)
            yield return hit;
    }

    private static Regex GlobSegmentToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*') sb.Append(".*");
            else if (c == '?') sb.Append('.');
            else if (c == '[')
            {
                int close = glob.IndexOf(']', i + 1);
                if (close > i + 1)
                {
                    sb.Append(glob.AsSpan(i, close - i + 1));
                    i = close;
                }
                else
                {
                    sb.Append(@"\[");
                }
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        sb.Append('$');
        var options = OperatingSystem.IsWindows()
            ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            : RegexOptions.CultureInvariant;
        return new Regex(sb.ToString(), options);
    }

    private static bool GlobMatches(string pattern, string fileName)
    {
        string target = pattern.Contains('/', StringComparison.Ordinal)
            ? pattern[(pattern.LastIndexOf('/') + 1)..]
            : pattern;
        return GlobSegmentToRegex(target).IsMatch(fileName);
    }

    private static string ValidateSourceSelector(string? source)
    {
        string clean = (source ?? "").Trim();
        if (clean.Length == 0)
            throw new ArgumentException("Source name is required.", nameof(source));
        if (!SafeSourceRegex.IsMatch(clean))
            throw new ArgumentException("Source name must match [A-Za-z0-9][A-Za-z0-9._-]{0,127}.", nameof(source));
        return clean;
    }

    private static string SanitizeSampleFilename(string? filename)
    {
        string raw = (filename ?? "").Trim();
        if (raw.Length == 0 || raw.Contains("..", StringComparison.Ordinal)
            || raw.Contains('/') || raw.Contains('\\') || raw.Contains(':'))
            throw new ArgumentException("Filename must be a plain filename without path separators or '..'.", nameof(filename));

        string clean = Path.GetFileName(raw);
        if (string.IsNullOrWhiteSpace(clean) || clean is "." or ".." || clean.Length > 128
            || clean.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Filename contains invalid characters.", nameof(filename));
        return clean;
    }

    private static string StripRecipeComment(string line)
    {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') quoted = !quoted;
            else if (c == '#' && !quoted && (i == 0 || line[i - 1] is ' ' or '\t'))
                return line[..i];
        }
        return line;
    }

    private static bool TryTokenizeLine(string line, out List<string> tokens)
    {
        tokens = [];
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length) break;
            if (line[i] == '"')
            {
                i++;
                int start = i;
                while (i < line.Length && line[i] != '"') i++;
                if (i >= line.Length) return false;
                tokens.Add(line[start..i]);
                i++;
            }
            else
            {
                int start = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
                tokens.Add(line[start..i]);
            }
        }
        return true;
    }

    private static bool IsEphemeralPointer(string token)
    {
        string bare = token.Contains('.', StringComparison.Ordinal) ? token[(token.LastIndexOf('.') + 1)..] : token;
        return EphemeralPointerTokens.Contains(bare);
    }

    private static void FlagEphemeralTokens(int lineNo, string directive, IEnumerable<string> tokens, List<string> warnings)
    {
        foreach (var tok in tokens)
            FlagEphemeralToken(lineNo, directive, tok, warnings);
    }

    private static void FlagEphemeralToken(int lineNo, string directive, string token, List<string> warnings)
    {
        if (IsEphemeralPointer(token))
            warnings.Add($"Line {lineNo}: '{directive}' references ephemeral row/byte/line offset '{token}'. Use 'omit {token}' or scoped 'key TIER {token} within ...' instead of recording offset pointers.");
    }

    private static bool LooksLikeKvpWrapper(string literal, HashSet<string> declaredNames)
    {
        if (declaredNames.Contains(literal))
            return true;
        if (literal.Contains(' ', StringComparison.Ordinal))
            return false;
        return BareIdentifierRegex.IsMatch(literal) && (literal.Contains('_', StringComparison.Ordinal) || literal.Any(char.IsUpper));
    }

    private static bool LooksTabSeparated(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(4).ToArray();
        return lines.Length > 0 && lines.All(l => l.Contains('\t'));
    }

    private static bool LooksLikeJsonLines(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Take(3)
            .ToArray();
        return lines.Length > 0 && lines.All(l => l.StartsWith('{') && l.EndsWith('}'));
    }

    private static List<string> SplitTabularLine(string line, char sep)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == sep && !inQuotes)
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        cells.Add(current.ToString().Trim());
        return cells;
    }

    private static bool IsValidHeaderName(string cell)
    {
        if (string.IsNullOrWhiteSpace(cell) || cell.Length > 48) return false;
        return char.IsLetter(cell[0]) && !cell.Any(char.IsControl);
    }

    private static string SanitizeFieldToken(string raw)
    {
        string cleaned = raw.Trim().Replace('"', '\'');
        return string.IsNullOrEmpty(cleaned) ? "field" : cleaned;
    }

    private static string QuoteIfSpaced(string token)
        => token.Contains(' ', StringComparison.Ordinal) ? $"\"{token}\"" : token;

    private static bool IsCategoricalHint(string field)
    {
        string lower = field.ToLowerInvariant();
        return lower is "pos" or "upos" or "lang" or "language" or "type" or "scope" or "category" or "label" or "tag" or "status";
    }

    private static string HumanizeColumn(string col)
        => Regex.Replace(col.Replace('_', ' ').Replace('-', ' '), @"([a-z])([A-Z])", "$1 $2").Trim().ToLowerInvariant();
}
