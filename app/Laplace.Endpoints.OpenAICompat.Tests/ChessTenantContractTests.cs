using System.Reflection;
using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// Play session routing is tenant-scoped authority. A session GUID is only a routing
/// handle; possession of it must not authorize another tenant to mutate or finish the
/// session, and request JSON must not be allowed to select substrate provenance.
/// </summary>
public sealed class ChessTenantContractTests
{
    private static Type ChessEndpointsType =>
        typeof(Program).Assembly.GetType(
            "Laplace.Endpoints.OpenAICompat.ChessEndpoints", throwOnError: true)!;

    [Fact]
    public void PlayStartRequest_DoesNotAcceptTenantProvenanceFromBody()
    {
        var requestType = ChessEndpointsType.GetNestedType(
            "PlayStartRequest", BindingFlags.NonPublic);

        Assert.NotNull(requestType);
        Assert.DoesNotContain(
            requestType!.GetProperties(BindingFlags.Instance | BindingFlags.Public),
            p => p.Name.Equals("Tenant", StringComparison.OrdinalIgnoreCase)
                || p.Name.Equals("User", StringComparison.OrdinalIgnoreCase)
                || p.Name.Equals("Requester", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PlaySessionOwnership_RequiresTheAuthenticatedRequesterIncludingWithinOneWorkspace()
    {
        var owner = new OperationPrincipal("tenant-a", "alice", "browser", "google");
        var session = new PlaySession(
            Hash128.OfCanonical("test/chess/play/tenant-binding"),
            "test/chess/play", recordToSubstrate: false,
            tenantId: "tenant-a", userId: "alice", requester: owner);
        var owns = ChessEndpointsType.GetMethod(
            "OwnsPlaySession", BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(owns);
        Assert.True((bool)owns!.Invoke(null, new object?[] { session, owner })!);
        Assert.False((bool)owns.Invoke(null, new object?[] { session, owner with { TenantId = "tenant-b" } })!);
        Assert.False((bool)owns.Invoke(null, new object?[] { session, owner with { Subject = "bob" } })!);
        Assert.False((bool)owns.Invoke(null, new object?[] { session, owner with { Authentication = "api_key" } })!);
        Assert.False((bool)owns.Invoke(null, new object?[] { session, null })!);
        Assert.False((bool)owns.Invoke(null, new object?[] { null, owner })!);
    }

}
