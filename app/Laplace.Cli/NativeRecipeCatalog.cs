using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Laplace.Engine.Core;
using Npgsql;

namespace Laplace.Cli;

internal enum NativeRecipeMode { Ingest, Plan, Claims, NoLoad }

internal sealed record NativeIngestFormat(string Name, string Grammar, string Like, IReadOnlyList<string> Match);

internal sealed record NativeIngestSource(
    string Name,
    IReadOnlyList<string> Aliases,
    string Witness,
    string Lineage,
    string Held,
    IReadOnlyList<string> Roots,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> After,
    IReadOnlyList<string> Reads,
    IReadOnlyList<NativeIngestFormat> Recipes);

/// <summary>
/// Thin reader over <c>Laplace-Engine/recipes</c> (<c>order</c> + <c>&lt;source&gt;/source</c>)
/// and locator/runner for the native <c>laplace</c> executable.
/// </summary>
internal sealed record NativeIngestCatalog(IReadOnlyList<NativeIngestSource> Sources, IReadOnlyList<NativeIngestFormat> Formats)
{
    public string Resolve(string name)
    {
        string key = name.ToLowerInvariant();
        return Sources.FirstOrDefault(s => s.Name == key || s.Aliases.Contains(key, StringComparer.Ordinal))?.Name ?? key;
    }

    public static NativeIngestCatalog Read(string root)
    {
        var order = ReadSourceOrder(root).ToList();
        var sources = new Dictionary<string, NativeIngestSource>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            string path = Path.Combine(directory, "source");
            if (!File.Exists(path)) continue;
            var fields = Fields(path);
            string name = Value(fields, "name");
            string[] aliases = Words(fields, "aliases");
            if (name != Path.GetFileName(directory))
                throw new InvalidDataException($"Source name differs from its directory: {path}");
            foreach (string key in new[] { name }.Concat(aliases))
            {
                if (!Regex.IsMatch(key, "^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant) || !names.Add(key))
                    throw new InvalidDataException($"Invalid or duplicate source name or alias: {key}");
            }
            sources.Add(name, new(
                name,
                aliases,
                Value(fields, "witness"),
                Value(fields, "lineage"),
                Value(fields, "held"),
                Values(fields, "root"),
                Values(fields, "files"),
                Words(fields, "after"),
                Words(fields, "reads"),
                Directory.EnumerateFiles(directory, "*.recipe").Order(StringComparer.Ordinal).Select(p => Format(p, root)).ToArray()));
        }

        var admitted = order.ToHashSet(StringComparer.Ordinal);
        while (order.Count < sources.Count)
        {
            string? next = sources.Keys.FirstOrDefault(name => !admitted.Contains(name) && sources[name].After.All(admitted.Contains));
            if (next is null)
                throw new InvalidDataException("Sources have missing or cyclic dependencies.");
            order.Add(next);
            admitted.Add(next);
        }

        var formats = Directory.EnumerateFiles(root, "*.recipe").Order(StringComparer.Ordinal).Select(p => Format(p, root)).ToArray();
        var recipes = sources.Values.SelectMany(s => s.Recipes).Concat(formats).ToDictionary(r => r.Name, StringComparer.Ordinal);

        NativeIngestFormat Inherit(NativeIngestFormat format, HashSet<string> active)
        {
            if (format.Like.Length == 0) return format;
            if (!active.Add(format.Like) || !recipes.TryGetValue(format.Like, out var parent))
                throw new InvalidDataException($"Missing or cyclic recipe inheritance: {format.Name} -> {format.Like}");
            return format with { Grammar = Inherit(parent, active).Grammar, Like = "" };
        }

        NativeIngestFormat ResolveFormat(NativeIngestFormat format) => Inherit(format, new(StringComparer.Ordinal) { format.Name });
        return new(
            order.Select(name => sources[name] with { Recipes = sources[name].Recipes.Select(ResolveFormat).ToArray() }).ToArray(),
            formats.Select(ResolveFormat).ToArray());
    }

    public static IReadOnlyList<string> ReadSourceOrder(string root)
    {
        var order = new List<string>();
        var earlier = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(Path.Combine(root, "order")))
        {
            string name = line.Split('#', 2)[0].Trim();
            if (name.Length == 0) continue;
            if (!Regex.IsMatch(name, "^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant) || earlier.Contains(name))
                throw new InvalidDataException($"Invalid or duplicate source in recipes/order: {name}");
            string path = Path.Combine(root, name, "source");
            if (!File.Exists(path))
                throw new InvalidDataException($"recipes/order source has no declaration: {name}");
            if (Words(Fields(path), "after").Any(dependency => !earlier.Contains(dependency)))
                throw new InvalidDataException($"Source '{name}' has dependencies missing or later in recipes/order.");
            order.Add(name);
            earlier.Add(name);
        }
        if (order.Count == 0) throw new InvalidDataException("recipes/order is empty.");
        return order.AsReadOnly();
    }

    private static NativeIngestFormat Format(string path, string root)
    {
        var fields = Fields(path);
        string grammar = Value(fields, "grammar"), format = Value(fields, "format");
        if (format.Length > 0)
        {
            if (!Regex.IsMatch(format, "^[a-zA-Z0-9_-]+$", RegexOptions.CultureInvariant))
                throw new InvalidDataException($"Invalid format name: {format}");
            string declared = Value(Fields(Path.Combine(root, "formats", format + ".format")), "grammar");
            if (declared.Length > 0) grammar = declared;
        }
        return new(Value(fields, "name"), grammar, Value(fields, "like"), Words(fields, "match"));
    }

    private static Dictionary<string, List<string>> Fields(string path)
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string raw in File.ReadLines(path))
        {
            string line = Regex.Split(raw, @"(?:^|\s)#", RegexOptions.CultureInvariant)[0].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (!fields.TryGetValue(parts[0], out var values)) fields[parts[0]] = values = [];
            values.Add(parts.Length > 1 ? parts[1] : "");
        }
        return fields;
    }

    private static string Value(Dictionary<string, List<string>> fields, string key) => fields.TryGetValue(key, out var values) ? values[0] : "";
    private static string[] Values(Dictionary<string, List<string>> fields, string key) => fields.TryGetValue(key, out var values) ? values.ToArray() : [];
    private static string[] Words(Dictionary<string, List<string>> fields, string key) => Values(fields, key).SelectMany(v => v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToArray();
}

internal static class NativeRecipeCatalog
{
    private static readonly Lazy<NativeIngestCatalog> _catalog = new(() => NativeIngestCatalog.Read(RecipesRoot));

    public static string RecipesRoot
    {
        get
        {
            var configured = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_RECIPES");
            if (string.IsNullOrWhiteSpace(configured))
            {
                var candidates = new List<string>
                {
                    Path.Combine(AppContext.BaseDirectory, "native", "recipe-engine", "share", "laplace", "recipes"),
                    Path.Combine(AppContext.BaseDirectory, "share", "laplace", "recipes"),
                };
                var pkg = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_ENGINE_PACKAGE");
                if (!string.IsNullOrWhiteSpace(pkg))
                    candidates.Add(Path.Combine(pkg, "share", "laplace", "recipes"));
                var workspace = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_SRC");
                if (!string.IsNullOrWhiteSpace(workspace))
                    candidates.Add(Path.Combine(workspace, "Laplace-Engine", "recipes"));
                if (LaplaceInstall.TryRepoRoot(out var repository))
                    candidates.Add(Path.Combine(Path.GetDirectoryName(repository)!, "Laplace-Engine", "recipes"));
                candidates.Add(Path.Combine(AppContext.BaseDirectory, "recipes"));

                configured = candidates.FirstOrDefault(dir => File.Exists(Path.Combine(dir, "order")))
                    ?? candidates[^1];
            }
            var root = Path.GetFullPath(configured);
            if (!File.Exists(Path.Combine(root, "order")))
                throw new InvalidOperationException($"Native recipe estate has no order declaration: {root}. Declare LAPLACE_RECIPES.");
            return root;
        }
    }

    public static NativeIngestCatalog Catalog => _catalog.Value;
    public static IReadOnlyList<string> SourceOrder => Catalog.Sources.Select(s => s.Name).ToArray();
    public static IReadOnlyList<string> ReadSourceOrder(string recipeRoot) => NativeIngestCatalog.ReadSourceOrder(recipeRoot);
    public static string CanonicalSourceName(string source) => Catalog.Resolve(source);

    public static string HomeDirectory()
        => Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home
            ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ResolveNativeExecutable()
    {
        string name = OperatingSystem.IsWindows() ? "laplace.exe" : "laplace";
        var candidates = new List<string>();
        var explicitExe = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_ENGINE_EXE")
            ?? LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_ENGINE_BIN");
        if (!string.IsNullOrWhiteSpace(explicitExe))
            candidates.Add(explicitExe);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, name));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "native", "recipe-engine", "bin", name));

        var pkg = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_ENGINE_PACKAGE");
        if (!string.IsNullOrWhiteSpace(pkg))
            candidates.Add(Path.Combine(pkg, "bin", name));

        var declaredBuild = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_BUILD");
        if (!string.IsNullOrWhiteSpace(declaredBuild))
            candidates.Add(Path.Combine(declaredBuild, "Laplace-Engine", "icx-release", name));

        var prefix = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_INSTALL_PREFIX");
        if (!string.IsNullOrWhiteSpace(prefix))
            candidates.Add(Path.Combine(prefix, "bin", name));

        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Canonical native engine executable ('laplace') not found.");
    }

    public static string ConnectionInfoFromNpgsql(string connectionString)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Database))
            throw new ArgumentException("The substrate host and database must be declared.", nameof(connectionString));
        static string Pair(string key, string value) => key + "='"
            + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
        var fields = new List<string>
        {
            Pair("host", settings.Host),
            Pair("port", settings.Port.ToString(CultureInfo.InvariantCulture)),
            Pair("dbname", settings.Database),
            Pair("connect_timeout", settings.Timeout.ToString(CultureInfo.InvariantCulture)),
            Pair("sslmode", settings.SslMode switch
            {
                SslMode.Disable => "disable", SslMode.Allow => "allow", SslMode.Prefer => "prefer",
                SslMode.Require => "require", SslMode.VerifyCA => "verify-ca", SslMode.VerifyFull => "verify-full",
                _ => throw new ArgumentOutOfRangeException(nameof(connectionString)),
            }),
        };
        if (!string.IsNullOrEmpty(settings.Username)) fields.Add(Pair("user", settings.Username));
        if (!string.IsNullOrEmpty(settings.Password)) fields.Add(Pair("password", settings.Password));
        if (!string.IsNullOrEmpty(settings.RootCertificate)) fields.Add(Pair("sslrootcert", settings.RootCertificate));
        if (!string.IsNullOrEmpty(settings.SslCertificate)) fields.Add(Pair("sslcert", settings.SslCertificate));
        if (!string.IsNullOrEmpty(settings.SslKey)) fields.Add(Pair("sslkey", settings.SslKey));
        if (!string.IsNullOrEmpty(settings.Options)) fields.Add(Pair("options", settings.Options));
        return string.Join(" ", fields);
    }

    public static async Task<int> RunNativeAsync(IReadOnlyList<string> args, string? connectionInfo, bool expandPrerequisites = true, CancellationToken ct = default)
    {
        string exe = ResolveNativeExecutable();
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        psi.Environment["LAPLACE_RECIPES"] = RecipesRoot;
        psi.Environment["LAPLACE_TIER0"] = LaplaceInstall.ResolveT0Perfcache();
        psi.Environment["LAPLACE_HIGHWAY"] = LaplaceInstall.ResolveHighwayPerfcache();
        psi.Environment["HOME"] = HomeDirectory();
        psi.Environment["LAPLACE_WORKING_SET_BYTES"] = MemoryTopology.WorkingSetFlushEnvelopeBytes.ToString(CultureInfo.InvariantCulture);
        if (!expandPrerequisites) psi.Environment["LAPLACE_INGEST_ONE"] = "1";
        else psi.Environment.Remove("LAPLACE_INGEST_ONE");
        if (!string.IsNullOrWhiteSpace(connectionInfo))
            psi.Environment["LAPLACE_CONNINFO"] = connectionInfo;

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start native engine executable: {exe}");
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return proc.ExitCode;
    }
}
