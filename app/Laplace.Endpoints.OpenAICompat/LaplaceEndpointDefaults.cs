namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Endpoint runtime defaults — IIS and <c>dotnet Laplace.Endpoints.OpenAICompat.dll</c> must work
/// without wrapping in scripts\win\env.cmd. Override via laplace.env next to the DLL or real env vars.
/// </summary>
internal static class LaplaceEndpointDefaults
{
    private const string DefaultWindowsDb =
        "Host=localhost;Username=postgres;Password=postgres;Database=laplace;Command Timeout=0";

    private const string DefaultListenUrl = "http://127.0.0.1:5187";

    public static void Initialize()
    {
        LoadEnvFile(Path.Combine(AppContext.BaseDirectory, "laplace.env"));

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LAPLACE_DB")))
            Environment.SetEnvironmentVariable("LAPLACE_DB", DefaultWindowsDb);

        if (Environment.GetEnvironmentVariable("LAPLACE_BILLING_BYPASS") is null)
            Environment.SetEnvironmentVariable("LAPLACE_BILLING_BYPASS", "true");

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_URLS")))
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", DefaultListenUrl);
    }

    public static string ContentRoot => AppContext.BaseDirectory;

    public static string WebRoot => Path.Combine(AppContext.BaseDirectory, "wwwroot");

    public static string ConnectionString
    {
        get
        {
            var s = Environment.GetEnvironmentVariable("LAPLACE_DB") ?? DefaultWindowsDb;
            if (!s.Contains("Include Error Detail", StringComparison.OrdinalIgnoreCase))
                s += ";Include Error Detail=true";
            if (!s.Contains("Search Path", StringComparison.OrdinalIgnoreCase))
                s += ";Search Path=laplace,public";
            return s;
        }
    }

    public static bool BillingBypass =>
        !string.Equals(
            Environment.GetEnvironmentVariable("LAPLACE_BILLING_BYPASS"),
            "false",
            StringComparison.OrdinalIgnoreCase);

    private static void LoadEnvFile(string path)
    {
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path))
        {
            if (line.TrimStart().StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            if (string.IsNullOrEmpty(key)) continue;
            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, val);
        }
    }
}
