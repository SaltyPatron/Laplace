from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding='utf-8')
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'{path}: expected one replacement, found {count}: {old[:120]!r}')
    p.write_text(text.replace(old, new, 1), encoding='utf-8')


replace_once(
    'app/Laplace.Substrate/Crud/Npgsql/NpgsqlIngestOps.cs',
    '''            ANALYZE laplace.attestations;
            ANALYZE laplace.consensus;
            ANALYZE laplace.entities;
            ANALYZE laplace.physicalities (entity_id, type)
''',
    '''            ANALYZE laplace.attestations;
            ANALYZE laplace.consensus;
            ANALYZE laplace.entities;
            ANALYZE laplace.entity_interpretations;
            ANALYZE laplace.physicalities (entity_id, type)
''')

replace_once(
    'app/Laplace.Substrate/Crud/Npgsql/NpgsqlIngestOps.cs',
    '''            ANALYZE laplace.attestations (subject_id, source_id, type_id, object_id);
            ANALYZE laplace.physicalities (entity_id, type);
            ANALYZE laplace.entities (id, tier, type_id);
            ANALYZE laplace.consensus (subject_id, type_id, object_id, rating, rd)
''',
    '''            ANALYZE laplace.attestations (subject_id, source_id, type_id, object_id);
            ANALYZE laplace.physicalities (entity_id, type);
            ANALYZE laplace.entities (id);
            ANALYZE laplace.entity_interpretations (entity_id, tier, type_id, first_observed_by);
            ANALYZE laplace.consensus (subject_id, type_id, object_id, rating, rd)
''')

replace_once(
    'app/Laplace.Substrate/Crud/Npgsql/CopyTupleParser.cs',
    '''        /// <summary>Partition key (LIST(tier), t2 further HASH(id)) — the
        /// keyed presence probe needs it because id alone cannot prune.</summary>
        public readonly List<short> Tiers = new();
        /// <summary>type_id — secondary-index contention key for parallel COPY.</summary>
''',
    '''        /// <summary>Observed structural tier. Canonical storage is HASH(id);
        /// tier is retained for interpretation publication and deterministic
        /// compatibility-row selection, never as an entity presence key.</summary>
        public readonly List<short> Tiers = new();
        /// <summary>Observed type interpretation and deterministic compatibility
        /// representative key for canonical entity COPY.</summary>
''')

replace_once(
    'extension/laplace_substrate/sql/bootstrap/bootstrap.sql.in',
    '''    -- Self-referential FKs dropped with the partitioned schema: a partitioned
    -- entities has no unique(id) to reference (PK is (id, tier)), and per-row
    -- FK validation on bulk COPY was pure cost. Referential integrity is
    -- structural — ids are content hashes minted by the same law that mints
    -- the referenced rows.
''',
    '''    -- Per-row self-FKs stay out of the hot COPY path. Canonical entities now
    -- have one id-only primary key, while tier/type/source observations live in
    -- entity_interpretations. Referential closure is proved set-wise by the
    -- admission/apply/proof owners rather than by one RBAR FK check per tuple.
''')

replace_once(
    'app/Laplace.Substrate/Crud/Npgsql/NpgsqlWorkingSetApply.cs',
    '''            // that keyspace is absent — the bitmap probe would return an
            // all-zero mask after paying full chunk round-trips. Entity tier
            // emptiness is resolved by the bounded smaller-side verifier below;
            // a separate EXISTS roster caused PostgreSQL to aggregate every
            // entity partition once per apply on large targets.
''',
    '''            // that keyspace is absent — the bitmap probe would return an
            // all-zero mask after paying full chunk round-trips. Canonical entity
            // presence is id-only/HASH(id), so it goes directly through the
            // content-id bitmap rather than a tier census or inversion.
''')

replace_once(
    'app/Laplace.Substrate/Crud/Npgsql/NpgsqlWorkingSetApply.cs',
    '''            // I/O locality — the load-bearing fix for large-DB probes. The native existence
            // bitmaps do keyed lookups into the PARTITIONED tables (entities LIST(tier),
            // physicalities HASH(id), attestations LIST(type_id)->HASH(subject)). Probing
''',
    '''            // I/O locality — the load-bearing fix for large-DB probes. The native existence
            // bitmaps read partitioned storage (entities HASH(id), physicalities HASH(id),
            // attestations LIST(type_id)->HASH(subject)). Probing
''')

replace_once(
    'app/Laplace.Substrate/Crud/Npgsql/NpgsqlWorkingSetApply.cs',
    '                + "(skipped {ECache:N0}e/{PCache:N0}p cached, {T0:N0}e tier0-gate, {PEmpty:N0}p/{AEmpty:N0}a empty-relation, {EInv:N0}e smaller-side invert, {AStruct:N0}a novel-by-construction; "\n',
    '                + "(skipped {ECache:N0}e/{PCache:N0}p cached, {T0:N0}e tier0-gate, {PEmpty:N0}p/{AEmpty:N0}a empty-relation, {EInv:N0}e retired-tier-invert, {AStruct:N0}a novel-by-construction; "\n')

replace_once(
    'app/Laplace.Substrate/Crud/Npgsql/NpgsqlWorkingSetApply.cs',
    '''            // Entities: first occurrence of each id, minus stored rows.
            // Kept rows carry their id so parallel COPY groups can own
            // DISJOINT btree key ranges — content-addressed ids are
            // uniformly random, and un-partitioned parallel inserts
            // measured as LWLock:BufferContent pile-ups on shared index
            // pages. Range-partitioned sorted groups fill leaves like a
            // parallel bulk index build instead.
''',
    '''            // Entities: one deterministic compatibility representative per
            // canonical id, minus stored rows. Interpretations were published
            // separately above. Kept rows carry content ids so parallel COPY
            // groups stay uniform over HASH(id), while sorted ids walk each
            // bucket's PK leaves forward.
''')
