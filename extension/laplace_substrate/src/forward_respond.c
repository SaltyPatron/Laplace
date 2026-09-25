/*
 * converse.respond: the query-relative response of the admitted web to a whole
 * observation (spec 36 COUPLE; INVENTION section 7).
 *
 * Every term is tugged at once. A term reaches its keys through its bindings
 * (KEY_BINDING: dog -> i46360), and the web answers from each key outward as an
 * indexed star, hop by hop (INVENTION section 9: hops and fanout are the compute
 * coordinates). A node records which terms reached it and by what route. A
 * candidate reached from every term, each by its own typed route, is the joint
 * interpretation the observation supports: "capital" and "France" meet at the key
 * that IS_INSTANCE_OF national capital and that France HAS_PART, and which senses of
 * "capital" and "France" were meant falls out of that meeting. No sense is elected
 * first.
 *
 * A route stays typed: the term, the key it left from, its hop count, the relation of
 * its last step and the node it came through, plus the weakest salience and the weakest
 * conservative standing along the path, and the last step's rating, deviation,
 * volatility and witnesses. Candidates are ordered by how many terms they answer, then
 * by the salience of their least salient route (relation rank is read-time salience: a
 * shared part of speech says less than a shared taxonomic or partitive fact), then by
 * fewest hops, then by the weakest standing. That ordering is this operation's
 * declared contract; routes are never collapsed into one score. The expansion is the
 * native consensus-neighbor scan, one set read per hop, with no SQL per candidate.
 *
 * Which salience bands the star may traverse is ROUTE's operand (min_salience): by
 * default the semantic bands, not lexical glue, scalar values or standards metadata.
 */
#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"

#include "laplace/core/hash128.h"
#include "laplace/core/relation_law.h"
#include "consensus_neighbors.h"
#include "spi_common.h"
#include "laplace/core/firmware_law.h"
#include "cognition_program.h"
#include "consensus_scan.h"
#include "walk_score.h"
#include "spi_nested.h"

PG_FUNCTION_INFO_V1(pg_laplace_forward_respond);
PG_FUNCTION_INFO_V1(pg_laplace_content_terms);
PG_FUNCTION_INFO_V1(pg_laplace_firmware);

#define RESPOND_MAX_TERMS 32
#define RESPOND_MAX_HOPS 4

typedef struct
{
    hash128_t id;          /* hash key */
    uint32 reached;        /* bit per observation term that has reached this node */
    uint32 is_key;         /* bit per term for which this node is a key (hop 0) */
} NodeState;

typedef struct
{
    hash128_t id;
    int32 term;
} RouteKey;

typedef struct
{
    RouteKey rkey;         /* hash key */
    hash128_t origin;      /* the term's key this route left from */
    hash128_t type;        /* relation of the last step */
    hash128_t via;         /* the node the last step came from */
    int64 rating, rd, volatility, witnesses;   /* last step's standing */
    __int128 weakest;      /* weakest conservative standing along the path */
    double salience;       /* least salient relation along the path */
    int hops;
    bool outbound;
    uint8 mode;            /* ROUTE_* taxonomic direction state */
} Route;

/* A path's taxonomic direction. Generalizing the term (ascending IS_A /
 * IS_INSTANCE_OF) is allowed only straight from its key; once a path has
 * ascended it may not descend, because up-then-down reaches siblings (Marseille
 * is a city, as a capital is, and not a capital). Any other salient step
 * commits the path, after which it may only specialize or keep going laterally:
 * capital <- national capital <- Paris, France HAS_PART Paris. */
#define ROUTE_AT_KEY 0
#define ROUTE_ASCENDED 1
#define ROUTE_COMMITTED 2

typedef struct
{
    NodeState *node;
    int64 shared;          /* rows naming the candidate as object, bounded */
    int covered;
    double salience;
    int hops;
    __int128 weakest;
} Ranked;

static SPIPlanPtr set_plan = NULL;

static ArrayType *
relation_set(const char *name)
{
    Datum arg = CStringGetTextDatum(name);
    bool isnull;
    Datum value;
    if (set_plan == NULL)
    {
        Oid types[1] = {TEXTOID};
        set_plan = SPI_prepare_cursor("SELECT consensus.relation_set_ids($1)", 1, types,
                                      CURSOR_OPT_PARALLEL_OK);
        if (set_plan == NULL || SPI_keepplan(set_plan) != 0)
            elog(ERROR, "respond: cannot retain the relation-set read");
    }
    if (SPI_execute_plan(set_plan, &arg, NULL, true, 1) != SPI_OK_SELECT || SPI_processed != 1)
        elog(ERROR, "respond: relation set %s is unavailable", name);
    value = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
    if (isnull)
        return construct_empty_array(BYTEAOID);
    return DatumGetArrayTypePCopy(value);
}

/* A small governed id set, read once per call into a lookup table. */
static HTAB *
id_set(ArrayType *ids, const char *label)
{
    HASHCTL ctl = {0};
    HTAB *set;
    ArrayIterator it = array_create_iterator(ids, 0, NULL);
    Datum value;
    bool isnull, found;
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(hash128_t);
    ctl.hcxt = CurrentMemoryContext;
    set = hash_create(label, 32, &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    while (array_iterate(it, &value, &isnull))
        if (!isnull)
        {
            hash128_t id = datum_to_hash128(value);
            hash_search(set, &id, HASH_ENTER, &found);
        }
    array_free_iterator(it);
    return set;
}

static double
relation_salience(const hash128_t *type)
{
    const laplace_relation_def_t *def = NULL;
    return laplace_relation_lookup(type, &def) == 0 && def != NULL ? def->rank : 0.0;
}

/* Meeting specificity: how many cells name the candidate as their object, read
 * with a bound. A hub every entry is an instance of ("Concept") is shared by
 * everything; an answer such as Paris is shared by few. Ordering only. */
#define RESPOND_SHARING_WINDOW (laplace_firmware_default()->sharing_window)
#define RESPOND_SHARING_CAP (laplace_firmware_default()->sharing_cap)

typedef struct
{
    hash128_t id;
    int64 count;
} SharingEntry;

static void
receive_sharing(const LaplaceConsensusRow *row, void *context)
{
    SharingEntry *entry = hash_search((HTAB *) context, &row->object, HASH_FIND, NULL);
    if (entry) entry->count++;
}

static bool
sharing_cutoff(const LaplaceConsensusRow *row, void *context)
{
    SharingEntry *entry = hash_search((HTAB *) context, &row->object, HASH_FIND, NULL);
    return entry && entry->count >= RESPOND_SHARING_CAP;
}

static int
shared_order(const void *left, const void *right)
{
    const Ranked *a = left, *b = right;
    if (a->shared != b->shared) return a->shared < b->shared ? -1 : 1;
    return 0;
}

static int
ranked_order(const void *left, const void *right)
{
    const Ranked *a = left, *b = right;
    if (a->covered != b->covered) return b->covered - a->covered;
    if (a->salience != b->salience) return a->salience > b->salience ? -1 : 1;
    if (a->hops != b->hops) return a->hops - b->hops;
    if (a->weakest != b->weakest) return a->weakest > b->weakest ? -1 : 1;
    return memcmp(&a->node->id, &b->node->id, sizeof(hash128_t));
}

static ArrayType *
id_array(hash128_t *ids, int n)
{
    Datum *d = palloc(sizeof(Datum) * Max(n, 1));
    for (int i = 0; i < n; ++i) d[i] = hash128_to_datum(&ids[i]);
    return construct_array(d, n, BYTEAOID, -1, false, TYPALIGN_INT);
}

Datum
pg_laplace_forward_respond(PG_FUNCTION_ARGS)
{
    const laplace_firmware_image_t *firmware = laplace_firmware_default();
    int hops = PG_ARGISNULL(1) ? firmware->meeting_hops : PG_GETARG_INT32(1);
    int fanout = PG_ARGISNULL(2) ? firmware->meeting_fanout : PG_GETARG_INT32(2);
    int frontier_cap = PG_ARGISNULL(3) ? firmware->meeting_frontier : PG_GETARG_INT32(3);
    int limit = PG_ARGISNULL(4) ? 24 : PG_GETARG_INT32(4);
    double min_salience = PG_ARGISNULL(5) ? firmware->salience_floor : PG_GETARG_FLOAT8(5);
    Datum *term_datums;
    bool *term_nulls;
    int n_in, n_terms = 0, n_frontier = 0;
    hash128_t terms[RESPOND_MAX_TERMS];
    hash128_t *frontier;
    bool spi_top = false;
    HASHCTL ctl = {0};
    HTAB *nodes, *routes, *binding_types, *non_salient_types, *upward_types;
    ArrayType *key_binding;
    ReturnSetInfo *rsinfo;
    uint32 all_terms;

    InitMaterializedSRF(fcinfo, 0);
    rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    if (PG_ARGISNULL(0) || limit <= 0 || fanout <= 0 || frontier_cap <= 0)
        return (Datum) 0;
    if (hops < 1 || hops > RESPOND_MAX_HOPS)
        ereport(ERROR, (errmsg("respond: hops must be between 1 and %d", RESPOND_MAX_HOPS)));
    deconstruct_array(PG_GETARG_ARRAYTYPE_P(0), BYTEAOID, -1, false, TYPALIGN_INT,
                      &term_datums, &term_nulls, &n_in);
    for (int i = 0; i < n_in; ++i)
    {
        hash128_t id;
        bool duplicate = false;
        if (term_nulls[i]) continue;
        id = datum_to_hash128(term_datums[i]);
        for (int j = 0; j < n_terms; ++j) duplicate |= hash128_eq(&terms[j], &id);
        if (duplicate) continue;
        if (n_terms == RESPOND_MAX_TERMS)
            ereport(ERROR, (errmsg("respond: an observation carries at most %d terms", RESPOND_MAX_TERMS)));
        terms[n_terms++] = id;
    }
    if (n_terms == 0)
        return (Datum) 0;
    all_terms = n_terms == 32 ? 0xffffffffu : ((1u << n_terms) - 1);

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "respond: SPI connect failed");
    key_binding = relation_set("KEY_BINDING");
    binding_types = id_set(key_binding, "respond binding types");
    non_salient_types = id_set(relation_set("NON_SALIENT_STRUCTURAL"), "respond non-salient types");
    upward_types = id_set(relation_set("PATH_UPWARD"), "respond taxonomic types");

    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(NodeState);
    ctl.hcxt = CurrentMemoryContext;
    nodes = hash_create("respond nodes", 1024, &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    ctl.keysize = sizeof(RouteKey);
    ctl.entrysize = sizeof(Route);
    routes = hash_create("respond routes", 1024, &ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    frontier = palloc(sizeof(hash128_t) * (frontier_cap + RESPOND_MAX_TERMS * 64 + RESPOND_MAX_TERMS));

    /* Hop 0 (Q -> K): every term is its own key, and reaches the keys its bindings name. */
    {
        int n_bound;
        LaplaceNeighbor *bound;
        for (int t = 0; t < n_terms; ++t)
        {
            bool found;
            NodeState *node = hash_search(nodes, &terms[t], HASH_ENTER, &found);
            if (!found) { node->reached = 0; node->is_key = 0; frontier[n_frontier++] = terms[t]; }
            node->is_key |= 1u << t;
        }
        bound = laplace_consensus_neighbors(id_array(terms, n_terms), key_binding, 64,
                                            false, true, true, &n_bound, NULL);
        for (int r = 0; r < n_bound; ++r)
        {
            bool found;
            NodeState *node;
            int t;
            if (!bound[r].outbound) continue;
            for (t = 0; t < n_terms && !hash128_eq(&terms[t], &bound[r].frontier); ++t) {}
            if (t == n_terms) continue;
            node = hash_search(nodes, &bound[r].neighbor, HASH_ENTER, &found);
            if (!found) { node->reached = 0; node->is_key = 0; frontier[n_frontier++] = bound[r].neighbor; }
            node->is_key |= 1u << t;
        }
    }

    /* Hops 1..H (K -> V): expand the frontier; ownership travels with every step. */
    for (int h = 1; h <= hops && n_frontier > 0; ++h)
    {
        int n_edges, n_next = 0;
        int per = h == 1 ? fanout : Max(8, fanout / 8);
        LaplaceNeighbor *edges = laplace_consensus_neighbors(
            id_array(frontier, n_frontier), NULL, per, false, false, true, &n_edges, NULL);
        hash128_t *next = palloc(sizeof(hash128_t) * (frontier_cap + 1));
        for (int r = 0; r < n_edges; ++r)
        {
            const LaplaceNeighbor *e = &edges[r];
            NodeState *from, *to;
            uint32 owners, fresh;
            bool found;
            if (hash_search(binding_types, &e->type, HASH_FIND, NULL) ||
                hash_search(non_salient_types, &e->type, HASH_FIND, NULL))
                continue;
            /* ROUTE's salience envelope: a governed classification value (a part of
             * speech, a language, a script) is a filter, not a strand. Traversing it
             * would make every noun two hops from every other. */
            if (relation_salience(&e->type) < min_salience)
                continue;
            from = hash_search(nodes, &e->frontier, HASH_FIND, NULL);
            if (from == NULL) continue;
            owners = (from->is_key | from->reached) & all_terms;
            if (owners == 0) continue;
            to = hash_search(nodes, &e->neighbor, HASH_ENTER, &found);
            if (!found) { to->reached = 0; to->is_key = 0; }
            fresh = owners & ~(to->reached | to->is_key);
            if (fresh == 0) continue;
            bool taxonomic = hash_search(upward_types, &e->type, HASH_FIND, NULL) != NULL;
            bool ascending = taxonomic && e->outbound;
            bool descending = taxonomic && !e->outbound;
            uint32 admitted = 0;
            for (int t = 0; t < n_terms; ++t)
            {
                RouteKey rk;
                Route *route, *prev = NULL;
                double salience = relation_salience(&e->type);
                __int128 standing = (__int128) e->rating - 2 * (__int128) e->rd;
                uint8 mode;
                if ((fresh & (1u << t)) == 0) continue;
                memset(&rk, 0, sizeof(rk));
                rk.id = e->frontier;
                rk.term = t;
                if ((from->is_key & (1u << t)) == 0)
                    prev = hash_search(routes, &rk, HASH_FIND, NULL);
                mode = prev ? prev->mode : ROUTE_AT_KEY;
                if (ascending && mode == ROUTE_COMMITTED) continue;
                if (descending && mode == ROUTE_ASCENDED) continue;
                mode = ascending ? ROUTE_ASCENDED : ROUTE_COMMITTED;
                admitted |= 1u << t;
                rk.id = e->neighbor;
                route = hash_search(routes, &rk, HASH_ENTER, &found);
                route->origin = prev ? prev->origin : e->frontier;
                route->type = e->type;
                route->via = e->frontier;
                route->rating = e->rating;
                route->rd = e->rd;
                route->volatility = e->volatility;
                route->witnesses = e->witnesses;
                route->salience = prev && prev->salience < salience ? prev->salience : salience;
                route->weakest = prev && prev->weakest < standing ? prev->weakest : standing;
                route->hops = h;
                route->outbound = e->outbound;
                route->mode = mode;
            }
            if (admitted == 0) continue;
            to->reached |= admitted;
            if (h < hops && n_next < frontier_cap) next[n_next++] = e->neighbor;
        }
        frontier = next;
        n_frontier = n_next;
    }

    /* Meeting pass. Forward expansion is bounded per node, so a hub's members are
     * truncated (a national capital has ~150 instances; Paris need not be among
     * the kept few). A node some terms reached reads its own cells, which are few,
     * and joins every node another term already reached, under the same salience
     * envelope and direction law. France HAS_PART Paris meets
     * capital <- national capital through Paris IS_INSTANCE_OF national capital. */
    {
        long n_nodes = hash_get_num_entries(nodes);
        hash128_t *partial = palloc(sizeof(hash128_t) * Max(Min(n_nodes, (long) frontier_cap), 1));
        int n_partial = 0;
        HASH_SEQ_STATUS seq;
        NodeState *node;
        hash_seq_init(&seq, nodes);
        while ((node = hash_seq_search(&seq)) != NULL)
        {
            uint32 have = node->reached & all_terms;
            if (node->is_key != 0 || have == 0 || have == all_terms) continue;
            if (n_partial < frontier_cap) partial[n_partial++] = node->id;
        }
        if (n_partial > 0 && n_terms > 1)
        {
            int n_edges;
            LaplaceNeighbor *edges = laplace_consensus_neighbors(
                id_array(partial, n_partial), NULL, fanout, false, false, true, &n_edges, NULL);
            for (int r = 0; r < n_edges; ++r)
            {
                const LaplaceNeighbor *e = &edges[r];
                NodeState *x, *y;
                uint32 want;
                if (hash_search(binding_types, &e->type, HASH_FIND, NULL) ||
                    hash_search(non_salient_types, &e->type, HASH_FIND, NULL) ||
                    relation_salience(&e->type) < min_salience)
                    continue;
                x = hash_search(nodes, &e->frontier, HASH_FIND, NULL);
                y = hash_search(nodes, &e->neighbor, HASH_FIND, NULL);
                if (x == NULL || y == NULL) continue;
                want = all_terms & ~x->reached & (y->reached | y->is_key);
                if (want == 0) continue;
                /* The step runs from y to x: its orientation is the edge's, reversed. */
                bool step_outbound = !e->outbound;
                bool taxonomic = hash_search(upward_types, &e->type, HASH_FIND, NULL) != NULL;
                bool ascending = taxonomic && step_outbound;
                bool descending = taxonomic && !step_outbound;
                double salience = relation_salience(&e->type);
                __int128 standing = (__int128) e->rating - 2 * (__int128) e->rd;
                for (int t = 0; t < n_terms; ++t)
                {
                    RouteKey rk;
                    Route *route, *prev = NULL;
                    uint8 mode;
                    int prev_hops = 0;
                    bool found;
                    if ((want & (1u << t)) == 0) continue;
                    memset(&rk, 0, sizeof(rk));
                    rk.id = y->id;
                    rk.term = t;
                    if ((y->is_key & (1u << t)) == 0)
                    {
                        prev = hash_search(routes, &rk, HASH_FIND, NULL);
                        if (prev == NULL) continue;
                        prev_hops = prev->hops;
                    }
                    if (prev_hops + 1 > hops) continue;
                    mode = prev ? prev->mode : ROUTE_AT_KEY;
                    if (ascending && mode == ROUTE_COMMITTED) continue;
                    if (descending && mode == ROUTE_ASCENDED) continue;
                    rk.id = x->id;
                    route = hash_search(routes, &rk, HASH_ENTER, &found);
                    route->origin = prev ? prev->origin : y->id;
                    route->type = e->type;
                    route->via = y->id;
                    route->rating = e->rating;
                    route->rd = e->rd;
                    route->volatility = e->volatility;
                    route->witnesses = e->witnesses;
                    route->salience = prev && prev->salience < salience ? prev->salience : salience;
                    route->weakest = prev && prev->weakest < standing ? prev->weakest : standing;
                    route->hops = prev_hops + 1;
                    route->outbound = step_outbound;
                    route->mode = ascending ? ROUTE_ASCENDED : ROUTE_COMMITTED;
                    x->reached |= 1u << t;
                }
            }
        }
    }

    /* Candidates: nodes answered by enough terms, the terms and their keys excluded. */
    {
        long n = hash_get_num_entries(nodes);
        Ranked *ranked = palloc(sizeof(Ranked) * Max(n, 1));
        HASH_SEQ_STATUS seq;
        NodeState *node;
        long m = 0;
        int needed = n_terms > 1 ? n_terms : 1;
        hash_seq_init(&seq, nodes);
        while ((node = hash_seq_search(&seq)) != NULL)
        {
            Ranked rk = {node, 0, 0, 0.0, 0, 0};
            bool first = true;
            if (node->is_key != 0) continue;
            for (int t = 0; t < n_terms; ++t)
            {
                RouteKey key;
                Route *route;
                if ((node->reached & (1u << t)) == 0) continue;
                memset(&key, 0, sizeof(key));
                key.id = node->id;
                key.term = t;
                route = hash_search(routes, &key, HASH_FIND, NULL);
                if (route == NULL) continue;
                rk.covered++;
                if (first || route->salience < rk.salience) rk.salience = route->salience;
                if (first || route->weakest < rk.weakest) rk.weakest = route->weakest;
                if (route->hops > rk.hops) rk.hops = route->hops;
                first = false;
            }
            /* A multi-term observation asks what answers all of it; fall back to the
             * best partial answers only when nothing answers every term. */
            if (rk.covered > 0) ranked[m++] = rk;
        }
        qsort(ranked, m, sizeof(Ranked), ranked_order);
        /* Within the leading coverage group, the least shared meeting first. */
        if (m > 1)
        {
            int group = 0;
            while (group < m && group < RESPOND_SHARING_WINDOW &&
                   ranked[group].covered == ranked[0].covered)
                ++group;
            if (group > 1)
            {
                HASHCTL sctl = {0};
                HTAB *sharing;
                hash128_t *ids = palloc(sizeof(hash128_t) * group);
                sctl.keysize = sizeof(hash128_t);
                sctl.entrysize = sizeof(SharingEntry);
                sctl.hcxt = CurrentMemoryContext;
                sharing = hash_create("respond sharing", group * 2, &sctl,
                                      HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
                for (int g = 0; g < group; ++g)
                {
                    bool found;
                    SharingEntry *entry = hash_search(sharing, &ranked[g].node->id, HASH_ENTER, &found);
                    entry->count = 0;
                    ids[g] = ranked[g].node->id;
                }
                laplace_consensus_scan_ranked(NULL, id_array(ids, group), NULL, false,
                                              receive_sharing, sharing_cutoff, sharing, NULL);
                for (int g = 0; g < group; ++g)
                {
                    SharingEntry *entry = hash_search(sharing, &ranked[g].node->id, HASH_FIND, NULL);
                    ranked[g].shared = entry ? entry->count : RESPOND_SHARING_CAP;
                }
                /* Stable: insertion sort keeps the salience/hops order within ties. */
                for (int g = 1; g < group; ++g)
                {
                    Ranked held = ranked[g];
                    int j = g - 1;
                    while (j >= 0 && shared_order(&ranked[j], &held) > 0)
                    {
                        ranked[j + 1] = ranked[j];
                        --j;
                    }
                    ranked[j + 1] = held;
                }
                hash_destroy(sharing);
                pfree(ids);
            }
        }
        if (m > 0 && ranked[0].covered < needed && n_terms > 1)
            needed = ranked[0].covered;
        for (long x = 0, emitted = 0; x < m && emitted < limit; ++x)
        {
            if (ranked[x].covered < needed) break;
            ++emitted;
            for (int t = 0; t < n_terms; ++t)
            {
                RouteKey key;
                Route *route;
                Datum values[15];
                bool nulls[15] = {false};
                memset(&key, 0, sizeof(key));
                key.id = ranked[x].node->id;
                key.term = t;
                route = hash_search(routes, &key, HASH_FIND, NULL);
                if (route == NULL) continue;
                values[0] = hash128_to_datum(&ranked[x].node->id);
                values[1] = Int32GetDatum(ranked[x].covered);
                values[2] = Int32GetDatum(t + 1);
                values[3] = hash128_to_datum(&terms[t]);
                values[4] = hash128_to_datum(&route->origin);
                values[5] = Int32GetDatum(route->hops);
                values[6] = hash128_to_datum(&route->type);
                values[7] = hash128_to_datum(&route->via);
                values[8] = BoolGetDatum(route->outbound);
                values[9] = Int64GetDatum(route->rating);
                values[10] = Int64GetDatum(route->rd);
                values[11] = Int64GetDatum(route->volatility);
                values[12] = Int64GetDatum(route->witnesses);
                values[13] = Float8GetDatum(route->salience);
                values[14] = Int64GetDatum((int64) route->weakest);
                tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
            }
        }
    }
    laplace_spi_finish(spi_top);
    return (Datum) 0;
}

/*
 * converse.content_terms(words): the observation's content terms, in order. A word
 * is a term when it binds to a key (HAS_SENSE) and its strongest witnessed
 * universal part of speech, if any, is a content tag. Function words and
 * punctuation are read from their own evidence, never from a word list.
 */
typedef struct
{
    hash128_t id;
    bool has_sense;
    bool has_upos;
    hash128_t upos;
    __int128 upos_standing;
} TermEvidence;

typedef struct
{
    HTAB *evidence;
    hash128_t has_sense, has_pos;
} TermRead;

static void
receive_term_evidence(const LaplaceConsensusRow *row, void *context)
{
    TermRead *read = context;
    TermEvidence *entry = hash_search(read->evidence, &row->subject, HASH_FIND, NULL);
    if (!entry || row->object_is_null || !(laplace_walk_edge_weight(row->rating, row->rd) > 0.0))
        return;
    if (hash128_eq(&row->type, &read->has_sense))
        entry->has_sense = true;
    else if (hash128_eq(&row->type, &read->has_pos))
    {
        __int128 standing = (__int128) row->rating - 2 * (__int128) row->rd;
        if (!entry->has_upos || standing > entry->upos_standing)
        {
            entry->has_upos = true;
            entry->upos = row->object;
            entry->upos_standing = standing;
        }
    }
}

Datum
pg_laplace_content_terms(PG_FUNCTION_ARGS)
{
    Datum *values;
    bool *nulls;
    int n;
    HASHCTL ctl = {0};
    TermRead read;
    hash128_t types[2];
    hash128_t *ids;
    int n_ids = 0;
    ArrayBuildState *out = NULL;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    deconstruct_array(PG_GETARG_ARRAYTYPE_P(0), BYTEAOID, -1, false, TYPALIGN_INT,
                      &values, &nulls, &n);
    if (laplace_relation_type_id("HAS_SENSE", &read.has_sense) != 0 ||
        laplace_relation_type_id("HAS_POS", &read.has_pos) != 0)
        elog(ERROR, "content_terms: HAS_SENSE / HAS_POS are not governed");
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(TermEvidence);
    ctl.hcxt = CurrentMemoryContext;
    read.evidence = hash_create("content term evidence", Max(n, 8), &ctl,
                                HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    ids = palloc(sizeof(hash128_t) * Max(n, 1));
    for (int i = 0; i < n; ++i)
    {
        bool found;
        hash128_t id;
        if (nulls[i]) continue;
        id = datum_to_hash128(values[i]);
        TermEvidence *entry = hash_search(read.evidence, &id, HASH_ENTER, &found);
        if (found) continue;
        entry->has_sense = entry->has_upos = false;
        ids[n_ids++] = id;
    }
    if (n_ids > 0)
    {
        bool spi_top = false;
        ArrayType *subjects = hash128_array_from_ids(ids, n_ids);
        types[0] = read.has_sense;
        types[1] = read.has_pos;
        ArrayType *type_ids = hash128_array_from_ids(types, 2);
        if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
            elog(ERROR, "content_terms: SPI connect failed");
        laplace_consensus_scan(subjects, NULL, type_ids, receive_term_evidence, &read, NULL);
        laplace_spi_finish(spi_top);
    }
    for (int i = 0; i < n_ids; ++i)
    {
        TermEvidence *entry = hash_search(read.evidence, &ids[i], HASH_FIND, NULL);
        if (!entry->has_sense) continue;
        if (entry->has_upos && !laplace_upos_is_content(&entry->upos)) continue;
        Datum id = hash128_to_datum(&ids[i]);
        out = accumArrayResult(out, id, false, BYTEAOID, CurrentMemoryContext);
    }
    if (!out)
        PG_RETURN_ARRAYTYPE_P(construct_empty_array(BYTEAOID));
    PG_RETURN_DATUM(makeArrayResult(out, CurrentMemoryContext));
}

/* converse.firmware(name): a governed firmware image's policy and content id, so SQL
 * orchestration passes the image's values instead of literals. NULL name = default. */
Datum
pg_laplace_firmware(PG_FUNCTION_ARGS)
{
    const laplace_firmware_image_t *image = PG_ARGISNULL(0)
        ? laplace_firmware_default()
        : laplace_firmware_lookup(text_to_cstring(PG_GETARG_TEXT_PP(0)));
    TupleDesc desc;
    Datum values[15];
    bool nulls[15] = {false};
    hash128_t id;
    if (!image)
        ereport(ERROR, (errmsg("firmware: no governed image of that name")));
    if (get_call_result_type(fcinfo, NULL, &desc) != TYPEFUNC_COMPOSITE)
        elog(ERROR, "firmware: composite result required");
    laplace_firmware_id(image, &id);
    values[0] = hash128_to_datum(&id);
    values[1] = CStringGetTextDatum(image->name);
    values[2] = Float8GetDatum(image->salience_floor);
    values[3] = Int32GetDatum(image->semantic_hops);
    values[4] = Int32GetDatum(image->fanout);
    values[5] = Int32GetDatum(image->meeting_hops);
    values[6] = Int32GetDatum(image->meeting_fanout);
    values[7] = Int32GetDatum(image->meeting_frontier);
    values[8] = Int32GetDatum(image->sharing_window);
    values[9] = Int32GetDatum(image->sharing_cap);
    values[10] = Float8GetDatum(image->spread);
    values[11] = Int32GetDatum(image->top_k);
    values[12] = Int32GetDatum(image->steps);
    values[13] = Int32GetDatum(image->max_stride);
    values[14] = Int32GetDatum((int32) image->version);
    PG_RETURN_DATUM(HeapTupleGetDatum(heap_form_tuple(BlessTupleDesc(desc), values, nulls)));
}
