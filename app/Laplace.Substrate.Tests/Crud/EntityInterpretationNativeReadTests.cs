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
                SELECT count(*) INTO physical_before FROM laplace.physicalities
                    WHERE entity_id=ANY(ARRAY[anchor,peer,a0,a1,b0,b1]);
                before_ids := lexical.word_shape_peers_fast(anchor,0.001);
                IF NOT COALESCE(peer=ANY(before_ids),false) THEN
                    RAISE EXCEPTION 'fixture must initially return the shared-type peer';
                END IF;
                INSERT INTO laplace.entities(id,tier,type_id) VALUES (peer,1,smaller);
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
                INSERT INTO laplace.entities(id,tier,type_id) VALUES (metadata,1,smaller);
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
}
