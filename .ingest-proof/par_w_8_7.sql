SET synchronous_commit=off;
INSERT INTO laplace.bench_par (id,entity_id,source_id,type,coord,hilbert_index,n_constituents,observed_at) SELECT id,entity_id,source_id,type,coord,hilbert_index,n_constituents,observed_at FROM laplace.bench_par_src WHERE rn % 8 = 7 ON CONFLICT DO NOTHING;
