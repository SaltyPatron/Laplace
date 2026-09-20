# Modality scalar/structure conformance checklist

This checklist applies the shared scalar/content law from modality-ladder-law.md.

## Scalar floor

For numeric modality values:

- no arbitrary amplitude/color/sample value is minted as a new Tier-0 atom;
- exact digital scalar representation decomposes into existing codepoint constituents;
- the ordered scalar composition/root is content-addressed and reusable;
- repeated equal values create occurrences, not duplicate scalar content;
- precision/radix/sign/decimal/exponent rules are versioned and reconstructable.

Examples:

~~~text
255     -> ['2','5','5'] -> one reusable number root
0.34567 -> ['0','.','3','4','5','6','7'] -> one reusable scalar root
~~~

A long finite pi prefix is the same mechanism at larger width.

## Provider and reconstruction

Every modality/source declares:

- exact artifact/provider generation;
- source numeric format and precision;
- canonical scalar recipe;
- ordered occurrence structure;
- channel/spatial/time/tensor/domain roles;
- physicality/trajectory types;
- provenance and reconstruction fields;
- calculation/testimony boundaries.

## Dedup proof

A representative fixture must show the same exact scalar used many times while:

- one canonical scalar root is reused;
- all occurrences/ordinals are retained;
- containing structures reconstruct exactly;
- re-ingest does not reinsert already-known scalar content as novelty.

## Product consequences

The same convergence law is used by code AST reuse, chess transpositions, repeated text/content, model structure, and repository subtree reuse. Content novelty and occurrence volume are separate axes everywhere.
