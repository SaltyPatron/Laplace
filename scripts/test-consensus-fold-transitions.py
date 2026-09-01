#!/usr/bin/env python3
"""Compile and execute native fold parity/mutation tests in an explicit scratch PG18.

Generated modules derive solely from src/fold_route.c, compiled with the same
strict floating-point flags as the engine. Never installs modules or changes a
live database. Requires psycopg2, a C compiler and a scratch extension template.
"""
import argparse
from pathlib import Path
import subprocess
import tempfile
import uuid

import psycopg2
from psycopg2 import sql

ROOT = Path(__file__).resolve().parents[1]
SIGNATURES = (
    'consensus.upsert_type(bytea,bytea[],bytea[],bigint[],bigint[],bigint[],timestamptz[],bigint[],bigint[],bigint[],bigint[],bigint[],bigint[])',
    'consensus.upsert(bytea[],bytea[],bytea[],bigint[],bigint[],bigint[],timestamptz[],bigint[],bigint[],bigint[],bigint[],bigint[],bigint[])')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--socket', type=Path, required=True)
    parser.add_argument('--data', type=Path, required=True)
    parser.add_argument('--user', required=True)
    parser.add_argument('--template', required=True)
    parser.add_argument('--pg-prefix', type=Path, default=Path('/opt/laplace/pgsql-18'))
    parser.add_argument('--core-lib', type=Path, default=Path('/opt/laplace/lib'))
    parser.add_argument('--mutations', action='store_true')
    args = parser.parse_args()
    if not args.socket.is_absolute() or not args.data.resolve().is_relative_to(Path('/tmp')):
        raise SystemExit('requires an explicit /tmp scratch cluster, never TCP')
    connargs = dict(host=str(args.socket), user=args.user)
    admin = psycopg2.connect(**connargs, dbname='postgres')
    admin.autocommit = True
    cur = admin.cursor()
    cur.execute("SELECT current_setting('data_directory'),current_setting('listen_addresses')")
    data, listeners = cur.fetchone()
    if Path(data).resolve() != args.data.resolve() or listeners:
        raise SystemExit('scratch cluster identity mismatch')
    name = 'etl_contract_' + uuid.uuid4().hex
    cur.execute(sql.SQL('CREATE DATABASE {} TEMPLATE {}').format(sql.Identifier(name), sql.Identifier(args.template)))
    db = None
    try:
        db = psycopg2.connect(**connargs, dbname=name)
        db.autocommit = True
        q = db.cursor()
        definitions = []
        for signature in SIGNATURES:
            q.execute('SELECT pg_get_functiondef(%s::regprocedure)', (signature,))
            definition = q.fetchone()[0]
            if "AS 'laplace_substrate'" not in definition:
                raise RuntimeError('template must use the unmodified extension functions')
            definitions.append(definition)
        source_dir = ROOT/'extension/laplace_substrate/src'
        source = (source_dir/'fold_route.c').read_text()
        checks = (ROOT/'extension/laplace_substrate/tests/sql/consensus_upsert.sql').read_text()
        variants = [('correct', source, False)]
        if args.mutations:
            for field, value in enumerate(('st.rating','st.rd','st.volatility','opp','phi','n_games','sum')):
                before = f'input[{field}] = {value};'
                if source.count(before) != 1:
                    raise RuntimeError('mutation no longer applies exactly once')
                variants.append((f'omit-{field}', source.replace(before, f'input[{field}] = 0;'), True))
            variants.append(('restored', source, False))
        with tempfile.TemporaryDirectory(prefix='laplace-fold-mutations-') as temporary:
            for label, content, should_fail in variants:
                module = Path(temporary)/f'{label}.so'
                generated = '#include "postgres.h"\n#include "fmgr.h"\nPG_MODULE_MAGIC;\n' + content
                subprocess.run(['cc','-shared','-fPIC','-O3','-fno-fast-math','-ffp-contract=off',
                    '-I'+str(args.pg_prefix/'include/server'), '-I'+str(ROOT/'engine/core/include'),
                    '-I'+str(source_dir), '-L'+str(args.core_lib), '-Wl,-rpath,'+str(args.core_lib),
                    '-x','c','-','-llaplace_core','-o',str(module)], input=generated, text=True, check=True)
                quoted = q.mogrify('%s', (str(module),)).decode()
                for definition in definitions:
                    q.execute(definition.replace("AS 'laplace_substrate'", 'AS '+quoted))
                try:
                    q.execute(checks)
                except psycopg2.Error as error:
                    q.execute('ROLLBACK')
                    if not should_fail or error.pgcode != 'P0001' or 'batch/scalar mismatch' not in str(error):
                        raise
                else:
                    if should_fail:
                        raise AssertionError(f'{label}: defective key escaped the parity test')
                print(f'PASS {label}: '+('defect detected' if should_fail else 'exact scalar parity'), flush=True)
                if not should_fail:
                    output = Path(temporary)/('regress-'+label)
                    output.mkdir()
                    subprocess.run([str(args.pg_prefix/'lib/pgxs/src/test/regress/pg_regress'),
                        '--bindir='+str(args.pg_prefix/'bin'), '--host='+str(args.socket), '--user='+args.user,
                        '--dbname='+name, '--use-existing',
                        '--inputdir='+str(ROOT/'extension/laplace_substrate/tests'),
                        '--outputdir='+str(output), 'consensus_upsert'], check=True)
    finally:
        if db is not None:
            db.close()
        cur.execute(sql.SQL('DROP DATABASE {} WITH (FORCE)').format(sql.Identifier(name)))
        admin.close()


if __name__ == '__main__':
    main()
