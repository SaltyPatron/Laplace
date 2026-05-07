namespace Laplace.Pipeline;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Pipeline.Abstractions;

using Npgsql;

/// <summary>
/// Concrete <see cref="ISignificance"/> — three-layer Glicko-2 over the
/// significance_source / significance_entity / significance_edge tables.
/// Updates flow through the native Glicko-2 kernel via <see cref="IGlicko2"/>.
///
/// Phase 2 / Track D / D7.
///
/// Per substrate invariant 5: this is RATED-SOURCE ATTESTATION, not
/// negative sampling. Trusted source observed/asserts X = weighted win
/// for X scaled by source's rating. Absence = high RD, NOT low rating.
/// </summary>
public sealed class Significance : ISignificance
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IGlicko2         _glicko;

    public Significance(NpgsqlDataSource dataSource, IGlicko2 glicko)
    {
        _dataSource = dataSource;
        _glicko     = glicko;
    }

    public async Task InitializeSourceAsync(AtomId sourceHash, GlickoState initial, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd  = new NpgsqlCommand(
            "INSERT INTO significance_source (source_hash, mu, phi, sigma, games) " +
            "VALUES (@h, @mu, @phi, @sigma, @games) " +
            "ON CONFLICT (source_hash) DO UPDATE SET " +
            "  mu = EXCLUDED.mu, phi = EXCLUDED.phi, sigma = EXCLUDED.sigma, games = EXCLUDED.games, last_updated = now()",
            conn);
        cmd.Parameters.AddWithValue("h",     sourceHash.AsSpan().ToArray());
        cmd.Parameters.AddWithValue("mu",    initial.Mu);
        cmd.Parameters.AddWithValue("phi",   initial.SigmaDisp);
        cmd.Parameters.AddWithValue("sigma", initial.Volatility);
        cmd.Parameters.AddWithValue("games", initial.Games);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task UpdateSourceAsync(AtomId sourceHash, IReadOnlyList<(GlickoState Opponent, double Outcome)> observations, CancellationToken cancellationToken) =>
        UpdateAsync("significance_source", "source_hash", sourceHash, null, observations, cancellationToken);

    public Task UpdateEntityAsync(AtomId entityHash, IReadOnlyList<(GlickoState Opponent, double Outcome)> observations, CancellationToken cancellationToken) =>
        UpdateAsync("significance_entity", "entity_hash", entityHash, null, observations, cancellationToken);

    public Task UpdateEdgeAsync(AtomId edgeTypeHash, AtomId edgeHash, IReadOnlyList<(GlickoState Opponent, double Outcome)> observations, CancellationToken cancellationToken) =>
        UpdateAsync("significance_edge", "edge_hash", edgeHash, edgeTypeHash, observations, cancellationToken);

    public async Task<GlickoState> GetSourceAsync(AtomId sourceHash, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd  = new NpgsqlCommand(
            "SELECT mu, phi, sigma, games FROM significance_source WHERE source_hash = @h", conn);
        cmd.Parameters.AddWithValue("h", sourceHash.AsSpan().ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return GlickoState.Default;
        }
        return new GlickoState(reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetInt32(3));
    }

    public async Task<GlickoState> GetEntityAsync(AtomId entityHash, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd  = new NpgsqlCommand(
            "SELECT mu, phi, sigma, games FROM significance_entity WHERE entity_hash = @h", conn);
        cmd.Parameters.AddWithValue("h", entityHash.AsSpan().ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return GlickoState.Default;
        }
        return new GlickoState(reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetInt32(3));
    }

    public async Task<GlickoState> GetEdgeAsync(AtomId edgeTypeHash, AtomId edgeHash, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd  = new NpgsqlCommand(
            "SELECT mu, phi, sigma, games FROM significance_edge WHERE edge_hash = @h AND edge_type_hash = @t", conn);
        cmd.Parameters.AddWithValue("h", edgeHash.AsSpan().ToArray());
        cmd.Parameters.AddWithValue("t", edgeTypeHash.AsSpan().ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return GlickoState.Default;
        }
        return new GlickoState(reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetInt32(3));
    }

    private async Task UpdateAsync(
        string table, string keyColumn, AtomId key, AtomId? secondaryKey,
        IReadOnlyList<(GlickoState Opponent, double Outcome)> observations,
        CancellationToken cancellationToken)
    {
        // Read current state.
        var current = secondaryKey.HasValue
            ? await GetEdgeAsync(secondaryKey.Value, key, cancellationToken).ConfigureAwait(false)
            : table switch
            {
                "significance_source" => await GetSourceAsync(key, cancellationToken).ConfigureAwait(false),
                "significance_entity" => await GetEntityAsync(key, cancellationToken).ConfigureAwait(false),
                _                     => GlickoState.Default,
            };

        // Apply via native Glicko-2.
        var opponents = new GlickoState[observations.Count];
        var outcomes  = new double[observations.Count];
        for (int i = 0; i < observations.Count; ++i)
        {
            opponents[i] = observations[i].Opponent;
            outcomes[i]  = observations[i].Outcome;
        }
        var updated = _glicko.Apply(current, opponents, outcomes);

        // Write back.
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var sql = secondaryKey.HasValue
            ? $"UPDATE {table} SET mu=@mu, phi=@phi, sigma=@sigma, games=@games, last_updated=now() WHERE {keyColumn}=@h AND edge_type_hash=@t"
            : $"INSERT INTO {table} ({keyColumn}, mu, phi, sigma, games) VALUES (@h, @mu, @phi, @sigma, @games) " +
              $"ON CONFLICT ({keyColumn}) DO UPDATE SET mu=EXCLUDED.mu, phi=EXCLUDED.phi, sigma=EXCLUDED.sigma, games=EXCLUDED.games, last_updated=now()";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("h",     key.AsSpan().ToArray());
        if (secondaryKey.HasValue) { cmd.Parameters.AddWithValue("t", secondaryKey.Value.AsSpan().ToArray()); }
        cmd.Parameters.AddWithValue("mu",    updated.Mu);
        cmd.Parameters.AddWithValue("phi",   updated.SigmaDisp);
        cmd.Parameters.AddWithValue("sigma", updated.Volatility);
        cmd.Parameters.AddWithValue("games", updated.Games);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
