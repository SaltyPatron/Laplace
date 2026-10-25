using Laplace.Engine.Core;
using global::Npgsql;
using NpgsqlTypes;

namespace Laplace.SubstrateCRUD.Npgsql;

public static partial class NpgsqlSubstrateReads
{

    public readonly record struct TaxonomyTreeRow(
        string Dir, int Ord, string IdHex, string Label, decimal? EffMu);

    /// <summary><c>taxonomy.tree(id)</c> — the IS_A climb/descent around a topic.</summary>
    public static Task<IReadOnlyList<TaxonomyTreeRow>> TaxonomyTreeAsync(
        NpgsqlDataSource dataSource, byte[] id, int depth, int childLimit,
        CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("taxonomy.tree"),
            static r => new TaxonomyTreeRow(
                r.GetString(0), r.GetInt32(1), r.GetString(2),
                r.IsDBNull(3) ? "" : r.GetString(3),
                r.IsDBNull(4) ? (decimal?)null : r.GetDecimal(4)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("depth", RequestedLimit(depth));
                p.AddWithValue("child_limit", RequestedLimit(childLimit));
            },
            timeoutSeconds: 30, ct: ct, label: "taxonomy_tree", onError: onError);

    /// <summary><c>taxonomy.ancestry(entity, band_mask)</c>.</summary>
    public static Task<IReadOnlyList<(string AncestorHex, int Depth)>> AncestryAsync(
        NpgsqlDataSource dataSource, byte[] entityId, byte[] bandMask, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("taxonomy.ancestry"),
            static r => (
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? 0 : r.GetInt32(1)),
            p =>
            {
                p.AddWithValue("entity", entityId);
                p.AddWithValue("band", bandMask);
            }, ct: ct, label: "laplace_ancestry", onError: onError);

    /// <summary><c>lexical.translations(entity, band_mask)</c>.</summary>
    public static Task<IReadOnlyList<(string TranslationHex, string SharedObjectHex)>> TranslationsAsync(
        NpgsqlDataSource dataSource, byte[] entityId, byte[] bandMask, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("lexical.translations"),
            static r => (
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? "" : r.GetString(1)),
            p =>
            {
                p.AddWithValue("entity", entityId);
                p.AddWithValue("band", bandMask);
            }, ct: ct, label: "laplace_translations", onError: onError);

    /// <summary><c>converse.resolve_ref(text)</c> — word, concept, or hex → entity id.</summary>
    public static async Task<byte[]?> ResolveRefAsync(
        NpgsqlDataSource dataSource,string reference,CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError=null)
        => (await ResolveRefWithLabelAsync(dataSource,reference,ct,onError).ConfigureAwait(false))?.Id;

    /// <summary><c>laplace.word_id(surface)</c> — surface spelling → word entity id.</summary>
    public static Task<byte[]?> WordIdAsync(
        NpgsqlDataSource dataSource, string surface, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ExecuteScalarAsync<byte[]>(dataSource,
            SqlCatalog.Get("laplace.word_id"),
            p => p.AddWithValue("w", surface),
            ct: ct, label: "word_id", onError: onError);

    public readonly record struct ConceptMemberRow(
        string IdHex, string Kind, string Label, decimal EffMu, long Witnesses);

    public static Task<IReadOnlyList<ConceptMemberRow>> ConceptMembersAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("lexical.concept_members_rendered"),
            static r => new ConceptMemberRow(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetInt64(4)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "concept_members", onError: onError);

    public readonly record struct ConceptPeerRow(string Peer, string Kind, double Strength);

    public static Task<IReadOnlyList<ConceptPeerRow>> ConceptPeersAsync(
        NpgsqlConnection conn, byte[] id, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn,
            SqlCatalog.Get("lexical.concept_peers"),
            static r => new ConceptPeerRow(r.GetString(0), r.GetString(1), r.GetDouble(2)),
            p =>
            {
                p.Add("id", NpgsqlDbType.Bytea).Value = id;
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "concept_peers", onError: onError);

    public readonly record struct WordPeerIdRow(byte[] WordId, byte[] PeerId, double Strength);

    /// <summary>
    /// Batch WordNet synset peers, morphological forms, and derivational relatives for an array of word entity IDs.
    /// </summary>
    public static Task<IReadOnlyList<WordPeerIdRow>> WordPeerIdsBatchAsync(
        NpgsqlConnection conn, byte[][] wordIds, int limitPerWord, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn,
            SqlCatalog.Get("lexical.word_peer_ids_batch"),
            static r => new WordPeerIdRow((byte[])r[0], (byte[])r[1], r.GetDouble(2)),
            p =>
            {
                var param = p.AddWithValue("ids", wordIds);
                param.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
                p.AddWithValue("limit", limitPerWord);
            }, ct: ct, label: "word_peer_ids_batch", onError: onError);

    public readonly record struct MatchupSenseRow(
        string SenseIdHex, string SynsetIdHex, string SynsetLabel, decimal EffMu, long Witnesses);

    /// <summary><c>lexical.senses(id)</c>.</summary>
    public static Task<IReadOnlyList<MatchupSenseRow>> SensesAsync(
        NpgsqlConnection conn, byte[] id, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("lexical.senses_rendered"),
            static r => new MatchupSenseRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetInt64(4)),
            p => p.Add("id", NpgsqlDbType.Bytea).Value = id,
            ct: ct, label: "senses", onError: onError);

    public readonly record struct TranslationRow(
        string Translation, string Language, decimal EffMu, long Witnesses);

    /// <summary><c>converse.translations(converse.resolve_ref(term), limit)</c>.</summary>
    public static Task<IReadOnlyList<TranslationRow>> TranslationsAsync(
        NpgsqlDataSource dataSource, string term, int limit, CancellationToken ct,
        NpgsqlRead.ErrorTranslator? onError = null) =>
        NpgsqlRead.ReadRowsAsync(dataSource, SqlCatalog.Get("converse.translations"),
            static r => new TranslationRow(
                r.GetString(0), r.GetString(1), r.GetDecimal(2), r.GetInt64(3)),
            p =>
            {
                p.AddWithValue("term", term);
                p.AddWithValue("limit", limit);
            }, ct: ct, label: "translations", onError: onError);
}
