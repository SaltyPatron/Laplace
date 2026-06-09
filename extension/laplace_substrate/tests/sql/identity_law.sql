BEGIN;
SET search_path = laplace, public;

SELECT fake_tier_band_count() = 0 AS no_fake_tiers;
SELECT count(*) = 0 AS no_identity_violations FROM identity_law_violations();
SELECT ok AS substrate_healthy FROM substrate_health();

COMMIT;
