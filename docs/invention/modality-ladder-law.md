# Modality ladder law — shared codepoint floor, reusable scalar trajectories, typed modality structure

This binding law is read under docs/INVENTION.md and docs/CAPABILITIES.md.

## The actual media/scalar law

Do not mint an arbitrary sensor/sample value as a new Tier-0 atom.

A finite digital scalar such as:

~~~text
0.34567
~~~

is canonical ordered content over the already admitted codepoint floor:

~~~text
['0', '.', '3', '4', '5', '6', '7']
~~~

Likewise:

~~~text
255    -> ['2','5','5']
-32768 -> ['-','3','2','7','6','8']
~~~

Those constituents compose into one reusable scalar/number identity and one exact ordered trajectory.

When that same scalar occurs again, the scalar content is not recorded again. The containing modality records another occurrence/reference to that already-known scalar at a different sample/channel/pixel/time/ordinal.

This is ordinary Laplace content-address convergence:

~~~text
content novelty != occurrence volume
~~~

## Pi is the same proof at larger width

A finite observed prefix of pi is not a million new Tier-0 atom kinds.

It is one ordered composition of the existing digit / punctuation codepoints. The repository's core benchmark already notes that "pi's million digits compose as ONE word"; the trajectory implementation separately proves that composition width is not limited by the local 16-bit ordinal field.

Conceptually:

~~~text
pi_N = compose(['3','.', '1','4','1','5','9', ... N finite digits ...])
~~~

The exact finite digit prefix determines the canonical composition/root. Re-observing the same prefix reuses that root.

The packed GeometryZM trajectory is the exact ordered constituent manifest. One vertex carries one constituent identity plus local ordinal/run/flags. GeometryZM/varlena/memory/storage limits bound one executable materialization; they do not turn each digit or scalar value into a new atomic alphabet.

## Tier-0: current executable floor versus abstract law

The current executable shared textual/number floor is the selected Unicode codepoint generation backed by the UCD/DUCET perfcache.

The abstract Tier-0 address law remains open and is not mathematically capped by today's Unicode release. That extensibility is not permission for a media decomposer to allocate arbitrary amplitude/color/sample values into unused Tier-0 ranks.

A new Tier-0 atom generation is a governed foundation change. Ordinary digital numbers and modality values compose above the existing floor.

## Exact scalar canonicalization

A scalar recipe must be deterministic and lossless for the admitted digital representation.

It declares, as applicable:

- radix;
- sign;
- decimal point;
- exponent form;
- leading/trailing-zero normalization;
- exact precision/scale;
- integer/rational/fixed/floating source representation;
- signed-zero / NaN / infinity handling when the source format permits them.

Do not stringify an inexact host floating-point approximation and call it the source value. Canonicalization starts from the exact admitted digital representation and its declared precision boundary.

For a value already supplied as an exact canonical decimal surface, ordinary content decomposition is the scalar identity/trajectory machinery. ModelCoordinates.ScalarId(string) is one current caller of that law.

## Audio

An analog waveform enters Laplace only through a finite digital observation.

For each decoded sample occurrence:

~~~text
exact decoded sample value
-> canonical scalar trajectory/root
-> sample occurrence
-> window/frame
-> segment
-> phrase/track
~~~

The occurrence carries the context that is not the scalar itself:

- sample ordinal;
- channel;
- sample format / quantization precision;
- sample rate / time mapping;
- source/package/reconstruction provenance.

Example:

~~~text
S = compose(['0','.','3','4','5','6','7'])

track:
  sample #1201 -> S
  sample #9342 -> S
  sample #18117 -> S
~~~

There is one canonical S. There are three sample occurrences.

A million identical amplitudes do not create a million scalar entities.

## Images

The same rule applies to channel values.

~~~text
N255 = compose(['2','5','5'])

pixel A:
  R -> N255
  G -> N128
  B -> N255

pixel B:
  R -> N255
  ...
~~~

N255 is reusable canonical numeric content. Channel role, pixel location, dimensions, layout and source occurrence remain typed structure around it.

Do not mint a packed RGBA tuple or channel intensity as a private Tier-0 atom merely because it is convenient.

## Video, models, machine data and other numeric modalities

Repeated numeric values reuse canonical scalar content where the declared recipe says the values denote the same exact scalar.

Their modality roles remain distinct occurrences/physicalities:

- frame/time/channel;
- tensor coordinate/dtype/precision;
- machine counter/register/field;
- measurement unit/source/calibration;
- domain-specific role.

Canonical numeric equality does not erase those contexts.

## Provider -> recipe -> shared structure

Every source still uses its exact provider/grammar/codec:

~~~text
artifact
-> provider recovers exact source structure
-> recipe classifies fields/values
-> reusable content/compositions
   + ordered occurrences/trajectories
   + physicality
   + provenance
   + testimony
   + calculations
   + reconstruction state
~~~

The provider does not get a private identity law.

## Packaging is not identity

JPEG/PNG/WAV/FLAC/MP3/MP4/JAR/ELF/etc. recover physical artifacts and source structure.

Container offsets, compression blocks, paths and codec framing remain provenance/reconstruction unless a declared content recipe requires them.

## Perfcache law

A numeric perfcache is an accelerator for already-defined canonical scalar compositions.

The current 0..255 number ROM precomputes the most common integer scalar roots used by byte-valued media. It does not mean:

- the scalar universe stops at 255;
- every scalar must be precomputed;
- a cache is the identity authority.

A fractional scalar such as 0.34567 can be composed normally without being present in that dense ROM. Repeated occurrences then reuse its canonical root through ordinary content addressing/indexing.

## Cross-modality perfcache reuse

Perfcaches follow canonical composition tiers, not consumer modality names.

For image/video:

~~~text
number roots
-> channel roots
-> pixel ROM
-> patch ROM
-> region ROM
-> image/frame ROM
                 \
                  -> video order/timing composition
~~~

For audio/video:

~~~text
scalar/sample roots
-> window ROM
-> segment/phrase/track ROM
                         \
                          -> video audio/timing composition
~~~

A video frame that is already a cached canonical image must reuse that image record. A soundtrack reuses the same audio structures as standalone audio. Video adds the containing timing/synchronization trajectory rather than duplicating lower image/audio state.

A finite practical tier may be exhaustively materialized into a dense direct-address mmap. A higher combinatorial tier may cache the finite admitted/hot canonical estate. Both remain derived acceleration under spec 33.

Current implementation owner: #1711.

## Physicality and trajectory

Keep these distinct:

~~~text
canonical scalar/entity identity
packed GeometryZM constituent trajectory
real physicality coord
realized child-coordinate curve
modality occurrence / role
~~~

Packed trajectory values are exact manifests, not spatial sample values. Frechet/Hausdorff/etc. apply to realized curves when the operation calls for geometry.

## Acceptance

- arbitrary audio amplitudes/channel values are not minted as fake Tier-0 atoms;
- exact finite numeric surfaces decompose to reusable canonical scalar roots;
- equal scalar content converges across repeated occurrences;
- occurrence ordinal/channel/time/pixel/tensor context remains independently attributable;
- source precision/quantization is retained so scalar canonicalization is reversible;
- pi or another long finite numeric surface can be represented as one wide ordered composition without turning digits into novel atoms;
- composition width is not silently limited by the packed 16-bit ordinal copy;
- number perfcaches accelerate common values without defining the scalar domain;
- media packages remain provenance/reconstruction rather than identity authority.

## Non-success

- one new Tier-0 entity for every amplitude/sample/color value;
- hashing raw PCM/RGBA as a private atom identity;
- duplicating the same scalar entity for every occurrence;
- losing sample rate/channel/precision/time while preserving only a flat scalar stream;
- using host float formatting that cannot reconstruct the admitted digital value;
- treating the 0..255 perfcache as the set of representable numbers.
