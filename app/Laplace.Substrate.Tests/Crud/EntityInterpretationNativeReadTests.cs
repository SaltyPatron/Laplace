using global::Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class EntityInterpretationNativeReadTests(LocalPgFixture pg)
{
    [Fact]
    public async Task ShapePeersKeepSharedTypeWhenAnUnrelatedSmallerFacetChangesTheSummary()
    {
        string scope = "native-interpretation-shape/" + Guid.NewGuid().ToString("N");
        await using var connection = await pg.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 30;
        command.CommandText = $$"""
            DO $fixture$
            DECLARE
                anchor bytea := public.laplace_hash128_blake3('{{scope}}/anchor');
                peer bytea := public.laplace_hash128_blake3('{{scope}}/peer');
                a0 bytea := public.laplace_hash128_blake3('{{scope}}/a0');
                a1 bytea := public.laplace_hash128_blake3('{{scope}}/a1');
                b0 bytea := public.laplace_hash128_blake3('{{scope}}/b0');
                b1 bytea := public.laplace_hash128_blake3('{{scope}}/b1');
                kind bytea := decode(repeat('7f',16),'hex');
                smaller bytea := decode(repeat('00',16),'hex');
                before_ids bytea[];
                after_ids bytea[];
                physical_before bigint;
            BEGIN
                INSERT INTO laplace.entities(id,tier,type_id) VALUES
                    (anchor,2,kind),(peer,2,kind),
                    (a0,0,kind),(a1,0,kind),(b0,0,kind),(b1,0,kind);
                INSERT INTO laplace.physicalities
                    (id,entity_id,type,coord,hilbert_index,trajectory,n_constituents)
                SELECT public.laplace_hash128_blake3(x.id || decode('0100','hex')),x.id,1,
                       public.ST_MakePoint(x.x,x.y,x.z,x.m),decode(repeat('00',16),'hex'),
                       NULL,0
                FROM (VALUES
                    (a0,0.271828,0.314159,0.161803,0.141421),
                    (a1,0.272828,0.315159,0.162803,0.142421),
                    (b0,0.271829,0.314160,0.161804,0.141422),
                    (b1,0.272829,0.315160,0.162804,0.142422)
                ) x(id,x,y,z,m);
                INSERT INTO laplace.physicalities
                    (id,entity_id,type,coord,hilbert_index,trajectory,n_constituents)
                VALUES
                    (public.laplace_hash128_blake3(anchor || decode('0100','hex')),anchor,1,
                     public.ST_MakePoint(0.272328,0.314659,0.162303,0.141921),
                     decode(repeat('00',16),'hex'),
                     public.ST_MakeLine(ARRAY[public.laplace_mantissa_pack(a0,1,1,0),
                                             public.laplace_mantissa_pack(a1,2,1,0)]),2),
                    (public.laplace_hash128_blake3(peer || decode('0100','hex')),peer,1,
                     public.ST_MakePoint(0.272329,0.314660,0.162304,0.141922),
                     decode(repeat('00',16),'hex'),
                     public.ST_MakeLine(ARRAY[public.laplace_mantissa_pack(b0,1,1,0),
                                             public.laplace_mantissa_pack(b1,2,1,0)]),2);
                -- Give both shapes the same witnessed lower surface. This
                -- exercises the live non-NULL batched case gate as well as shape.
                INSERT INTO laplace.consensus
                    (id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
                SELECT public.laplace_hash128_blake3(child || relation || target),
                       child,relation,target,2500000000000,30000000000,60000000,1,now()
                FROM (VALUES
                    (a0,laplace.word_id('a')),(b0,laplace.word_id('a')),
                    (a1,laplace.word_id('b')),(b1,laplace.word_id('b'))
                ) maps(child,target)
                CROSS JOIN (SELECT laplace.relation_type_id('HAS_LOWERCASE_MAPPING') AS relation) r;
                IF (SELECT count(*) FROM lexical.word_case_classes_batch(ARRAY[anchor,peer])
                    WHERE case_class='ab') <> 2 THEN
                    RAISE EXCEPTION 'fixture must have equal witnessed lower surfaces';
                END IF;
                SELECT count(*) INTO physical_before FROM laplace.physicalities
                    WHERE entity_id=ANY(ARRAY[anchor,peer,a0,a1,b0,b1]);
                before_ids := lexical.word_shape_peers_fast(anchor,0.001);
                IF NOT COALESCE(peer=ANY(before_ids),false) THEN
                    RAISE EXCEPTION 'fixture must initially return the shared-type peer';
                END IF;
                PERFORM laplace.entity_interpretations_publish(
                    ARRAY[peer],ARRAY[1::smallint],ARRAY[smaller],
                    ARRAY[decode(repeat('00',16),'hex')],ARRAY[true]);
                IF (SELECT type_id FROM laplace.entities WHERE id=peer) <> smaller THEN
                    RAISE EXCEPTION 'fixture did not change the compatibility summary';
                END IF;
                after_ids := lexical.word_shape_peers_fast(anchor,0.001);
                IF NOT COALESCE(peer=ANY(after_ids),false)
                   OR (SELECT count(*) FROM unnest(after_ids) x WHERE x=peer) <> 1 THEN
                    RAISE EXCEPTION 'shared-type peer disappeared or multiplied after unrelated facet';
                END IF;
                IF (SELECT count(*) FROM laplace.physicalities
                    WHERE entity_id=ANY(ARRAY[anchor,peer,a0,a1,b0,b1])) <> physical_before THEN
                    RAISE EXCEPTION 'facet change altered physicality cardinality';
                END IF;
            END $fixture$;
            """;
        await command.ExecuteNonQueryAsync();
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task WalkExcludesRelationMetadataThroughEveryRecordedTypeMembership()
    {
        string scope = "native-interpretation-walk/" + Guid.NewGuid().ToString("N");
        await using var connection = await pg.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 30;
        command.CommandText = $$"""
            DO $fixture$
            DECLARE
                anchor bytea := public.laplace_hash128_blake3('{{scope}}/anchor');
                ordinary bytea := public.laplace_hash128_blake3('{{scope}}/ordinary');
                metadata bytea := public.laplace_hash128_blake3('{{scope}}/metadata');
                regular_type bytea := public.laplace_hash128_blake3('{{scope}}/regular-type');
                relation_type bytea := laplace.entity_type_id('RelationType');
                edge_type bytea := laplace.relation_type_id('COMPLETES_TO');
                smaller bytea := decode(repeat('00',16),'hex');
                before_ids bytea[];
                after_ids bytea[];
            BEGIN
                INSERT INTO laplace.entities(id,tier,type_id) VALUES
                    (anchor,3,regular_type),(ordinary,3,regular_type),(metadata,3,relation_type);
                INSERT INTO laplace.consensus
                    (id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
                SELECT public.laplace_hash128_blake3(anchor || edge_type || obj),anchor,edge_type,obj,
                       1800000000000,50000000000,60000000,5,now()
                FROM unnest(ARRAY[ordinary,metadata]) AS target(obj);
                -- Check the actual native path before and after the smaller facet.
                SELECT array_agg(w.entity_id ORDER BY w.entity_id) INTO before_ids
                FROM consensus.walk_branches(anchor,edge_type,1,8) w;
                IF before_ids IS DISTINCT FROM ARRAY[ordinary] THEN
                    RAISE EXCEPTION 'fixture must walk only the ordinary neighbour initially';
                END IF;
                PERFORM laplace.entity_interpretations_publish(
                    ARRAY[metadata],ARRAY[1::smallint],ARRAY[smaller],
                    ARRAY[decode(repeat('00',16),'hex')],ARRAY[true]);
                IF (SELECT type_id FROM laplace.entities WHERE id=metadata) <> smaller THEN
                    RAISE EXCEPTION 'fixture did not replace the metadata summary type';
                END IF;
                SELECT array_agg(w.entity_id ORDER BY w.entity_id) INTO after_ids
                FROM consensus.walk_branches(anchor,edge_type,1,8) w;
                IF after_ids IS DISTINCT FROM before_ids THEN
                    RAISE EXCEPTION 'unrelated facet admitted a RelationType metadata neighbour';
                END IF;
                SELECT array_agg(w.entity_id ORDER BY w.entity_id) INTO after_ids
                FROM consensus.walk_branches(anchor,edge_type,1,8,NULL,NULL,false,true) w;
                IF after_ids IS DISTINCT FROM before_ids THEN
                    RAISE EXCEPTION 'geometry-enabled walk lost the same membership exclusion';
                END IF;
                IF (SELECT count(*) FROM laplace.consensus WHERE subject_id=anchor AND type_id=edge_type) <> 2
                   OR (SELECT count(*) FROM laplace.entities WHERE id=metadata) <> 1 THEN
                    RAISE EXCEPTION 'membership filtering changed durable evidence or canonical E count';
                END IF;
            END $fixture$;
            """;
        await command.ExecuteNonQueryAsync();
        await transaction.RollbackAsync();
    }
    [Fact]
    public async Task BatchedCaseSurfacesPreserveWitnessSelectionRunsNullsAndVariantParity()
    {
        string scope = "native-case-surfaces/" + Guid.NewGuid().ToString("N");
        await using var connection = await pg.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 30;
        command.CommandText = $$"""
            DO $fixture$
            DECLARE
                word bytea := public.laplace_hash128_blake3('{{scope}}/word');
                lower_word bytea := laplace.word_id('aa!');
                fallback_word bytea := public.laplace_hash128_blake3('{{scope}}/fallback-word');
                grapheme bytea := public.laplace_hash128_blake3('{{scope}}/grapheme');
                unmapped bytea := public.laplace_hash128_blake3('{{scope}}/unmapped');
                absent bytea := public.laplace_hash128_blake3('{{scope}}/absent');
                kind bytea := public.laplace_hash128_blake3('{{scope}}/kind');
                lower_type bytea := laplace.relation_type_id('HAS_LOWERCASE_MAPPING');
                a bytea := laplace.word_id('a');
                z bytea := laplace.word_id('z');
                bang bytea := laplace.word_id('!');
                upper_q bytea := laplace.word_id('Q');
                lower_surface text;
                scalar_ids bytea[];
                batch_ids bytea[];
            BEGIN
                INSERT INTO laplace.entities(id,tier,type_id) VALUES
                    (word,3,kind),(lower_word,3,kind),(fallback_word,3,kind),
                    (grapheme,2,kind),(unmapped,2,kind);
                INSERT INTO laplace.physicalities
                    (id,entity_id,type,coord,hilbert_index,trajectory,n_constituents)
                SELECT public.laplace_hash128_blake3(x.id || decode('0100','hex')),
                       x.id,1,public.ST_MakePoint(0.1,0.2,0.3,0.4),
                       decode(repeat('00',16),'hex'),x.trajectory,x.n
                FROM (VALUES
                    (grapheme,public.ST_MakeLine(ARRAY[
                        public.laplace_mantissa_pack(upper_q,1,1,0),
                        public.laplace_mantissa_pack(upper_q,2,1,0)]),2),
                    (unmapped,public.ST_MakeLine(ARRAY[
                        public.laplace_mantissa_pack(upper_q,1,1,0),
                        public.laplace_mantissa_pack(upper_q,2,1,0)]),2),
                    (word,public.ST_MakeLine(ARRAY[
                        public.laplace_mantissa_pack(grapheme,1,2,0),
                        public.laplace_mantissa_pack(bang,3,1,0)]),3),
                    (lower_word,public.ST_MakeLine(ARRAY[
                        public.laplace_mantissa_pack(a,1,2,0),
                        public.laplace_mantissa_pack(bang,3,1,0)]),3),
                    (fallback_word,public.ST_MakeLine(ARRAY[
                        public.laplace_mantissa_pack(unmapped,1,1,0),
                        public.laplace_mantissa_pack(unmapped,2,1,0)]),2)
                ) x(id,trajectory,n)
                ON CONFLICT(id) DO NOTHING;
                INSERT INTO laplace.consensus
                    (id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
                VALUES
                    (public.laplace_hash128_blake3(grapheme || lower_type || z),
                     grapheme,lower_type,z,1600000000000,30000000000,60000000,1,now()),
                    (public.laplace_hash128_blake3(grapheme || lower_type || a),
                     grapheme,lower_type,a,2500000000000,30000000000,60000000,1,now());
                IF (SELECT count(*) FROM lexical.word_case_classes_batch(
                        ARRAY[word,lower_word,fallback_word,absent,word])) <> 4 THEN
                    RAISE EXCEPTION 'case batch must retain exactly one result per input identity';
                END IF;
                SELECT case_class INTO lower_surface
                FROM lexical.word_case_classes_batch(ARRAY[word]);
                IF lower_surface IS DISTINCT FROM 'aa!' THEN
                    RAISE EXCEPTION 'winning witnessed mapping or run expansion changed: %',lower_surface;
                END IF;
                IF (SELECT case_class FROM lexical.word_case_classes_batch(ARRAY[lower_word]))
                    IS DISTINCT FROM lower_surface
                   OR (SELECT case_class FROM lexical.word_case_classes_batch(ARRAY[fallback_word]))
                    IS DISTINCT FROM 'QQQQ'
                   OR (SELECT case_class FROM lexical.word_case_classes_batch(ARRAY[absent]))
                    IS NOT NULL THEN
                    RAISE EXCEPTION 'lower equality, unmapped render fallback or absent NULL changed';
                END IF;
                IF EXISTS (SELECT FROM lexical.word_case_classes_batch(ARRAY[]::bytea[])) THEN
                    RAISE EXCEPTION 'empty input must have no case rows';
                END IF;
                scalar_ids := lexical.word_case_variants(word);
                SELECT array_agg(variant_id ORDER BY variant_id) INTO batch_ids
                FROM lexical.word_case_variants_batch(ARRAY[word,word]);
                IF NOT COALESCE(lower_word=ANY(scalar_ids),false)
                   OR batch_ids IS DISTINCT FROM scalar_ids THEN
                    RAISE EXCEPTION 'existing scalar and batched case variants diverged';
                END IF;
            END $fixture$;
            """;
        await command.ExecuteNonQueryAsync();
        await transaction.RollbackAsync();
    }

}
