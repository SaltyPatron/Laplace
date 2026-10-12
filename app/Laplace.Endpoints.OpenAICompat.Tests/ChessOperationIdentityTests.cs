using Laplace.Chess.Service;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class ChessOperationIdentityTests
{
    [Theory]
    [InlineData("google")]
    [InlineData("microsoft")]
    public void BrowserOperationUsesResolvedAccount(string provider)
    {
        var tenant = new TenantContext("workspace-a", "browser", new Dictionary<string, string>
        { ["user_id"] = "verified-account-id", ["provider"] = provider });
        Assert.Equal(new OperationPrincipal("workspace-a", "verified-account-id", "browser", provider),
            ChessEndpoints.RequestPrincipal(tenant));
    }

    [Fact]
    public void WorkloadOperationRetainsValidatedKeyIdentityWithoutItsSecret()
    {
        var tenant = new TenantContext("workspace-a", "api_key", new Dictionary<string, string>
        { ["key_prefix"] = "validated-key-prefix" });
        Assert.Equal(new OperationPrincipal("workspace-a", "validated-key-prefix", "api_key"),
            ChessEndpoints.RequestPrincipal(tenant));
    }

    [Theory]
    [InlineData("header")]
    [InlineData("invalid_key")]
    [InlineData("browser")]
    [InlineData("api_key")]
    public void MissingAuthenticatedSubjectCannotCreateAnOperation(string kind)
        => Assert.Null(ChessEndpoints.RequestPrincipal(new("workspace-a", kind, TenantContext.NoClaims)));

    [Fact]
    public void WorkspaceCannotReadOrOperateAnotherWorkspacesJob()
    {
        var job = new ChessLabJob("operation", ChessLabJobKind.Cutechess, ChessLabJobState.Completed,
            new Dictionary<string, string>(), new(), new Dictionary<string, string>(), DateTimeOffset.UtcNow,
            Requester: new("workspace-a", "account", "browser", "google"));
        Assert.True(ChessEndpoints.OwnsLabJob(job, "workspace-a"));
        Assert.False(ChessEndpoints.OwnsLabJob(job, "workspace-b"));
        Assert.False(ChessEndpoints.OwnsLabJob(job, ""));
        Assert.False(ChessEndpoints.OwnsLabJob(job with { Requester = null }, "workspace-a"));
        Assert.False(ChessEndpoints.OwnsLabJob(null, "workspace-a"));
    }
}
