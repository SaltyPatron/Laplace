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
    private static readonly string OtherPlatform = OperatingSystem.IsWindows()
        ? "Host=/var/run/postgresql;Username=laplace_admin;Database=laplace"
        : "Host=127.0.0.1;Username=laplace_admin;Database=laplace";

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
    public void TheOtherPlatformsRouteIsNotThisOnes()
    {
        Assert.Throws<InvalidOperationException>(() => ManagedServiceDatabase.Resolve(OtherPlatform));
    }

    public static IEnumerable<object[]> Unsafe() => new[]
    {
        "Host=hart-server;Username=laplace_admin;Database=laplace",
        "Host=/tmp;Username=laplace_admin;Database=laplace",
        "Host=192.168.1.2;Username=laplace;Database=laplace-mono",
        "Host=/var/run/postgresql,192.168.1.2;Username=laplace_admin;Database=laplace",
        "Host=127.0.0.1,192.168.1.2;Username=laplace;Database=laplace-mono",
        Local + ";Port=5433",
        Local + ";Password=test-sentinel-not-a-secret",
        Local + ";Pwd=test-sentinel-not-a-secret",
        Local + ";Passfile=/tmp/test-sentinel-not-a-secret",
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
