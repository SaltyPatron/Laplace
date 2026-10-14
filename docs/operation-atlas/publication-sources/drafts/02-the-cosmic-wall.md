# The cosmic wall

*Draft. Edit freely. Image: `images/03-bands.png` (to be rendered from the prototype database).*

Last time, every Unicode codepoint got a point on the surface of a 4D sphere, the glome. That surface is a perimeter, a wall, and everything else in Laplace lives inside it.

A word is a composition of codepoints, and its position is the average of its codepoints' positions. A sentence is a composition of words and codepoints (the spaces and punctuation count; nothing is dropped), so its position is the average of those. And so on up: paragraphs, documents, files, images, everything.

The math gives three guarantees:

1. **Nothing can ever fall outside the wall.** An average of points on or inside a sphere is always inside it, at every level, forever, no matter how much is added.
2. **Only pure repeats touch the wall exactly.** `n`, `nn`, `nnnnn` all sit exactly on `n`'s point. Anything with variety falls inward.
3. **Every composition is at least as deep as the average of its own constituents.** That's the triangle inequality, and it's why tiers layer: words sit inside the wall, sentences inside their words, and so on down.

How deep something sits measures how concentrated its constituents are. Statisticians call it the mean resultant length: 1 when everything is identical, toward 0 as things spread evenly over the whole sphere. So tiers form bands, and the bands of a text settle toward the center of what the text is made of. A book in English settles where English settles; a document mixing scripts and modalities sinks deeper.

A text is essentially a colloid: its words fall inward while its spaces and punctuation stay on the wall, and the whole settles where the mixture settles.

And it's not just theory. In the prototype, every one of 3.9 million records built from 195 Project Gutenberg books was checked in exact integer arithmetic: none is outside the wall, every pure repeat sits exactly on its point, and every sampled composition is at least as deep as its constituents' average.

Knowledge can occupy the same space; matter cannot. `tar`, `rat` and `art` share a position exactly, and their identities stay separate, because every composition also has an exact ID and an ordered path.

A note for the mathematicians: computers round. Laplace's coordinates live on an exact fixed-point grid, and averages round toward the center, so the guarantees hold bit for bit, not just approximately.

Next: why storing everything humanity has written doesn't mean storing it more than once.

Full technical details, research and sources: the Laplace wiki.
