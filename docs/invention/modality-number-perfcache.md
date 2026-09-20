# Modality number perfcache — acceleration for canonical scalar roots

The modality-number perfcache is derived ROM for common scalar compositions. It is subordinate to docs/specs/33_Perfcache_Blob_Law.md and modality-ladder-law.md.

## What v1 accelerates

v1 precomputes canonical integer content roots for 0..255:

~~~text
0   -> ['0']
42  -> ['4','2']
255 -> ['2','5','5']
~~~

This is useful for byte-valued image channels and any other source that lawfully uses the same exact abstract integer values.

The cache does not invent those identities. The ordinary content composer does.

## What it does not mean

The cache does not mean:

- 0..255 is the numeric universe;
- audio values must be uint8;
- fractional values need new Tier-0 atoms;
- every number must have a ROM entry.

For example:

~~~text
0.34567 -> ['0','.','3','4','5','6','7']
~~~

can be composed through the ordinary content/trajectory path. Once its scalar root exists, repeated occurrences reuse it exactly like a cached integer root.

A larger future numeric perfcache may accelerate a selected finite hot set, but cache coverage is never semantic coverage.

## Correctness

- cache records must equal ordinary canonical composition for the same scalar;
- missing cache entries fall back to canonical composition;
- cache generation is deterministic/rebuildable;
- changing a scalar canonicalization recipe requires a new cache generation;
- source occurrence roles/precision/channel/time are not stored in the scalar ROM merely because they reference the scalar.
