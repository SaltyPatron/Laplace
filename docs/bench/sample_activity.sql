-- Sample live backend activity 5× over ~8s. Shows the real SQL + busy/idle + waits.
\pset pager off
\timing off
\echo '--- sample 1 ---'
SELECT pid, state, coalesce(wait_event_type||':'||wait_event,'-') AS wait, left(query,95) AS q
FROM pg_stat_activity WHERE datname='laplace' AND pid<>pg_backend_pid() AND state<>'idle' ORDER BY pid;
SELECT pg_sleep(2);
\echo '--- sample 2 ---'
SELECT pid, state, coalesce(wait_event_type||':'||wait_event,'-') AS wait, left(query,95) AS q
FROM pg_stat_activity WHERE datname='laplace' AND pid<>pg_backend_pid() AND state<>'idle' ORDER BY pid;
SELECT pg_sleep(2);
\echo '--- sample 3 ---'
SELECT pid, state, coalesce(wait_event_type||':'||wait_event,'-') AS wait, left(query,95) AS q
FROM pg_stat_activity WHERE datname='laplace' AND pid<>pg_backend_pid() AND state<>'idle' ORDER BY pid;
SELECT pg_sleep(2);
\echo '--- sample 4 ---'
SELECT pid, state, coalesce(wait_event_type||':'||wait_event,'-') AS wait, left(query,95) AS q
FROM pg_stat_activity WHERE datname='laplace' AND pid<>pg_backend_pid() AND state<>'idle' ORDER BY pid;
SELECT pg_sleep(2);
\echo '--- sample 5 (counts) ---'
SELECT count(*) FILTER (WHERE state='active') AS active, count(*) FILTER (WHERE state='idle') AS idle,
       count(*) AS total FROM pg_stat_activity WHERE datname='laplace' AND pid<>pg_backend_pid();
