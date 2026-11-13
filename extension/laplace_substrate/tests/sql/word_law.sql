BEGIN;

SELECT realize.render(public.blake3_send(public.laplace_text_id('A'))) = 'A'
    AS render_t0_via_perfcache;



-- One standing algebra: eff_mu is Laplace-Native's conservative confidence.
SELECT bool_and(consensus.eff_mu(r, d) = public.laplace_confidence(r, d, 2)) AS eff_mu_matches_engine
FROM (VALUES (1500.0::float8, 350.0::float8), (1600, 80), (2010.5, 12), (0, 0), (-5, 7)) v(r, d);

COMMIT;
