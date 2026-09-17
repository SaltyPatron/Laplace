from pathlib import Path

path = Path('app/Laplace.Substrate/Crud/Npgsql/NpgsqlWorkingSetApply.cs')
text = path.read_text(encoding='utf-8')


def replace_once(old: str, new: str) -> None:
    global text
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'expected exactly one replacement, found {count}: {old[:120]!r}')
    text = text.replace(old, new, 1)


def replace_between(start: str, end: str, replacement: str) -> None:
    global text
    a = text.find(start)
    if a < 0:
        raise SystemExit(f'start marker missing: {start!r}')
    b = text.find(end, a)
    if b < 0:
        raise SystemExit(f'end marker missing: {end!r}')
    text = text[:a] + replacement + text[b:]

old_distinct = '''    /// <summary>
    /// Return the first row for each staged entity id. Once the tier-0
    /// completion marker is present, tier-0 ids are separated into the exact
    /// known-present set instead of entering a database probe.
    /// </summary>
    internal static List<int> DistinctEntityRowIndices(
        CopyTupleParser.EntityRows ents, bool tier0Gate, out List<Hash128>? tier0Present)
    {
        var ids = CollectionsMarshal.AsSpan(ents.Ids);
        var tiers = CollectionsMarshal.AsSpan(ents.Tiers);
        var first = new List<int>(ids.Length);
        var seen = new HashSet<Hash128>(ids.Length);
        tier0Present = tier0Gate ? new List<Hash128>() : null;

        for (int i = 0; i < ids.Length; i++)
        {
            if (!seen.Add(ids[i])) continue;
            if (tier0Gate && tiers[i] == 0)
            {
                tier0Present!.Add(ids[i]);
                continue;
            }
            first.Add(i);
        }
        return first;
    }
'''
new_distinct = '''    /// <summary>
    /// Return one deterministic canonical row for each staged content id.
    /// Tier/type multiplicity is persisted separately as entity interpretations;
    /// the base entity COPY therefore chooses a compatibility representative by
    /// (tier,type_id) rather than by arrival/batch order. If any interpretation
    /// is tier 0 after the Unicode completion marker, the content id is already
    /// known present and the whole canonical id can skip the database probe.
    /// </summary>
    internal static List<int> DistinctEntityRowIndices(
        CopyTupleParser.EntityRows ents, bool tier0Gate, out List<Hash128>? tier0Present)
    {
        var ids = CollectionsMarshal.AsSpan(ents.Ids);
        var tiers = CollectionsMarshal.AsSpan(ents.Tiers);
        var types = CollectionsMarshal.AsSpan(ents.TypeIds);
        var best = new Dictionary<Hash128, int>(ids.Length);
        HashSet<Hash128>? tier0Ids = tier0Gate ? new HashSet<Hash128>() : null;

        for (int i = 0; i < ids.Length; i++)
        {
            Hash128 id = ids[i];
            if (tier0Gate && tiers[i] == 0) tier0Ids!.Add(id);
            if (!best.TryGetValue(id, out int prior)
                || tiers[i] < tiers[prior]
                || (tiers[i] == tiers[prior]
                    && types[i].CompareToBytewise(types[prior]) < 0))
                best[id] = i;
        }

        var orderedIds = best.Keys.OrderBy(id => id, Hash128BytewiseOrder).ToArray();
        var selected = new List<int>(orderedIds.Length);
        tier0Present = tier0Gate ? new List<Hash128>() : null;
        foreach (var id in orderedIds)
        {
            if (tier0Gate && tier0Ids!.Contains(id))
            {
                tier0Present!.Add(id);
                continue;
            }
            selected.Add(best[id]);
        }
        return selected;
    }
'''
replace_once(old_distinct, new_distinct)

replace_once('''        // Materialized only when invert leaves a bitmap remainder.
        List<Hash128> probeEntityIds = new();
        List<short> probeEntityTiers = new();

''', '')

replace_between(
    '            long physEmptySkip = 0, attEmptySkip = 0;\n',
    '            if (_physPresenceComplete)\n',
    '''            long physEmptySkip = 0, attEmptySkip = 0;
            // Canonical entity storage is HASH(id). Tier is an interpretation
            // projection and cannot participate in row presence or partition
            // routing. Build the exact id set that still needs verification;
            // the former LIST(tier) smaller-side inversion would otherwise turn
            // a stored id observed at another tier into a false absence.
            long entInvertResolved = 0; // retained telemetry field; no tier inversion remains
            var probeEntIdsUse = new List<Hash128>(entVerifyIdx.Count);
            if (!_entityPresenceComplete)
            {
                for (int k = 0; k < entVerifyIdx.Count; k++)
                    probeEntIdsUse.Add(ents.Ids[entVerifyIdx[k]]);
            }

''')

replace_between(
    '            if (probeEntIdsUse.Count > 1)\n',
    '            if (probePhysIdsUse.Count > 1)\n',
    '''            if (probeEntIdsUse.Count > 1)
            {
                // HASH(id) owns entity placement. Sort the content hashes so
                // each hash bucket's btree walk is forward; there is no tier key
                // to keep aligned with this permutation anymore.
                var perm = BuildProbePermutation(probeEntIdsUse.Count,
                    (a, b) => probeEntIdsUse[a].CompareToBytewise(probeEntIdsUse[b]));
                probeEntIdsUse = ApplyProbePermutation(probeEntIdsUse, perm);
            }
''')

replace_once('''            var entProbeTask = ProbePresentTieredParallelAsync(
                "laplace.entities_stored_bitmap", probeEntIdsUse, probeEntTiersUse,
                r => Interlocked.Add(ref rtProbe, r), ct);
''', '''            var entProbeTask = ProbePresentCoreAsync(
                "SELECT laplace.entities_stored_bitmap($1)", probeEntIdsUse,
                static (_, _, _) => { },
                r => Interlocked.Add(ref rtProbe, r), ct);
''')

replace_once('''            if (presentFromInvert.Count > 0)
                foreach (var id in presentFromInvert) presentEntities.Add(id);
''', '')

# Keep the old helper temporarily compile-safe for history/measurement references,
# but make its documentation explicit that the production path no longer calls it.
text = text.replace(
    '    /// Under the apply lock: for each LIST(tier) leaf, if committed row count is\n',
    '    /// Historical LIST(tier) inversion retained for measured-reference archaeology;\n    /// the HASH(id) production path above no longer calls it. Formerly, if committed row count was\n',
    1)

path.write_text(text, encoding='utf-8')
