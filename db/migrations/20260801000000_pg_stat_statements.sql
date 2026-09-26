-- Per-statement execution and planning time. The library is loaded through
-- shared_preload_libraries (CpuTopologyCommands.EmitPgTuning) next to laplace_substrate,
-- whose perfcache blobs are mapped at preload; loading it requires a postmaster restart.
-- The extension can be created before that restart; the view then returns no rows.
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;
