> Archived workstream analysis. Historical evidence only; GitHub owns status.

# W16 — Reinvented wheels: the census, the cause, and the gate

**Status:** measured census. No changes made. · **Related:** W3 (`CALLS` graph —
the mechanical fix), W15 (native offload), spec 37 §0 (the drift failure mode)

The claim under test: *"you reinvent wheels all over the place."* It is correct,
it is measurable, and the measurement shows the cause is **not** carelessness —
it is that **nothing in this repository can see whether a thing is already
implemented.**

---

## 1. The census — `laplace_geom` functions and their substrate callers

19 functions live in `public` (they take PostGIS `geometry` arguments, and
PostGIS defines that type in `public` — this is forced, not chosen). Counting
files under `extension/laplace_substrate/sql/` that reference each:

| function | substrate callers |
|---|---|
| `laplace_trajectory_constituents` | 17 |
| `laplace_frechet_4d` | 8 |
| `laplace_hash128_blake3` | 8 |
| `laplace_angular_distance_4d` | 7 |
| `laplace_mantissa_unpack` | 1 |
| `laplace_vertex_tier` | 1 |
| `laplace_hausdorff_4d` | 1 |
| **`laplace_radius_origin`** | **0** |
| **`laplace_centroid_4d`** | **0** |
| **`laplace_hilbert_encode`** | **0** |
| **`laplace_distance_4d`** | **0** |
| **`laplace_dwithin_4d`** | **0** |

**Five of twelve sampled functions have zero substrate callers.** Some of that
is legitimate — `hilbert_encode` is a write-side operation done natively at
ingest. But two are duplications with a live second implementation.

## 2. Duplication #1 — `radius_origin`, computed two ways

**Implementation A**, `schema/tables/physicalities.sql.in:18` — a generated
stored column:

```sql
radius_origin double precision GENERATED ALWAYS AS (
    sqrt(ST_X(coord)^2 + ST_Y(coord)^2 + ST_Z(coord)^2 + ST_M(coord)^2)
) STORED
```

**Implementation B**, `public.laplace_radius_origin(geometry)` — native C,
`IMMUTABLE STRICT`, backed by `pg_laplace_radius_origin`.

The stored column does not call the function. Four PostGIS accessor calls plus
arithmetic per row, when a single native call exists. **Same value, two code
paths, neither aware of the other.** Zero callers on B is the tell.

## 3. Duplication #2 — centroid, computed two ways

**Implementation A**, `engine/core/src/math4d.c:61` `math4d_centroid` — called
by the composer at write time (`hash_composer.c:29`) to produce the `coord` that
gets persisted.

**Implementation B**, `public.laplace_centroid_4d(geometry)` — the SQL-exposed
centroid, zero substrate callers.

**This one has already caused a real defect.** Because nothing forces the read
side to use the same centroid the composer used, "the centroid of the
trajectory" and "the persisted `coord`" drifted into meaning different things.
Measured 2026-08-02: `laplace_centroid_4d` applied to a stored `trajectory`
returns radius **1.6–2.1**, outside the unit sphere entirely — because the
trajectory holds *mantissa-packed payload*, not positions. A function that looks
like the right tool, accepts the argument without complaint, and returns a
plausible number that means nothing.

Same root cause as the Fréchet defect being fixed in #786: **a geometry-typed
container makes payload and position indistinguishable to the type system**, so
any metric will accept either.

## 4. The pattern, restated with tonight's other instances

| reinvented | the wheel that existed | how it was caught |
|---|---|---|
| `entities ⋈ physicalities` join in `geometry_audit` | `v_word_points` (correct key **and** `type = 1` filter) | returned **zero rows silently**; the wrong key looked like "no data" |
| `radius_origin` arithmetic in a generated column | `laplace_radius_origin` (native C) | this census |
| read-side centroid | `math4d_centroid` (the composer's) | radius 1.6–2.1, physically impossible |
| Fréchet over packed trajectories | `word_curve` realize path (`word_shape_distance` does it right) | Grok's audit; #786 |
| six ad-hoc diagnostic queries | `sense_audit`, `resolve_audit` — which did not exist until they were built | three wrong claims, corrected by one call |

## 5. The cause is structural, not attitudinal

Every one of these was written by someone who **could not see** that the
original existed:

1. **The database cannot see its own call graph.** Measured (W14 R0): 363
   functions, **zero** with parsed bodies, 9 dependency edges in the entire
   schema — all from views. Nothing can answer "who calls this" or "does this
   already exist" from the catalog.
2. **`api()` searches by substring of the *name*.** `laplace_radius_origin`
   lives in `public`, so a substrate author searching `api('radius')` finds
   nothing. The introspection surface does not cross the schema boundary that
   PostGIS forced.
3. **Names hide function.** `v_word_points` carries **every tier**, not just
   words. An author looking for a general entity↔physicality join would never
   search for it. Discoverability failure, not discipline failure.
4. **Nothing fails when a wheel is re-derived.** No gate, no test, no warning.
   The duplicate is usually *correct enough* to pass — which is worse than
   wrong, because it survives.

**A wheel gets reinvented when nothing can see that the original exists.** That
is the whole finding, and it is why "be more careful" is not a fix.

## 6. What is NOT the defect

The two-schema split is forced. `laplace_geom` functions take PostGIS
`geometry`; PostGIS installs into `public`; therefore they install beside it.
That is also the real reason every `search_path` ends `, public` — those bodies
genuinely reach across for geometry.

Blaming the schema layout is wrong and would send someone to consolidate
schemas, which cannot be done without dropping PostGIS types. **The boundary is
correct. The failure to call across it is the defect**, and it persists because
neither side can enumerate the other.

## 7. The mechanical fix — and why it is W3

A grep-based check ("did you re-derive this join?") cannot work: the duplicate
is not textually similar to the original, which is exactly why it was written.

The fix is the substrate reading its own source (**W3**, GH #765): with `CALLS`
and `DEFINES` edges over the code, three questions become indexed reads:

- **zero incoming `CALLS`** → a canonical nobody uses (`laplace_radius_origin`,
  `laplace_centroid_4d` today)
- **two `DEFINES` for equivalent computation** → a duplication candidate
- **"does this exist"** → a query, not a grep over two schemas

That is also the correct form of G4, and it is currently blocked because the
code lane cannot read `.sql.in` files at all (measured: `ingest code` accepted
**zero** of 44 files, because `Path.GetExtension("chat.sql.in")` returns `.in`).

## 8. Ordered work

1. **Fix the code lane** (W3 / #765): compound extensions, then SQL structural
   extraction. Everything else here depends on it.
2. **Make `api()` cross the schema boundary** — one-line-ish, and it is the
   cheapest thing that would have prevented duplication #1. An author searching
   `api('radius')` must find `public.laplace_radius_origin`.
3. **Retire the two duplications**: point the generated column at
   `laplace_radius_origin`, and give the read side one centroid entry point
   shared with the composer.
4. **Type-separate payload from position** (also #786's root): a
   `geometry`-typed column cannot distinguish a packed manifest from a curve, so
   every metric accepts both. A domain type, a naming law, or a checked wrapper
   — pick one; the current state guarantees this recurs.
5. **Then G4 as a substrate query**, retiring the grep scaffolding.

## 9. The honest caveat

This census sampled 12 of 19 `laplace_geom` functions and counted **file**
references, not call sites — a file referencing a function twice counts once,
and a zero here means "no `.sql.in` under `laplace_substrate` mentions it,"
which does not rule out C or C# callers. It is enough to establish the pattern
and to name two concrete duplications; it is **not** a complete duplication
audit, and calling it one would repeat the error this document is about.
