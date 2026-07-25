using System;
using System.Threading.Tasks;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Chess.Service.Tests;

// #604 item 4: tenant/user/session/game identity threaded through the play entry point (spec-34
// conversational provenance, stubbed until auth). The record-level plumbing runs everywhere; the
// host-level threading + validation is an integration test (needs a disposable DB, no-op otherwise).
public sealed class ChessPlayIdentityTests
{
    [Fact]
    public void PlaySession_HoldsTenantAndUser()
    {
        var s = new PlaySession(Hash128.Zero, "chess/play/session", recordToSubstrate: false,
            tenantId: "acme", userId: "alice");
        Assert.Equal("acme", s.TenantId);
        Assert.Equal("alice", s.UserId);
    }

    [Fact]
    public void PlaySession_UserDefaultsNull_TenantDefaultsPublic()
    {
        var s = new PlaySession(Hash128.Zero, "chess/play/session", recordToSubstrate: false);
        Assert.Equal("public", s.TenantId);
        Assert.Null(s.UserId);
    }

    [Fact]
    public void ChessPlayStart_CarriesIdentityFields()
    {
        var start = new ChessPlayStart(Guid.NewGuid(), "fen", "ongoing", 0, "acme", "alice", "deadbeef");
        Assert.Equal("acme", start.TenantId);
        Assert.Equal("alice", start.UserId);
        Assert.Equal("deadbeef", start.GameId);
    }

    [Fact]
    public async Task StartPlaySession_ThreadsIdentity_AndRejectsInvalid()
    {
        if (TestDb.ConnString is not { } cs) return; // integration: explicit test DB only
        await using var host = await ChessLiveGameHost.CreateAsync(connString: cs);

        var id = host.StartPlaySession(recordToSubstrate: false, tenantId: "acme", userId: "alice");
        var session = host.GetPlaySession(id)!;
        Assert.Equal("acme", session.TenantId);
        Assert.Equal("alice", session.UserId);

        // tenant and user become canonical-key segments — the spec-34 charset guard rejects
        // anything outside [A-Za-z0-9._@-].
        Assert.Throws<ArgumentException>(() => host.StartPlaySession(tenantId: "bad tenant!"));
        Assert.Throws<ArgumentException>(() => host.StartPlaySession(tenantId: "acme", userId: "bad user!"));
    }
}
