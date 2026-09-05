using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    public readonly record struct UserArtifactObservation(
        long Bytes,
        DateTimeOffset? ModifiedAt);

    /// <summary>
    /// Whether one tenant source has confirmed membership of the exact physical file.
    /// Global content identity does not imply this source-scoped occurrence.
    /// </summary>
    public static async Task<bool> HasConfirmedUserArtifactOccurrenceAsync(
        NpgsqlConnection connection,
        byte[] fileId,
        byte[] sourceId,
        CancellationToken ct)
    {
        bool? exists = await NpgsqlRead.ExecuteScalarAsync<bool>(connection, """
            SELECT EXISTS (
                SELECT 1
                FROM laplace.attestations a
                JOIN laplace.entities e
                  ON e.id = a.object_id
                 AND e.type_id = @file_type
                WHERE a.subject_id = @source AND a.source_id = @source
                  AND a.object_id = @file AND a.type_id = @type
                  AND a.outcome = @outcome
            )
            """,
            parameters =>
            {
                parameters.Add("source", NpgsqlDbType.Bytea).Value = sourceId;
                parameters.Add("file", NpgsqlDbType.Bytea).Value = fileId;
                parameters.Add("type", NpgsqlDbType.Bytea).Value =
                    RelationTypeRegistry.RelationTypeId(UserArtifactContent.MembershipRelation).ToBytes();
                parameters.Add("file_type", NpgsqlDbType.Bytea).Value =
                    EntityTypeRegistry.SourceFile.ToBytes();
                parameters.Add("outcome", NpgsqlDbType.Smallint).Value =
                    (short)AttestationOutcome.Confirm;
            },
            ct: ct,
            label: "confirmed_user_artifact_occurrence").ConfigureAwait(false);
        return exists is true;
    }

    /// <summary>The newest successful physical observation of a source-owned file.</summary>
    public static async Task<UserArtifactObservation?> UserArtifactObservationAsync(
        NpgsqlConnection connection,
        string sourceName,
        byte[] fileId,
        CancellationToken ct)
    {
        var rows = await NpgsqlRead.ReadRowsAsync(connection, """
            SELECT bytes, modified_at
            FROM laplace.ingest_file_journal
            WHERE source_name = @source_name
              AND file_id = @file
              AND status = 'ok'
            ORDER BY ended_at DESC NULLS LAST, run_id DESC
            LIMIT 1
            """,
            static reader => new UserArtifactObservation(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1)),
            parameters =>
            {
                parameters.Add("source_name", NpgsqlDbType.Text).Value = sourceName;
                parameters.Add("file", NpgsqlDbType.Bytea).Value = fileId;
            },
            ct: ct,
            label: "user_artifact_observation").ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>Confirmed contexts for one prompt under its tenant prompt source.</summary>
    public static Task<IReadOnlyList<string>> ConfirmedPromptContextsAsync(
        NpgsqlConnection connection,
        byte[] promptId,
        byte[] promptSourceId,
        CancellationToken ct) =>
        NpgsqlRead.ReadRowsAsync(connection, """
            SELECT DISTINCT encode(COALESCE(context_id, object_id), 'hex')
            FROM laplace.attestations
            WHERE subject_id = @subject
              AND source_id = @source
              AND type_id = @type
              AND outcome = @outcome
            ORDER BY 1
            """,
            static reader => reader.GetString(0),
            parameters =>
            {
                parameters.Add("subject", NpgsqlDbType.Bytea).Value = promptId;
                parameters.Add("source", NpgsqlDbType.Bytea).Value = promptSourceId;
                parameters.Add("type", NpgsqlDbType.Bytea).Value =
                    RelationTypeRegistry.RelationTypeId(ConversationContent.MembershipRelation).ToBytes();
                parameters.Add("outcome", NpgsqlDbType.Smallint).Value =
                    (short)AttestationOutcome.Confirm;
            },
            ct: ct,
            label: "confirmed_prompt_contexts");
}
