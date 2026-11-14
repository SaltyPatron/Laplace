BEGIN;

SELECT count(*) = 0 AS no_identity_violations FROM laplace.identity_law_violations();
SELECT ok AS substrate_healthy FROM laplace.substrate_health();

COMMIT;
