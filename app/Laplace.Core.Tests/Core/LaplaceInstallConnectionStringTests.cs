using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

/// <summary>
/// LAPLACE_DB precedence law: an explicit Database= inside LAPLACE_DB is
/// authoritative for default callers; an explicit caller argument overrides it;
/// PGDATABASE-or-default resolution applies only when neither names a database.
/// This knob was once fake on Linux — the deployed API's Database=laplace was
/// silently stomped by the (since-retired) dev-sandbox default — and these
/// tests keep it real.
/// </summary>
public sealed class LaplaceInstallConnectionStringTests : IDisposable
{
    private readonly string? _priorDb = Environment.GetEnvironmentVariable("LAPLACE_DB");
    private readonly string? _priorPg = Environment.GetEnvironmentVariable("PGDATABASE");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LAPLACE_DB", _priorDb);
        Environment.SetEnvironmentVariable("PGDATABASE", _priorPg);
    }

    private static string DatabaseOf(string connString)
    {
        foreach (var part in connString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && string.Equals(part[..eq].Trim(), "Database", StringComparison.OrdinalIgnoreCase))
                return part[(eq + 1)..];
        }
        return "";
    }

    [Fact]
    public void ExplicitDatabaseInEnv_IsAuthoritative_ForDefaultCaller()
    {
        Environment.SetEnvironmentVariable("LAPLACE_DB",
            "Host=/var/run/postgresql;Username=laplace_admin;Database=laplace");
        Environment.SetEnvironmentVariable("PGDATABASE", null);
        Assert.Equal("laplace", DatabaseOf(LaplaceInstall.PostgresConnectionString()));
    }

    [Fact]
    public void ExplicitCallerArgument_OverridesEnvDatabase()
    {
        Environment.SetEnvironmentVariable("LAPLACE_DB",
            "Host=/var/run/postgresql;Username=laplace_admin;Database=laplace");
        Assert.Equal("chess_test",
            DatabaseOf(LaplaceInstall.PostgresConnectionString("chess_test")));
    }

    [Fact]
    public void EnvWithoutDatabase_FallsThroughToResolution()
    {
        Environment.SetEnvironmentVariable("LAPLACE_DB",
            "Host=/var/run/postgresql;Username=laplace_admin");
        Environment.SetEnvironmentVariable("PGDATABASE", "laplace");
        Assert.Equal("laplace", DatabaseOf(LaplaceInstall.PostgresConnectionString()));
    }

    [Fact]
    public void EnvWithoutDatabase_NoPgDatabase_DefaultsToLaplace()
    {
        Environment.SetEnvironmentVariable("LAPLACE_DB",
            "Host=/var/run/postgresql;Username=laplace_admin");
        Environment.SetEnvironmentVariable("PGDATABASE", null);
        Assert.Equal("laplace", DatabaseOf(LaplaceInstall.PostgresConnectionString()));
    }

    [Fact]
    public void ConnectionDefaults_AreAppendedOnce()
    {
        Environment.SetEnvironmentVariable("LAPLACE_DB",
            "Host=/var/run/postgresql;Username=laplace_admin;Database=laplace");
        var s = LaplaceInstall.PostgresConnectionString();
        Assert.Contains("Search Path=laplace,public", s);
        Assert.Contains("Include Error Detail=true", s);
    }
}
