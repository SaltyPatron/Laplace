BEGIN;
INSERT INTO laplace.consensus(id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at)
SELECT laplace.consensus_id(laplace.word_id('b'),laplace.word_id('b'),public.laplace_hash128_blake3('test/readpath/inbound-target')),laplace.word_id('b'),laplace.word_id('b'),public.laplace_hash128_blake3('test/readpath/inbound-target'),1700000000000,30000000000,60000000,1,now();
PREPARE readpath_in(bytea,int4) AS WITH edges AS MATERIALIZED (SELECT c.*,row_number() OVER (ORDER BY consensus.edge_rank(c.type_id,consensus.eff_mu(c.rating,c.rd)) DESC,c.type_id,c.subject_id) AS ord FROM consensus.consensus_in($1,$2) c), ids AS (SELECT type_id AS id FROM edges UNION SELECT subject_id FROM edges), labels AS MATERIALIZED (SELECT d.id,d.label FROM realize.display_label_batch(ARRAY(SELECT id FROM ids ORDER BY id)) d) SELECT encode(e.subject_id,'hex'),t.label,s.label,consensus.eff_mu_display(e.rating,e.rd),e.witness_count FROM edges e JOIN labels t ON t.id=e.type_id JOIN labels s ON s.id=e.subject_id ORDER BY e.ord;
CREATE TEMP TABLE inbound(subject_hex,relation_label,subject_label,eff_mu,witnesses) AS EXECUTE readpath_in(public.laplace_hash128_blake3('test/readpath/inbound-target'),40);
CREATE TEMP TABLE outbound AS SELECT * FROM ops.consensus_out_labeled(laplace.word_id('b'),40);
DO $check$ BEGIN
IF (SELECT count(*) FROM inbound)<>1 OR NOT EXISTS(SELECT FROM inbound WHERE subject_hex=encode(laplace.word_id('b'),'hex') AND relation_label='b' AND subject_label='b') THEN RAISE EXCEPTION 'inbound catalog read lost a cache-resident subject or relation'; END IF;
IF (SELECT count(*) FROM outbound)<>1 OR NOT EXISTS(SELECT FROM outbound WHERE type_label='b' AND object_label='Entity · '||left(encode(public.laplace_hash128_blake3('test/readpath/inbound-target'),'hex'),12)) THEN RAISE EXCEPTION 'outbound read lost unresolved identity or custom relation'; END IF;
IF EXISTS(SELECT FROM ops.consensus_out_labeled(laplace.word_id('b'),0)) THEN RAISE EXCEPTION 'zero limit expanded edges'; END IF;
END $check$;
TABLE inbound;
TABLE outbound;
ROLLBACK;
