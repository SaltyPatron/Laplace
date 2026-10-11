using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class ManagedServiceDatabaseTests
{
    // The host-authenticated local route of the platform the tests run on: the peer socket on Linux, SSPI over
    // loopback on Windows. The role and database are the installation's own.
    private static readonly string Local = OperatingSystem.IsWindows()
        ? "Host=127.0.0.1;Username=laplace;Database=laplace-mono"
        : "Host=/var/run/postgresql;Username=laplace_admin;Database=laplace";

    [Fact]
    public void LocalRoutePreservesServingConfiguration()
    {
        var parsed = new NpgsqlConnectionStringBuilder(ManagedServiceDatabase.Resolve(Local + ";Command Timeout=8;Search Path=laplace,public"));
        Assert.Equal(OperatingSystem.IsWindows() ? "127.0.0.1" : "/var/run/postgresql", parsed.Host);
        Assert.Equal(OperatingSystem.IsWindows() ? "laplace" : "laplace_admin", parsed.Username);
        Assert.Equal(8, parsed.CommandTimeout);
        Assert.Equal("laplace,public", parsed.SearchPath);
    }

    [Fact]
    public void VerifiedRemoteRoutePreservesConfiguredDestinationCredentialsAndPort()
    {
        var parsed = new NpgsqlConnectionStringBuilder(ManagedServiceDatabase.Resolve(
            "Host=knowledge.example;Port=6432;Username=bot;Database=curated;SSL Mode=VerifyFull;"
            + "Root Certificate=authority.pem;Password=test-sentinel-not-a-secret;Timeout=5"));
        Assert.Equal("knowledge.example", parsed.Host);
        Assert.Equal(6432, parsed.Port);
        Assert.Equal("bot", parsed.Username);
        Assert.Equal("curated", parsed.Database);
        Assert.Equal(SslMode.VerifyFull, parsed.SslMode);
        Assert.Equal("authority.pem", parsed.RootCertificate);
        Assert.Equal("test-sentinel-not-a-secret", parsed.Password);
        Assert.Equal(5, parsed.Timeout);
    }

    [Fact]
    public void OrderedFallbackIsPreservedWithoutLoadBalancing()
    {
        var parsed = new NpgsqlConnectionStringBuilder(ManagedServiceDatabase.Resolve(
            "Host=cloud.example,server.example;Username=bot;Database=curated;SSL Mode=VerifyFull"));
        Assert.Equal("cloud.example,server.example", parsed.Host);
        Assert.False(parsed.LoadBalanceHosts);
    }

    // The cluster Laplace-Operations declares keeps its socket in /tmp: a Unix-domain socket all the same.
    [Fact]
    public void AnotherSocketDirectoryIsStillTheLocalRoute()
    {
        if (OperatingSystem.IsWindows()) return;
        var parsed = new NpgsqlConnectionStringBuilder(ManagedServiceDatabase.Resolve("Host=/tmp;Username=laplace;Database=laplace-mono"));
        Assert.Equal("/tmp", parsed.Host);
    }

    public static IEnumerable<object[]> Unsafe() => new[]
    {
        "Host=hart-server;Username=laplace_admin;Database=laplace",
        "Host=tmp;Username=laplace_admin;Database=laplace",
        "Host=/tmp/../srv;Username=laplace_admin;Database=laplace",
        "Host=192.168.1.2;Username=laplace;Database=laplace-mono",
        "Host=/var/run/postgresql,192.168.1.2;Username=laplace_admin;Database=laplace",
        "Host=127.0.0.1,192.168.1.2;Username=laplace;Database=laplace-mono",
        "Host=knowledge.example;Username=bot;Database=curated;SSL Mode=Require",
        "Host=knowledge.example;Username=bot;Database=curated;SSL Mode=VerifyCA",
        "Host=knowledge.example;Database=curated;SSL Mode=VerifyFull",
        "Host=knowledge.example;Username=bot;SSL Mode=VerifyFull",
        Local + ";Port=test-sentinel-not-a-secret",
        Local + ";Password='test-sentinel-not-a-secret",
        Local + ";unknown-key=test-sentinel-not-a-secret",
    }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Unsafe))]
    public void UnsafeOrMalformedOverridesFailWithoutEchoingValues(string input)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ManagedServiceDatabase.Resolve(input));
        Assert.DoesNotContain("test-sentinel-not-a-secret", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(";Password=")]
    [InlineData(";Passfile=")]
    public void EmptyCredentialPlaceholdersAreNormalizedAway(string empty)
    {
        var parsed = new NpgsqlConnectionStringBuilder(ManagedServiceDatabase.Resolve(Local + empty));
        Assert.False(parsed.ShouldSerialize("Password"));
        Assert.False(parsed.ShouldSerialize("Passfile"));
    }
}
