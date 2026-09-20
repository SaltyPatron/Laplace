# Compositional perfcache lattice

Laplace perfcaches are not "one cache per modality."

A perfcache is a deterministic, versioned, mmap-friendly acceleration of reusable canonical structure or a deterministic calculation over it.

The natural organization is a dependency lattice that follows composition:

~~~text
Unicode/codepoints
      |
      v
canonical scalars / numbers
      |
      +------------------+
      |                  |
      v                  v
image channels       audio samples
      |                  |
      v                  v
pixels               windows / frames
      |                  |
      v                  v
patches              segments / phrases
      |                  |
      v                  v
regions              tracks
      |                  |
      v                  |
images / frames          |
                        /
                       /
        +---- video ----+
             timing/sync
~~~

Chess has the same pattern:

~~~text
piece/square vocabulary
-> positions
-> legal/canonical transitions
-> lines / openings / replay surfaces
~~~

Software can likewise reuse grammar/token/node/AST/subtree caches where a finite or admitted deterministic set makes that worthwhile.

## Dense finite versus sparse/admitted caches

A cache does not need to be Tier-0 and it does not need to cover an infinite universe.

There are two lawful shapes.

### Dense complete ROM

When a recipe's legal state space is finite, directly enumerable and serviceable in the selected machine/resource envelope, precompute the entire domain.

Examples can include:

- Unicode generation records;
- relation/highway manifests;
- canonical small numeric domains;
- finite piece×square vocabularies;
- a pixel domain when its exact format/cardinality makes full enumeration practical.

A dense domain can use direct indexing:

~~~text
record = records[address(value)]
~~~

This is true O(1) and needs no hash table.

The exact cardinality depends on the declared pixel/sample format. Do not pretend all image formats have one universal pixel population.

### Sparse/admitted/hot ROM

Patches, regions, frames, images, AST subtrees, chess positions and other higher compositions may have enormous possible universes while the actually admitted estate is finite.

Cache the deterministic canonical structures that are present, selected or sufficiently hot:

~~~text
canonical id / compact key
-> immutable mmap lookup
-> id / coord / hilbert / trajectory metadata / typed fields
~~~

A sparse cache may use a minimal-perfect hash, open-addressed fixed table, deterministic hash index, sorted key table, or another versioned lookup structure. Its big-O and collision semantics must be stated honestly.

"Not exhaustive" does not mean "not a perfcache."

## Modular selectors and profiles

A cache does not have to materialize a whole tier or only a heat-selected set. It may compile a declared modular subset.

Useful selector classes include:

- **range:** ASCII `U+0000..U+007F`, a numeric interval, a bounded ordinal range;
- **explicit set:** selected hexadecimal colors, named palette entries, selected chess positions;
- **generated finite domain:** all values permitted by one declared bit depth/format;
- **band:** frequency bins/filter-bank coefficients inside a declared audio-analysis band;
- **predicate:** records matching deterministic type/recipe fields;
- **hot/admitted:** the finite known estate that has already occurred or crossed a usage threshold;
- **dependency closure:** every constituent needed by a selected higher-tier object/package.

The selector is versioned and receipted.

### Example: palette ROM

Suppose a deployment only needs a declared set of colors:

~~~text
palette:
  #000000
  #FFFFFF
  #FF0000
  #00FF00
  #0000FF
  ...
~~~

The image cache can compile only the corresponding canonical pixel/color records, then optionally precompute every serviceable patch/region structure over that palette.

Those pixels keep the exact same canonical ids/coords they have in any full image generation.

A different palette is another cache module, not another image ontology.

### Example: ASCII profile

An embedded/terminal/code workload may map only the ASCII segment of the Unicode generation plus the grammar/number structures it needs.

~~~text
U+0000..U+007F
-> same canonical codepoint ids/coords as full T0
-> local direct index
~~~

Do not renumber ASCII from 0..127 as a new canonical universe merely because the module is compact.

### Example: speech-oriented audio profile

A speech deployment may compile only the sample formats plus frequency-domain calculations it actually uses.

The frequency cache is a **calculated projection**, not raw audio identity:

~~~text
sample-rate/window/analyzer recipe
+ declared speech band/filter-bank selector
-> selected deterministic frequency-bin/filter records
~~~

Standalone audio and video soundtrack analysis can both reuse those records.

Do not confuse "human speech frequency profile" with deleting frequencies from Laplace's knowledge. It is a resident calculation/cache profile.

### Profile composition

A runtime/device can map several modules:

~~~text
profile =
  unicode/ascii
+ number/common
+ image/palette/acme-ui
+ audio/speech-band/v2
+ image/hot-patches
+ video/hot-frames
~~~

The registry resolves overlap by canonical key/generation and retains one semantic record.

This enables very small edge profiles as well as large server profiles without changing canonical identity.

## Local addresses versus canonical addresses

A compact module may assign a dense local slot to selected records:

~~~text
slot 0 -> canonical U+0020 record
slot 1 -> canonical U+0021 record
...
~~~

or:

~~~text
slot 17 -> canonical pixel #FF0000
~~~

The local slot is not the entity id, Tier-0 rank, Hilbert key or semantic ordinal. It exists only so the mapped module can do very cheap pointer arithmetic.

That distinction lets a tiny ASCII/palette/band module be densely packed without corrupting global identity.

The manifest records the mapping and canonical-generation fingerprint so a consumer can always recover/verify the global record.

## Cross-modality reuse

Higher modalities consume lower cached structures; they do not clone them.

A video frame that is canonically the same image uses the image root and image cache records.

A video's audio track uses the same audio sample/window/track cache structures that standalone audio uses.

Video-specific state is the higher composition:

~~~text
ordered frame roots
+ audio roots
+ timestamp / synchronization occurrences
-> video root / trajectory
~~~

The cache dependency is therefore compositional rather than named after the consumer.

An image perfcache can accelerate:

- standalone image ingest/read/search;
- every video frame using the same image recipe;
- document/page/image extraction;
- any other modality embedding/referencing that image structure.

An audio perfcache can accelerate standalone audio, video soundtracks, multimodal recordings and any other consumer of the same canonical audio structures.

## Pixels, patches, regions and complete images

Nothing in the perfcache law stops caching higher image tiers.

For a declared image recipe:

~~~text
numbers
-> channels
-> pixels
-> patches
-> regions
-> images
~~~

each tier is deterministic canonical composition.

If a complete pixel state space is practical, emit a complete pixel ROM. Otherwise emit the admitted/hot pixel estate.

Likewise, the finite set of patches/regions/images already known to Laplace can be exported as mmap records. Re-observing one becomes a cache hit rather than recomposition/database probing.

The same canonical image can occur in many files, documents or videos while the image record exists once.

## Scale-complete image substructure lattice

An image region participates in every contiguous square subregion it contains, not only one fixed partition size.

For an N×N region, the number of contiguous square occurrences across sizes 1×1 through N×N is:

~~~text
sum(k=1..N) (N-k+1)^2
= sum(j=1..N) j^2
= N(N+1)(2N+1)/6
~~~

For N=8:

~~~text
1x1  64
2x2  49
3x3  36
4x4  25
5x5  16
6x6   9
7x7   4
8x8   1
---------
total 204
~~~

These are 204 **substructure occurrences** in that 8×8 region. Their canonical roots are globally reusable. If a 3×3 or 4×4 pattern has already occurred elsewhere, this region references the same canonical structure rather than creating another semantic copy.

This creates a deterministic multiscale image basis:

~~~text
pixels
-> every 2x2
-> every 3x3
-> every 4x4
-> ...
-> complete region
~~~

The basis is not limited to powers of two and is not a learned receptive field. It is exact source structure under the declared image recipe.

### The 2x2 inside the 8x8 is not an 8x8-owned record

Suppose four ordered pixel entities compose to P:

~~~text
P = compose([p0,p1,p2,p3])
~~~

If that exact ordered composition occurs inside 8x8 A, 8x8 B, frame C, image D, or standalone in a query, there is still one canonical P.

The higher structures carry occurrences/references to P in their trajectories. They do not mint P-at-parent, P-at-tier, or P-at-frame variants as content identities.

This is exactly the same as king: once [k,i,n,g] exists, every use points at the same entity and adds role/context/evidence around it.

Therefore the 204 square occurrences in an 8x8 region are references into the global canonical content world. The region may introduce some previously unseen square compositions, but every already-known 2x2/3x3/... root is simply reused.

### Why this compounds

A larger region is described in terms of many already-known smaller canonical structures.

Two non-identical 8×8 regions can therefore share dozens or hundreds of exact lower-scale structures even when their complete 8×8 roots differ.

That gives Laplace exact multiscale response planes for:

- structural equality;
- partial overlap;
- repeated textures/backgrounds;
- edges/corners/shape fragments;
- sprite/tile reuse;
- video frame reuse;
- cross-image similarity;
- anomaly/residual detection.

The cache can expose these roots directly, so a higher image/video operation does not rediscover the same lower-scale patterns.

### Storage shape

Do not confuse occurrence count with novelty count.

An 8×8 region may reference 204 square occurrences while introducing far fewer new canonical roots.

A ROM record can carry compact local references into the loaded cache generation:

~~~text
8x8 record
  64 refs -> 1x1 roots
  49 refs -> 2x2 roots
  36 refs -> 3x3 roots
  ...
   1 ref  -> 8x8 root/self
~~~

Local slots are acceleration addresses only; they resolve to global canonical ids.

A selected cache module may retain all scale references or only the scale families required by a workload. The selection is receipted and never changes the canonical structures themselves.

## Trunk-first cache short-circuit

Probe the highest lawful reusable composition before rebuilding its descendants.

For a decoded image:

~~~text
decoded image + shape/recipe
-> image cache key
   hit  -> reuse complete image root; stop
   miss -> region keys
           hit  -> reuse region
           miss -> patch keys
                   hit  -> reuse patch
                   miss -> pixel/direct-domain lookup
~~~

This is the perfcache analogue of trunk-to-leaf existence probing. Work is proportional to the novel branches, not automatically to every node in the artifact.

A cache lookup key may be a deterministic materialization fingerprint over exact recovered bytes/shape/recipe that maps to the canonical record. That lookup fingerprint is acceleration state, **not** semantic identity. Collision/verification behavior must be explicit; a key collision cannot silently return the wrong canonical object.

For repeated video frames this matters enormously: after decode, a complete-image hit can bypass image tree reconstruction entirely. If the whole frame is new but most patches are known, only novel branches need composition.

Audio can use the same strategy:

~~~text
track -> segment -> window -> sample
~~~

and software repositories can do the same with repository/file/AST-subtree caches.

## What a record can carry

A perfcache record may carry deterministic facts needed by hot operations, for example:

- canonical entity id;
- type/recipe generation;
- coordinate;
- Hilbert key;
- constituent count;
- trajectory offset/length into an immutable section;
- compact dependency keys;
- lookup class/version flags;
- reconstruction metadata that is deterministic and appropriate for the cache class.

It must not silently absorb testimony, mutable standing or occurrence provenance merely because those are convenient to read.

## Index-friendly execution

The perfcache should make database indexes easier to use, not harder.

Preferred shape:

~~~text
request value / structure
-> native/perfcache lookup
-> canonical id / coord / hilbert / typed key
-> prepared SQL/SPI probe using that key as a parameter
-> ordinary B-tree/GiST/GIN/HASH index
~~~

Avoid:

~~~text
WHERE expensive_function(indexed_column) = ...
WHERE per_row_cache_lookup(indexed_column) = ...
~~~

when the same transform can be performed once on the request/constant side.

A cache-backed operation should feed stable constants/arrays/ranges into the indexed relation or execute the entire bounded hot lookup natively. It must not hide the indexed column behind a function and force a scan.

If an expression index genuinely uses a cache-backed function, the function's generation/immutability contract must make PostgreSQL's immutability requirement truthful; a mutable external mmap cannot be smuggled behind an IMMUTABLE declaration.

## Cache dependency and invalidation

Each higher cache records the generations/fingerprints it depends on.

For example:

~~~text
pixel ROM
depends on:
  T0 generation
  numeric scalar recipe
  image channel order/precision recipe
  placement/composition recipe

patch ROM
depends on:
  pixel ROM/canonical image recipe generation
  patch shape/order recipe

video structure ROM
depends on:
  image recipe/cache generation
  audio recipe/cache generation
  timing/synchronization recipe
~~~

A lower-generation change invalidates/rebuilds affected dependents. Unrelated caches remain usable.

## Database and cache are siblings

PostgreSQL remains durable semantic state.

The perfcache is a deterministic projection/acceleration of that state or of the same canonical generator inputs. It is not an alternate truth source and is never allowed to seed invented semantic rows back into PostgreSQL merely because the mmap contains them.

## Performance consequence

The goal is to eliminate repeated work:

~~~text
first/new structure:
  compose / verify / persist / maybe publish cache generation

known structure:
  mmap lookup
  -> O(1) or declared bounded lookup
  -> reuse canonical record
~~~

That principle compounds upward.

If every frame of a video is already a known image root, video construction should not recompute every pixel/patch/region. It should mostly compose/reuse frame roots plus the genuinely new timing/sequence structure.

This is the same structural-sharing principle used for repositories and software patches.


## Current implementation owner

#1711 owns the common cache registry/dependency manifest, dense/sparse lookup framework, image/audio higher-tier cache generations, video reuse and index-preserving integration.
