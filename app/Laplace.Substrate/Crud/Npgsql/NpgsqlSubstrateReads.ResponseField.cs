using Npgsql;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{
    public sealed record LexiconRelationRow(byte[] Id, string? Canonical, bool Inverse, bool Descriptive, bool Textual);

    public static Task<IReadOnlyList<LexiconRelationRow>> LexiconRelationsAsync(
        NpgsqlConnection connection, byte[][] relations, string[] criteria, CancellationToken ct = default) =>
        NpgsqlRead.ReadRowsAsync(connection, SqlCatalog.Get("query.lexicon_relations"),
            static r => new LexiconRelationRow(r.GetFieldValue<byte[]>(0),
                r.IsDBNull(1) ? null : r.GetString(1), !r.IsDBNull(2) && r.GetBoolean(2),r.GetBoolean(3),r.GetBoolean(4)),
            p => { p.AddWithValue("relations",relations); p.AddWithValue("criteria",criteria); },ct: ct);

    public sealed record ResponseFieldRow(int Round, int Occurrence, byte[]? Anchor, string? AnchorLabel,
        byte[] Entity, string Label, short Route, byte[]? Via, byte[]? Relation, string? RelationLabel,
        double? Rating, double? Deviation, double? Volatility, long? Matches, long? Witnesses,
        short Tier, double? Distance, bool? Outbound, int[] Origins, int[] Targets, byte[][] BindingClaims, int[] Anchors, bool Textual, int[] ReadingOrigins);

    /// <summary>Two batched Native coupling rounds. Relation, containment and shape remain distinct responses.</summary>
    public static Task<IReadOnlyList<ResponseFieldRow>> ResponseFieldAsync(
        NpgsqlConnection connection, IReadOnlyList<byte[]> occurrences, int fanout, int nearest,
        CancellationToken ct = default) => NpgsqlRead.ReadRowsAsync(connection, SqlCatalog.Get("query.response_field"),
            static r => new ResponseFieldRow(r.GetInt32(0), r.GetInt32(1),
                r.IsDBNull(2) ? null : r.GetFieldValue<byte[]>(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetFieldValue<byte[]>(4), r.GetString(5), r.GetInt16(6),
                r.IsDBNull(7) ? null : r.GetFieldValue<byte[]>(7), r.IsDBNull(8) ? null : r.GetFieldValue<byte[]>(8),
                r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetDouble(10),
                r.IsDBNull(11) ? null : r.GetDouble(11), r.IsDBNull(12) ? null : r.GetDouble(12),
                r.IsDBNull(13) ? null : r.GetInt64(13), r.IsDBNull(14) ? null : r.GetInt64(14),
                r.GetInt16(15), r.IsDBNull(16) ? null : r.GetDouble(16),
                r.IsDBNull(17) ? null : r.GetBoolean(17),r.GetFieldValue<int[]>(18),
                r.GetFieldValue<int[]>(19),r.GetFieldValue<byte[][]>(20),r.GetFieldValue<int[]>(21),r.GetBoolean(22),r.GetFieldValue<int[]>(23)),
            p => { p.AddWithValue("occurrences", occurrences.ToArray()); p.AddWithValue("fanout", fanout); p.AddWithValue("nearest", nearest); },
            ct: ct);
}
