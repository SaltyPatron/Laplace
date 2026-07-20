using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// The ONE feedback lane of the Gödel engine loop (doc 15 §3 G1): confirm/refute
/// attestations from user or API feedback, deposited through the standard writer
/// spine and folded into the same consensus the next walk reads.
/// CLI (QueryCommands.AttestAsync) and the endpoint (POST /v1/feedback) are thin
/// frontends over this implementation — Rule #6, doc 15 invariant I5.
/// </summary>
public static class FeedbackContent
{
    public static readonly Hash128 Source =
        SubstrateCanonicalIds.Source("UserFeedback");

    public readonly record struct ResolvedToken(string Token, Hash128? Id, bool Present)
    {
        public bool Usable => Id is not null && Present;
    }

    public sealed record ConsensusState(long Rating, long Rd, long WitnessCount);

    public sealed record DepositResult(long AttestationsInserted, long ConsensusUpdated);

    /// <summary>
    /// Resolve tokens to content root ids and test substrate presence in ONE
    /// batched round-trip (entities_exist_bitmap). Requires the codepoint
    /// perfcache to be loaded by the caller.
    /// </summary>
    public static async Task<IReadOnlyList<ResolvedToken>> ResolveTokensAsync(
        NpgsqlDataSource ds, IReadOnlyList<string> tokens, CancellationToken ct = default)
    {
        var resolved = new Hash128?[tokens.Count];
        var probeIds = new List<Hash128>(tokens.Count);
        for (int i = 0; i < tokens.Count; i++)
        {
            var rid = TextDecomposer.ContentRootId(tokens[i]);
            resolved[i] = rid;
            if (rid is not null) probeIds.Add(rid.Value);
        }

        byte[] bitmap = probeIds.Count == 0
            ? []
            : await new NpgsqlSubstrateReader(ds).EntitiesExistBitmapAsync(probeIds, ct).ConfigureAwait(false);

        var result = new ResolvedToken[tokens.Count];
        int probeBit = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (resolved[i] is null)
            {
                result[i] = new ResolvedToken(tokens[i], null, false);
                continue;
            }
            bool present = (bitmap[probeBit >> 3] & (1 << (probeBit & 7))) != 0;
            probeBit++;
            result[i] = new ResolvedToken(tokens[i], resolved[i], present);
        }
        return result;
    }

    /// <summary>
    /// True when <paramref name="name"/> is exactly a canonical relation type
    /// name (uppercase, e.g. IS_A, PRECEDES, RELATED_TO). Deliberately strict:
    /// ordinal match against the manifest, so ordinary lowercase tokens can
    /// never be misread as relations.
    /// </summary>
    public static bool TryResolveRelation(string name, out RelationTypeRegistry.RelationTypeResolution resolution)
    {
        resolution = default;
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var r in RelationTypeRegistry.AllCanonical())
        {
            if (string.Equals(r.Canonical, name, StringComparison.Ordinal))
            {
                resolution = r;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// PRECEDES-chain feedback: one confirm/refute witness per consecutive pair
    /// of the token sequence (the n-gram walk's own edge vocabulary).
    /// </summary>
    public static SubstrateChange BuildPrecedesChain(IReadOnlyList<Hash128> ids, bool confirm)
    {
        if (ids.Count < 2)
            throw new ArgumentException("need ≥2 resolved ids for a PRECEDES chain", nameof(ids));

        var b = new SubstrateChangeBuilder(Source, "attest/0", null,
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: ids.Count - 1);
        for (int i = 0; i + 1 < ids.Count; i++)
            b.AddAttestation(NativeAttestation.Categorical(
                ids[i], "PRECEDES", ids[i + 1],
                Source, null, SourceTrust.UserPrompt, confirm: confirm));
        return b.Build();
    }

    /// <summary>
    /// Triple feedback: confirm/refute one (subject, relation, object) claim —
    /// the same consensus row the walk reads (doc 15 invariant I2).
    /// </summary>
    public static SubstrateChange BuildTriple(
        Hash128 subject, string canonicalRelation, Hash128 obj, bool confirm)
    {
        var b = new SubstrateChangeBuilder(Source, "attest/0", null,
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 1);
        b.AddAttestation(NativeAttestation.Categorical(
            subject, canonicalRelation, obj,
            Source, null, SourceTrust.UserPrompt, confirm: confirm));
        return b.Build();
    }

    /// <summary>
    /// Prior consensus state of a triple's row, or null when the claim is new.
    /// </summary>
    public static async Task<ConsensusState?> ConsensusStateAsync(
        NpgsqlDataSource ds, Hash128 subject, Hash128 typeId, Hash128 obj,
        CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT rating, rd, witness_count FROM laplace.consensus "
            + "WHERE subject_id = @s AND type_id = @t AND object_id = @o";
        cmd.Parameters.AddWithValue("s", subject.ToBytes());
        cmd.Parameters.AddWithValue("t", typeId.ToBytes());
        cmd.Parameters.AddWithValue("o", obj.ToBytes());
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new ConsensusState(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
    }

    /// <summary>
    /// Deposit through the standard spine — the fold is inline at apply, so the
    /// very next walk reads the updated consensus (doc 15 invariant I4).
    /// </summary>
    public static async Task<DepositResult> ApplyAsync(
        NpgsqlDataSource ds, SubstrateChange change, CancellationToken ct = default)
    {
        var inner = new NpgsqlSubstrateWriter(ds);
        await using var acc = new ConsensusAccumulatingWriter(inner, ds);
        var result = await ((ISubstrateWriter)acc).ApplyAsync(change, ct).ConfigureAwait(false);
        return new DepositResult(result.AttestationsInserted, acc.CellsFolded);
    }
}
