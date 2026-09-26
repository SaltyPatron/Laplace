-- A leaf swapped in under a partitioned table is where routing sends its keys: the
-- parent's partitions changing invalidates every backend's resolved leaves.
BEGIN;
SELECT consensus.partition_leaf('\x00000000000000000000000000000000'::bytea,
    '\x0123456789abcdef0123456789abcdef'::bytea)::regclass AS leaf \gset
SELECT pg_get_expr(relpartbound, oid) AS bound FROM pg_class WHERE oid = :'leaf'::regclass \gset
CREATE TABLE laplace.regress_swap_leaf (LIKE laplace.consensus INCLUDING DEFAULTS INCLUDING GENERATED INCLUDING CONSTRAINTS);
ALTER TABLE laplace.consensus DETACH PARTITION :leaf;
ALTER TABLE laplace.consensus ATTACH PARTITION laplace.regress_swap_leaf :bound;
SELECT consensus.partition_leaf('\x00000000000000000000000000000000'::bytea,
    '\x0123456789abcdef0123456789abcdef'::bytea) = 'laplace.regress_swap_leaf'::regclass AS routes_to_swapped_leaf;
ROLLBACK;
