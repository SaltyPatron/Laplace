#!/usr/bin/env python3
"""Compare actual native consensus fold persistence with binary COPY in a scratch PG18.

Diagnostic only: COPY receives already-folded rows, so this is an upper bound on
the benefit of changing persistence, NOT a replacement fold or throughput claim.
Requires psycopg2. Creates a uniquely named database from a scratch template and
drops only that database. Refuses TCP and any server outside the supplied /tmp
scratch directory. All nine production indexes remain present.
"""
import argparse
import io
import json
import os
from pathlib import Path
import time
import uuid

import psycopg2
from psycopg2 import sql


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--socket', type=Path, required=True)
    parser.add_argument('--data', type=Path, required=True)
    parser.add_argument('--user', required=True)
    parser.add_argument('--template', required=True)
    parser.add_argument('--rows', type=int, default=30000)
    parser.add_argument('--samples', type=int, default=3)
    parser.add_argument('--candidate-module', type=Path)
    parser.add_argument('--distinct-inputs', action='store_true')
    args = parser.parse_args()
    if not args.data.resolve().is_relative_to(Path('/tmp')) or not args.socket.is_absolute():
        raise SystemExit('only an explicit /tmp scratch cluster is accepted')
    if not 1 <= args.rows <= 100000 or not 1 <= args.samples <= 10:
        raise SystemExit('benchmark safety bound exceeded')
    connargs = dict(host=str(args.socket), user=args.user)
    admin = psycopg2.connect(**connargs, dbname='postgres')
    admin.autocommit = True
    cur = admin.cursor()
    cur.execute("SELECT current_setting('data_directory'),current_setting('listen_addresses')")
    data, listeners = cur.fetchone()
    if Path(data).resolve() != args.data.resolve() or listeners:
        raise SystemExit('scratch cluster identity mismatch or TCP listener enabled')
    name = 'etl_bench_' + uuid.uuid4().hex
    cur.execute(sql.SQL('CREATE DATABASE {} TEMPLATE {}').format(sql.Identifier(name), sql.Identifier(args.template)))
    try:
        with psycopg2.connect(**connargs, dbname=name) as db:
            db.autocommit = True
            q = db.cursor()
            q.execute("SELECT count(*) FROM laplace.consensus")
            if q.fetchone()[0] != 0:
                raise RuntimeError('scratch template consensus must be empty')
            q.execute("SET statement_timeout='60s'")
            q.execute('SET enable_mergejoin=off; SET enable_hashjoin=off')
            q.execute("""CREATE TEMP TABLE batch AS
                WITH inputs AS (
                  SELECT n,decode(md5('bench/s/'||n),'hex') s,
                         decode(md5('bench/o/'||n),'hex') o,
                         laplace.relation_type_id('IS_A') t
                  FROM generate_series(1,%s) n)
                SELECT t, array_agg(s ORDER BY laplace.consensus_id(s,t,o)) ss,
                    array_agg(o ORDER BY laplace.consensus_id(s,t,o)) oo,
                    array_agg(30000000000::bigint + CASE WHEN %s THEN n ELSE 0 END
                        ORDER BY laplace.consensus_id(s,t,o)) phis,
                    array_agg(1::bigint) games,array_agg(900000000::bigint) sums,
                    array_agg('2026-01-01'::timestamptz) ts
                FROM inputs GROUP BY t""", (args.rows,args.distinct_inputs))
            fold = 'SELECT consensus.upsert_type(t,ss,oo,phis,games,sums,ts) FROM batch'
            q.execute("SELECT pg_get_functiondef('consensus.upsert_type(bytea,bytea[],bytea[],bigint[],bigint[],bigint[],timestamptz[],bigint[],bigint[],bigint[],bigint[],bigint[],bigint[])'::regprocedure)")
            original = q.fetchone()[0]
            candidate = None
            if args.candidate_module:
                if not args.candidate_module.resolve().is_relative_to(Path('/tmp')):
                    raise RuntimeError('candidate module must be isolated under /tmp')
                module = q.mogrify('%s', (str(args.candidate_module),)).decode()
                if "AS 'laplace_substrate'" not in original:
                    raise RuntimeError('unexpected original function module')
                candidate = original.replace("AS 'laplace_substrate'", 'AS ' + module)
            columns = 'id,subject_id,type_id,object_id,rating,rd,volatility,witness_count,last_observed_at'
            q.execute(fold)
            raw = io.BytesIO()
            q.copy_expert(f'COPY (SELECT {columns} FROM laplace.consensus ORDER BY id) TO STDOUT (FORMAT BINARY)', raw)
            signature = "SELECT count(*),md5(string_agg(row(c.*)::text,'' ORDER BY id)) FROM laplace.consensus c"
            q.execute(signature)
            expected = q.fetchone()
            q.execute("SELECT count(*) FROM pg_index WHERE indrelid='laplace.consensus'::regclass")
            indexes = q.fetchone()[0]
            if indexes != 9:
                raise RuntimeError(f'expected all nine indexes, found {indexes}')
            results = []
            q.execute("SELECT pg_backend_pid(),version(),current_setting('shared_buffers'),current_setting('fsync'),current_setting('synchronous_commit')")
            backend, version, buffers, fsync, sync = q.fetchone()
            def process_metrics():
                proc = Path('/proc') / str(backend)
                stat = (proc/'stat').read_text().split(') ',1)[1].split()
                status = dict(line.split(':',1) for line in (proc/'status').read_text().splitlines() if ':' in line)
                io_stats = dict(line.split(':',1) for line in (proc/'io').read_text().splitlines())
                return dict(cpu_seconds=(int(stat[11])+int(stat[12]))/os.sysconf('SC_CLK_TCK'),
                    rss_kb=int(status['VmRSS'].split()[0]),peak_rss_kb=int(status['VmHWM'].split()[0]),
                    read_bytes=int(io_stats['read_bytes']),write_bytes=int(io_stats['write_bytes']))
            for sample in range(args.samples):
                modes = ['native', 'copy'] + (['candidate'] if candidate else [])
                if sample % 2:
                    modes.reverse()
                for mode in modes:
                    q.execute(candidate if mode == 'candidate' else original)
                    q.execute('TRUNCATE laplace.consensus')
                    q.execute('SELECT pg_current_wal_insert_lsn()')
                    before = q.fetchone()[0]
                    metrics_before = process_metrics()
                    started = time.monotonic()
                    if mode != 'copy':
                        q.execute(fold)
                        assert q.fetchone()[0] == args.rows
                    else:
                        raw.seek(0)
                        q.copy_expert(f'COPY laplace.consensus ({columns}) FROM STDIN (FORMAT BINARY)', raw)
                    seconds = time.monotonic() - started
                    metrics = process_metrics()
                    for field in ('cpu_seconds','read_bytes','write_bytes'):
                        metrics[field] -= metrics_before[field]
                    q.execute('SELECT pg_wal_lsn_diff(pg_current_wal_insert_lsn(),%s)', (before,))
                    wal = int(q.fetchone()[0])
                    q.execute(signature)
                    assert q.fetchone() == expected, 'persisted fold fields differ'
                    results.append(dict(sample=sample, mode=mode, seconds=round(seconds,6), wal_bytes=wal,
                                        process=metrics, verified_rows=expected[0]))
            print(json.dumps(dict(rows=args.rows,indexes=indexes,samples=args.samples,
                distinct_inputs=args.distinct_inputs,server=version,shared_buffers=buffers,fsync=fsync,synchronous_commit=sync,
                timing='statement dispatch through completion/commit; fixture generation excluded',
                caveat='COPY excludes fold and prior-read work; scratch warm-cache fixture, not corpus throughput',
                results=results), indent=2), flush=True)
        db.close()
    finally:
        cur.execute(sql.SQL('DROP DATABASE {} WITH (FORCE)').format(sql.Identifier(name)))
        admin.close()


if __name__ == '__main__':
    main()
