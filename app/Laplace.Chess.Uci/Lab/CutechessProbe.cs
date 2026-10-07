using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Laplace.Chess.Uci.Lab;

/// <summary>
/// CuteChess as deploy/cutechess-release.json pins it: the CLI by executing it (which proves the loader and Qt Core),
/// and the official GUI bound to its retained source build, its selected Qt SDK, and an offscreen QApplication
/// initialization, without opening a desktop window.
/// </summary>
public static class CutechessProbe
{
    public static JsonObject Probe(IToolRunner runner, string binary, JsonNode lock_, string? qtVersion = null)
    {
        string version = lock_["version"]!.GetValue<string>();
        var reply = runner.Run(new ToolRun(binary, ["--version"], Timeout: TimeSpan.FromSeconds(120)));
        if (reply.ExitCode != 0)
            throw new InvalidOperationException($"{binary} --version failed ({reply.ExitCode}): {reply.Output.Trim()}");
        string output = reply.Stdout.Trim();
        if (!Regex.IsMatch(output, "^cutechess-cli " + Regex.Escape(version) + @"[ \t]*\r?$", RegexOptions.Multiline))
            throw new InvalidOperationException($"{binary} is not locked CuteChess {version}: {Clip(output, 800)}");
        var match = Regex.Match(output, @"Using Qt version (\d+\.\d+\.\d+)");
        if (!match.Success || new Version(match.Groups[1].Value) < new Version(6, 8, 0))
            throw new InvalidOperationException($"{binary} requires a working Qt >=6.8 runtime: {Clip(output, 800)}");
        qtVersion ??= lock_["qt_version"]?.GetValue<string>();
        if (qtVersion is not null && match.Groups[1].Value != qtVersion)
            throw new InvalidOperationException($"{binary} uses Qt {match.Groups[1].Value}; expected {qtVersion}");
        return new JsonObject
        {
            ["component"] = "cutechess", ["version"] = version, ["qt_version"] = match.Groups[1].Value, ["path"] = binary, ["ready"] = true,
        };
    }

    public static JsonObject VerifySource(string path, JsonNode lock_)
    {
        string root = SourceIntegrity.RealPath(SourceIntegrity.GitText(path, "rev-parse", "--show-toplevel"));
        if (root != SourceIntegrity.RealPath(path)) throw new InvalidOperationException($"{path} is not a standalone dependency repository");
        static string Normalize(string v)
        {
            v = v.TrimEnd('/');
            if (v.EndsWith(".git", StringComparison.Ordinal)) v = v[..^4];
            return v.Replace("git@github.com:", "https://github.com/").Replace("ssh://git@github.com/", "https://github.com/");
        }
        string origin = SourceIntegrity.GitText(path, "remote", "get-url", "origin");
        if (Normalize(origin) != Normalize(lock_["repository"]!.GetValue<string>()))
            throw new InvalidOperationException($"{path} origin does not match {lock_["repository"]}");
        string actual = SourceIntegrity.GitText(path, "rev-parse", "HEAD");
        string commit = lock_["commit"]!.GetValue<string>();
        if (actual != commit) throw new InvalidOperationException($"{path} is at {actual}; expected {commit}");
        if (SourceIntegrity.GitText(path, "status", "--porcelain", "--untracked-files=all").Length > 0)
            throw new InvalidOperationException($"{path} contains local changes; preserving it without building");
        var integrity = SourceIntegrity.VerifyCheckout(path, actual, "CuteChess");
        string version = File.ReadAllText(Path.Combine(path, ".version")).Trim();
        if (version != lock_["version"]!.GetValue<string>() || !File.Exists(Path.Combine(path, "CMakeLists.txt")))
            throw new InvalidOperationException($"{path} does not contain the locked CuteChess {lock_["version"]} source");
        return integrity;
    }

    private static readonly string[] GuiModules = ["Core", "Gui", "Widgets", "Concurrent", "Svg", "PrintSupport", "Core5Compat"];

    /// <summary>Identify the selected SDK inputs; runtime loading is established by <see cref="ProbeGui"/>.</summary>
    public static JsonObject GuiInventory(string qtPrefix, JsonNode lock_)
    {
        qtPrefix = SourceIntegrity.RealPath(qtPrefix);
        string qtVersion = lock_["qt_version"]!.GetValue<string>();
        string versionFile = Path.Combine(qtPrefix, "lib", "cmake", "Qt6", "Qt6ConfigVersion.cmake");
        string versionText = File.ReadAllText(versionFile);
        var versionFiles = new JsonObject { [versionFile] = LabFiles.Sha256(versionFile) };
        // Qt 6.11 wraps CMake's basic version file in its compatibility policy; follow only the fixed same-directory include.
        if (Regex.IsMatch(versionText, @"^\s*include\s*\(\s*""\$\{CMAKE_CURRENT_LIST_DIR\}/Qt6ConfigVersionImpl\.cmake""\s*\)\s*$", RegexOptions.Multiline))
        {
            string impl = Path.Combine(Path.GetDirectoryName(versionFile)!, "Qt6ConfigVersionImpl.cmake");
            versionText += "\n" + File.ReadAllText(impl);
            versionFiles[impl] = LabFiles.Sha256(impl);
        }
        var versions = Regex.Matches(versionText, @"^\s*set\s*\(\s*PACKAGE_VERSION\s+""?(\d+\.\d+\.\d+)""?\s*\)\s*$", RegexOptions.Multiline)
            .Select(static m => m.Groups[1].Value).ToList();
        if (versions.Count != 1 || versions[0] != qtVersion || new Version(versions[0]) < new Version(6, 8, 0))
            throw new InvalidOperationException($"GUI requires the selected Qt {qtVersion} SDK >=6.8");
        var modules = new JsonObject();
        foreach (var name in GuiModules)
        {
            string config = Path.Combine(qtPrefix, "lib", "cmake", "Qt6" + name, $"Qt6{name}Config.cmake");
            if (!File.Exists(config)) throw new InvalidOperationException($"GUI requires Qt module {name}: {config}");
            modules[name] = new JsonObject { ["config"] = config, ["config_sha256"] = LabFiles.Sha256(config) };
        }
        string[] pluginNames = OperatingSystem.IsWindows()
            ? ["platforms/qoffscreen.dll", "platforms/qminimal.dll", "platforms/qwindows.dll", "imageformats/qsvg.dll", "iconengines/qsvgicon.dll"]
            : ["platforms/libqoffscreen.so", "platforms/libqminimal.so", "platforms/libqxcb.so", "platforms/libqwayland.so",
               "platforms/libqwayland-generic.so", "platforms/libqwayland-egl.so", "imageformats/libqsvg.so", "iconengines/libqsvgicon.so"];
        var plugins = new JsonObject();
        foreach (var name in pluginNames)
        {
            string path = Path.Combine(qtPrefix, "plugins", name.Replace('/', Path.DirectorySeparatorChar));
            bool present = File.Exists(path);
            plugins[name] = new JsonObject { ["path"] = path, ["present"] = present, ["sha256"] = present ? LabFiles.Sha256(path) : null };
        }
        var offscreen = plugins[pluginNames[0]]!;
        if (!offscreen["present"]!.GetValue<bool>()) throw new InvalidOperationException($"GUI offscreen platform plugin missing: {offscreen["path"]}");
        return new JsonObject
        {
            ["prefix"] = qtPrefix, ["version"] = versions[0], ["version_files"] = versionFiles, ["modules"] = modules,
            ["module_identity_scope"] = "SDK CMake configuration files, not loaded library identities",
            ["plugins"] = plugins, ["offscreen"] = offscreen.DeepClone(),
        };
    }

    public static Dictionary<string, string> GuiEnvironment(string qtPrefix)
    {
        qtPrefix = SourceIntegrity.RealPath(qtPrefix);
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["QT_PLUGIN_PATH"] = Path.Combine(qtPrefix, "plugins"),
            ["QT_QPA_PLATFORM_PLUGIN_PATH"] = Path.Combine(qtPrefix, "plugins", "platforms"),
        };
        if (OperatingSystem.IsLinux())
        {
            // DT_RUNPATH is searched after LD_LIBRARY_PATH: select this SDK's runtime first.
            string selected = Path.Combine(qtPrefix, "lib");
            var inherited = (Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "").Split(Path.PathSeparator)
                .Where(p => p.Length > 0 && p != selected);
            env["LD_LIBRARY_PATH"] = string.Join(Path.PathSeparator, new[] { selected }.Concat(inherited));
        }
        return env;
    }

    /// <summary>Run upstream QApplication initialization through --version on the offscreen platform.</summary>
    public static JsonObject ProbeGui(IToolRunner runner, string binary, JsonNode lock_, string qtPrefix, string? work = null)
    {
        var info = new FileInfo(binary);
        if (info.LinkTarget is not null || !info.Exists)
            throw new InvalidOperationException($"GUI executable must be a regular direct build/install file: {binary}");
        binary = SourceIntegrity.RealPath(binary);
        string version = lock_["version"]!.GetValue<string>(), qtVersion = lock_["qt_version"]!.GetValue<string>();
        var inventory = GuiInventory(qtPrefix, lock_);
        string before = LabFiles.Sha256(binary);
        string scratch = work ?? Path.Combine(Environment.GetEnvironmentVariable("LAPLACE_WORK_ROOT") is { Length: > 0 } w ? w : "/build/laplace/work", "chess-tools");
        Directory.CreateDirectory(scratch);
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (k, v) in GuiEnvironment(qtPrefix)) env[k] = v;
        env["QT_QPA_PLATFORM"] = "offscreen";
        env["QT_DEBUG_PLUGINS"] = "1";
        env["QT_LOGGING_RULES"] = "qt.core.library.debug=true;qt.core.plugin.*.debug=true";
        env["QT_MESSAGE_PATTERN"] = "%{category}: %{message}";
        foreach (var k in new[] { "DISPLAY", "WAYLAND_DISPLAY", "QT_QPA_PLATFORMTHEME", "QT_QPA_GENERIC_PLUGINS" }) env[k] = null;
        string root = Path.Combine(scratch, "cutechess-gui-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        ToolResult result;
        try
        {
            foreach (var (key, name) in new[] { ("XDG_CONFIG_HOME", "config"), ("XDG_CONFIG_DIRS", "config-dirs"), ("XDG_DATA_HOME", "data"),
                         ("XDG_DATA_DIRS", "data-dirs"), ("XDG_CACHE_HOME", "cache"), ("XDG_RUNTIME_DIR", "runtime") })
            {
                string path = Path.Combine(root, name);
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
                else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                env[key] = path;
            }
            result = runner.Run(new ToolRun(binary, ["-platform", "offscreen", "--version"], root, env, TimeSpan.FromSeconds(30)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"CuteChess GUI initialization failed ({result.ExitCode}): {Tail(result.Stderr, 2000)}");
        if (!Regex.IsMatch(result.Stdout, "^Cute Chess " + Regex.Escape(version) + @"[ \t]*\r?$", RegexOptions.Multiline))
            throw new InvalidOperationException($"GUI is not locked Cute Chess {version}: {Clip(result.Stdout, 800)}");
        if (!Regex.IsMatch(result.Stdout, "^Using Qt version " + Regex.Escape(qtVersion) + @"[ \t]*\r?$", RegexOptions.Multiline))
            throw new InvalidOperationException($"GUI does not use selected Qt {qtVersion}: {Clip(result.Stdout, 800)}");
        var loaded = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(result.Stderr, @"^qt\.core\.library:\s+(""(?:[^""\\]|\\.)*"")\s+loaded library\s*$", RegexOptions.Multiline))
        {
            string path = JsonSerializer.Deserialize<string>(m.Groups[1].Value)!;
            if (Path.GetFileName(path) is "libqoffscreen.so" or "qoffscreen.dll") loaded.Add(SourceIntegrity.RealPath(path));
        }
        string expected = SourceIntegrity.RealPath(inventory["offscreen"]!["path"]!.GetValue<string>());
        if (loaded.Count != 1 || loaded.Min != expected)
            throw new InvalidOperationException($"GUI did not load the selected offscreen plugin: expected {expected}, observed [{string.Join(", ", loaded)}]");
        if (LabFiles.Sha256(binary) != before || !SourceIntegrity.SameJson(GuiInventory(qtPrefix, lock_), inventory))
            throw new InvalidOperationException("GUI executable or selected Qt inputs changed during the runtime probe");
        var direct = new JsonObject();
        foreach (var (k, v) in GuiEnvironment(qtPrefix)) direct[k] = v;
        return new JsonObject
        {
            ["component"] = "cutechess-gui", ["version"] = version, ["qt_version"] = qtVersion, ["path"] = binary, ["binary_sha256"] = before,
            ["ready"] = true, ["status"] = "ready-headless", ["qapplication_initialized"] = true, ["qt"] = inventory,
            ["loaded_platform_plugin"] = expected, ["plugin_diagnostics_sha256"] = LabFiles.Sha256(Encoding.UTF8.GetBytes(result.Stderr)),
            ["scope"] = "official GUI QApplication initialization and offscreen platform loading through --version",
            ["settings_scope"] = OperatingSystem.IsLinux() ? "isolated XDG directories" : "temporary XDG directories; platform native settings are not isolated",
            ["interactive_desktop_tested"] = false, ["interactive_desktop_ready"] = null,
            ["direct_launch"] = new JsonObject { ["argv"] = Json.Array([binary]), ["environment"] = direct },
        };
    }

    /// <summary>Bind the direct installed executable and the selected SDK to a verified official build.</summary>
    public static JsonObject VerifyGuiInstall(IToolRunner runner, string binary, string receiptPath, JsonNode lock_, string? work = null)
    {
        byte[] receiptBytes = File.ReadAllBytes(receiptPath);
        var receipt = JsonNode.Parse(receiptBytes)!;
        if (receipt["schema"]?.GetValue<string>() != "laplace.cutechess-gui-source-build.v1"
            || receipt["repository"]?.GetValue<string>() != lock_["repository"]!.GetValue<string>()
            || receipt["commit"]?.GetValue<string>() != lock_["commit"]!.GetValue<string>()
            || receipt["build_target"]?.GetValue<string>() != "gui")
            throw new InvalidOperationException("GUI build receipt does not match the selected official source");
        string source = receipt["source"]!.GetValue<string>();
        var integrity = VerifySource(source, lock_);
        if (!SourceIntegrity.SameJson(integrity, receipt["source_integrity"]))
            throw new InvalidOperationException("GUI source identity differs from the retained build");
        var info = new FileInfo(binary);
        if (info.LinkTarget is not null || !info.Exists || LabFiles.Sha256(binary) != receipt["binary_sha256"]?.GetValue<string>())
            throw new InvalidOperationException("GUI installed executable differs from the retained official build");
        var qt = receipt["runtime"]!["qt"]!;
        string qtPrefix = qt["prefix"]!.GetValue<string>();
        if (!SourceIntegrity.SameJson(GuiInventory(qtPrefix, lock_), qt))
            throw new InvalidOperationException("GUI selected Qt inputs differ from the retained build");
        var runtime = ProbeGui(runner, binary, lock_, qtPrefix, work);
        if (runtime["binary_sha256"]!.GetValue<string>() != receipt["binary_sha256"]!.GetValue<string>()
            || !SourceIntegrity.SameJson(VerifySource(source, lock_), integrity))
            throw new InvalidOperationException("GUI installed executable or source changed during verification");
        if (!File.ReadAllBytes(receiptPath).AsSpan().SequenceEqual(receiptBytes))
            throw new InvalidOperationException("GUI retained build receipt changed during verification");
        return new JsonObject
        {
            ["build_receipt"] = receiptPath, ["build_receipt_sha256"] = LabFiles.Sha256(receiptBytes),
            ["repository"] = receipt["repository"]!.DeepClone(), ["commit"] = receipt["commit"]!.DeepClone(), ["source"] = source,
            ["build_target"] = "gui", ["runtime"] = runtime,
        };
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n];
    private static string Tail(string s, int n) => s.Length <= n ? s : s[^n..];
}
