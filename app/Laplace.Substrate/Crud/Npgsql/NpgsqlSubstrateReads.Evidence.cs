using Laplace.Engine.Core;
using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{

    /// <summary><c>ops.entity_evidence_count(id)</c> — attestation rows whose subject is id.</summary>
    public static Task<long?> EvidenceCountAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ExecuteScalarAsync<long?>(conn,
            SqlCatalog.Get("ops.entity_evidence_count"),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "evidence_count", onError: onError);

    public readonly record struct EvidenceReceiptRow(
        string? TypeIdHex, string TypeLabel, string ObjectIdHex, string ObjectLabel,
        string? SourceLabels, long WitnessCount, decimal EffMu);

    /// <summary><c>ops.evidence_receipt(id, limit)</c>.</summary>
    public static Task<IReadOnlyList<EvidenceReceiptRow>> EvidenceReceiptAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("ops.evidence_receipt"),
            static r => new EvidenceReceiptRow(
                r.IsDBNull(0) ? null : r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5), r.GetDecimal(6)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "evidence_receipt", onError: onError);

    public readonly record struct AttestationDisplayRow(
        string TypeLabel, string? PeerLabel, string SourceLabel, byte[]? ContextId,
        short Outcome, long ObservationCount);

    /// <summary>
    /// One registered witness's Native series for a canonical claim. Score is
    /// the total outcome score across Games; Tokens counts occurrences.
    /// Neither quantity is the number of distinct witnesses.
    /// </summary>
    public readonly record struct EvidenceSeriesRow(
        string ClaimId, string? TypeId, string ObjectId, string TypeLabel, string ObjectLabel,
        decimal EffMu, string SourceId, string SourceLabel, double Games, double Tokens,
        double Score, double Trust, string? DependenceRoot, int? Position);

    public static Task<IReadOnlyList<EvidenceSeriesRow>> EvidenceSeriesReceiptAsync(
        NpgsqlConnection conn, byte[] id, int limit, bool inbound, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("ops.evidence_series_receipt"),
            static r => new EvidenceSeriesRow(
                r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2),
                r.GetString(3), r.GetString(4), r.GetDecimal(5), r.GetString(6), r.GetString(7),
                r.GetDouble(8), r.GetDouble(9), r.GetDouble(10), r.GetDouble(11),
                r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetInt32(13)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("limit", limit);
                p.AddWithValue("inbound", inbound);
            }, ct: ct, label: "evidence_series_receipt", onError: onError);

    /// <summary><c>ops.attestations_out(id)</c> with bounded display labels after evidence election.</summary>
    public static Task<IReadOnlyList<AttestationDisplayRow>> AttestationsOutDisplayAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("inspection.attestations_out"),
            static r => new AttestationDisplayRow(
                r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : (byte[])r[3], r.GetInt16(4), r.GetInt64(5)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "attestations_out_display", onError: onError);

    /// <summary><c>ops.attestations_in(id)</c> with bounded display labels (peer = subject).</summary>
    public static Task<IReadOnlyList<AttestationDisplayRow>> AttestationsInDisplayAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("inspection.attestations_in"),
            static r => new AttestationDisplayRow(
                r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : (byte[])r[3], r.GetInt16(4), r.GetInt64(5)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "attestations_in_display", onError: onError);

    public readonly record struct OrdinalCountRow(long Ordinal, long? Count);

    /// <summary>Batch <c>ops.evidence_count(NULL, NULL, id)</c> over an array, keyed by ordinal.</summary>
    public static Task<IReadOnlyList<OrdinalCountRow>> EvidenceCountsBatchAsync(
        NpgsqlConnection conn, byte[][] ids, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("ops.evidence_counts_batch"),
            static r => new OrdinalCountRow(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1)),
            p =>
            {
                var param = p.AddWithValue("ids", ids);
                param.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
            }, ct: ct, label: "evidence_counts_batch", onError: onError);

    public readonly record struct OrdinalAttestationRow(
        long Ordinal, string TypeIdHex, string ObjectIdHex, string SourceIdHex, string? ContextIdHex,
        short Outcome, long ObservationCount);

    /// <summary>Batch <c>ops.attestations_out(id, perId)</c> over an array, keyed by ordinal.</summary>
    public static Task<IReadOnlyList<OrdinalAttestationRow>> AttestationsOutBatchAsync(
        NpgsqlConnection conn, byte[][] ids, int perId, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("ops.attestations_out_batch"),
            static r => new OrdinalAttestationRow(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetInt16(5), r.GetInt64(6)),
            p =>
            {
                var param = p.AddWithValue("ids", ids);
                param.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
                p.AddWithValue("per_id", perId);
            }, ct: ct, label: "attestations_out_batch", onError: onError);

    public readonly record struct TargetEvidenceRow(
        byte[]? EntityId, string? EntityLabel, string? TypeIdHex, string? TypeLabel,
        string? ObjectIdHex, string? ObjectLabel, string? SourceLabels, long? WitnessCount, decimal? EffMu);

    /// <summary>
    /// Resolve a free-form target, then its evidence receipts — one round trip. A
    /// resolvable-but-unwitnessed target returns exactly one anchor row with every
    /// evidence column NULL (the LEFT JOIN LATERAL), which the caller filters out.
    /// </summary>
    public static Task<IReadOnlyList<TargetEvidenceRow>> EvidenceForTargetAsync(
        NpgsqlConnection conn, string target, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("ops.evidence_for_target"),
            static r => new TargetEvidenceRow(
                r.IsDBNull(0) ? null : (byte[])r[0],
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetInt64(7),
                r.IsDBNull(8) ? null : r.GetDecimal(8)),
            p =>
            {
                BindCanonicalReference(p, target);
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "evidence_for_target", onError: onError);

}
