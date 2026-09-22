using System.Runtime.CompilerServices;
using global::Npgsql;
using global::NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    public readonly record struct ForwardTurnObservedRow(
        int Step,
        string Event,
        int RoutingRound,
        string? EntityIdHex,
        string? EntityLabel,
        string? Surface,
        int StrideUsed,
        string? RootIdHex,
        int CandidateCount,
        int OrderedContextCount,
        int ProposalChannelCount,
        int ExactChannelCount,
        long SequenceOccurrences,
        int CoveredOccurrences,
        int RelationFamilies,
        int OpposedOccurrences,
        string? SupportAnchorIdHex,
        string? SupportAnchorLabel,
        string? SupportRelationIdHex,
        string? SupportRelationLabel,
        bool? SupportOutbound,
        long? SupportRating,
        long? SupportRd,
        long? SupportWitnesses,
        int SupportSources,
        int SupportContexts,
        bool DeclaredResult,
        string? ProgramIdHex,
        int RequiredObligations,
        int SatisfiedObligations,
        int RemainingRequired,
        bool Completion,
        string? Disposition,
        string? OutputFingerprintHex,
        string? SemanticActIdHex,
        int OutputCount,
        string[] PriorDiscourseIds);

    public static async IAsyncEnumerable<ForwardTurnObservedRow> ForwardTurnObservedStepsAsync(
        NpgsqlDataSource dataSource,
        string prompt,
        byte[]? session,
        int steps,
        int maxStride,
        double spread,
        int topK,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            SqlCatalog.Get("conversation.forward_turn_observed").Text, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, prompt);
        command.Parameters.AddWithValue(NpgsqlDbType.Bytea, (object?)session ?? DBNull.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, steps);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, maxStride);
        command.Parameters.AddWithValue(NpgsqlDbType.Double, spread);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, topK);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new ForwardTurnObservedRow(
                Step: reader.GetInt32(0),
                Event: reader.GetString(1),
                RoutingRound: reader.GetInt32(2),
                EntityIdHex: reader.IsDBNull(3) ? null : reader.GetString(3),
                EntityLabel: reader.IsDBNull(4) ? null : reader.GetString(4),
                Surface: reader.IsDBNull(5) ? null : reader.GetString(5),
                StrideUsed: reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                RootIdHex: reader.IsDBNull(7) ? null : reader.GetString(7),
                CandidateCount: reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                OrderedContextCount: reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
                ProposalChannelCount: reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                ExactChannelCount: reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                SequenceOccurrences: reader.IsDBNull(12) ? 0 : reader.GetInt64(12),
                CoveredOccurrences: reader.IsDBNull(13) ? 0 : reader.GetInt32(13),
                RelationFamilies: reader.IsDBNull(14) ? 0 : reader.GetInt32(14),
                OpposedOccurrences: reader.IsDBNull(15) ? 0 : reader.GetInt32(15),
                SupportAnchorIdHex: reader.IsDBNull(16) ? null : reader.GetString(16),
                SupportAnchorLabel: reader.IsDBNull(17) ? null : reader.GetString(17),
                SupportRelationIdHex: reader.IsDBNull(18) ? null : reader.GetString(18),
                SupportRelationLabel: reader.IsDBNull(19) ? null : reader.GetString(19),
                SupportOutbound: reader.IsDBNull(20) ? null : reader.GetBoolean(20),
                SupportRating: reader.IsDBNull(21) ? null : reader.GetInt64(21),
                SupportRd: reader.IsDBNull(22) ? null : reader.GetInt64(22),
                SupportWitnesses: reader.IsDBNull(23) ? null : reader.GetInt64(23),
                SupportSources: reader.IsDBNull(24) ? 0 : reader.GetInt32(24),
                SupportContexts: reader.IsDBNull(25) ? 0 : reader.GetInt32(25),
                DeclaredResult: !reader.IsDBNull(26) && reader.GetBoolean(26),
                ProgramIdHex: reader.IsDBNull(27) ? null : reader.GetString(27),
                RequiredObligations: reader.IsDBNull(28) ? 0 : reader.GetInt32(28),
                SatisfiedObligations: reader.IsDBNull(29) ? 0 : reader.GetInt32(29),
                RemainingRequired: reader.IsDBNull(30) ? 0 : reader.GetInt32(30),
                Completion: !reader.IsDBNull(31) && reader.GetBoolean(31),
                Disposition: reader.IsDBNull(32) ? null : reader.GetString(32),
                OutputFingerprintHex: reader.IsDBNull(33) ? null : reader.GetString(33),
                SemanticActIdHex: reader.IsDBNull(34) ? null : reader.GetString(34),
                OutputCount: reader.IsDBNull(35) ? 0 : reader.GetInt32(35),
                PriorDiscourseIds: reader.IsDBNull(36)
                    ? Array.Empty<string>()
                    : reader.GetFieldValue<string[]>(36));
        }
    }
}
