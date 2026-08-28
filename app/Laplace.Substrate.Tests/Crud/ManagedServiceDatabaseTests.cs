using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class ManagedServiceDatabaseTests
{
    private const string Peer = "Host=/var/run/postgresql;Username=laplace_admin;Database=laplace";

    [Fact]
    public void LocalPeerRoutePreservesServingConfiguration()
    {
        var parsed = new NpgsqlConnectionStringBuilder(ManagedServiceDatabase.Resolve(Peer + ";Command Timeout=8;Search Path=laplace,public"));
        Assert.Equal("/var/run/postgresql", parsed.Host);
        Assert.Equal("laplace_admin", parsed.Username);
        Assert.Equal(8, parsed.CommandTimeout);
        Assert.Equal("laplace,public", parsed.SearchPath);
    }

    [Theory]
    [InlineData("Host=127.0.0.1;Username=laplace_admin;Database=laplace")]
    [InlineData("Host=hart-server;Username=laplace_admin;Database=laplace")]
    [InlineData("Host=/tmp;Username=laplace_admin;Database=laplace")]
    [InlineData("Host=/var/run/postgresql,192.168.1.2;Username=laplace_admin;Database=laplace")]
    [InlineData("Host=/var/run/postgresql;Username=postgres;Database=laplace")]
    [InlineData("Host=/var/run/postgresql;Username=laplace_admin;Database=postgres")]
    [InlineData(Peer + ";Port=5433")]
    [InlineData(Peer + ";Password=test-sentinel-not-a-secret")]
    [InlineData(Peer + ";Pwd=test-sentinel-not-a-secret")]
    [InlineData(Peer + ";Passfile=/tmp/test-sentinel-not-a-secret")]
    [InlineData(Peer + ";Port=test-sentinel-not-a-secret")]
    [InlineData(Peer + ";Password='test-sentinel-not-a-secret")]
    [InlineData(Peer + ";unknown-key=test-sentinel-not-a-secret")]
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
        var parsed = new NpgsqlConnectionStringBuilder(ManagedServiceDatabase.Resolve(Peer + empty));
        Assert.False(parsed.ShouldSerialize("Password"));
        Assert.False(parsed.ShouldSerialize("Passfile"));
    }
}
