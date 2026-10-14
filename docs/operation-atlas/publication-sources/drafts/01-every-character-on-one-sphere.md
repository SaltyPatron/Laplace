# Every character humanity has ever typed, on one 4D sphere

*Draft. Edit freely. Image: `images/01-glome-of-unicode.png`, then `images/02-king-by-king.png`.*

Everything in Laplace starts with one decision: the floor of all digital content is the Unicode codepoint. Text is codepoints. Numbers are codepoints. A pixel's channel value, `255`, is the codepoints `2`, `5`, `5`. Everything breaks down to them, and everything is built back up from them.

So the first thing Laplace needs is a place for every codepoint. Not the ~160,000 that are assigned today: all **1,114,112** of them, the entire Unicode codespace, which is fixed forever.

That place is the surface of a 4D sphere, the glome (S³). Every codepoint gets a deterministic point on it:

- **Where the points are:** Marc Alexa's Super-Fibonacci spirals, which spread points evenly across the glome.
- **Which codepoint gets which point:** the points are walked in the order of their Hilbert value, a curve that visits neighbours one after another, and codepoints are sequenced by DUCET, Unicode's own collation order. Rank *r* takes the *r*-th point.

The result: characters that collate together sit together. `a` sits by its fullwidth, bold, script and circled forms, then `A`. And because a word's position is the centre of its characters' positions, words fall into place the same way. Measured on the real Unicode 17 data:

| from `king` | distance |
|---|---|
| `King` | 0.023 |
| `ring`, `sing` | 0.049 |
| `dong` | 0.070 |
| `kong` | 0.079 |
| `ding` | 0.105 |
| unrelated words | ~0.16 |
| `猫` | 0.651 |

King by king by ding by dong by kong. It isn't exact, but it's predictable, and it's recordable: every install of Laplace computes the same point for the same character, bit for bit, and a build fingerprint proves which layout you have.

The picture shows all 159,866 assigned codepoints, coloured by script, seen through the Hopf map, which is one of the classic ways to look at a 4D sphere in 3D.

Next: what happens inside the sphere.

Full technical details, research and sources: the Laplace wiki.
