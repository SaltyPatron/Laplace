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
    [Fact]
    public void PlayStartRequest_DoesNotAcceptTenantProvenanceFromBody()
    {
        var requestType = typeof(ChessEndpoints).GetNestedType(
            "PlayStartRequest", BindingFlags.NonPublic);

        Assert.NotNull(requestType);
        Assert.DoesNotContain(
            requestType!.GetProperties(BindingFlags.Instance | BindingFlags.Public),
            p => p.Name.Equals("Tenant", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PlaySessionOwnership_IsExactTenantMatch()
    {
        var session = new PlaySession(
            Hash128.OfCanonical("test/chess/play/tenant-binding"),
            "test/chess/play", recordToSubstrate: false,
            tenantId: "tenant-a");

        Assert.True(ChessEndpoints.OwnsPlaySession(session, "tenant-a"));
        Assert.False(ChessEndpoints.OwnsPlaySession(session, "tenant-b"));
        Assert.False(ChessEndpoints.OwnsPlaySession(null, "tenant-a"));
    }

    [Fact]
    public void PlayRoutes_ResolveTenantAndGuardEveryExistingSessionMutation()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "app", "Laplace.Endpoints.OpenAICompat",
            "EndpointMappings.Chess.cs"));

        Assert.DoesNotContain("req.Tenant", source, StringComparison.Ordinal);
        Assert.Contains("resolver.ResolveAsync(ctx, ct)", source, StringComparison.Ordinal);

        foreach (var route in new[]
                 {
                     "/chess/play/move",
                     "/chess/play/bestmove",
                     "/chess/play/finish",
                 })
        {
            var start = source.IndexOf($"app.MapPost(\"{route}\"", StringComparison.Ordinal);
            Assert.True(start >= 0, $"route not found: {route}");
            var next = source.IndexOf(".WithTags(\"chess\");", start, StringComparison.Ordinal);
            Assert.True(next > start, $"route body not terminated: {route}");
            var block = source[start..next];
            Assert.Contains("resolver.ResolveAsync(ctx, ct)", block, StringComparison.Ordinal);
            Assert.Contains("OwnsPlaySession(live.GetPlaySession(req.SessionId), tenant.TenantId)",
                block, StringComparison.Ordinal);
            Assert.Contains("return Results.NotFound();", block, StringComparison.Ordinal);
        }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "app"))
                && Directory.Exists(Path.Combine(dir.FullName, "extension")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("repository root not found from test output path");
    }
}
