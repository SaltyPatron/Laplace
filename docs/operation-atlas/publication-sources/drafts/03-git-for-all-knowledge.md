# Git for all knowledge

*Draft. Edit freely. No image yet (a tree diagram of the white image would fit here).*

If you've used Git, you already know the core idea of Laplace storage: **same content, same hash.** Git names every file by a hash of its content, so identical files are stored once and a single commit hash stands for an entire project.

Laplace does that for everything, at every level:

- A word is a composition of codepoints, named by the BLAKE3 hash of its constituents.
- A sentence is a composition of words and codepoints, named the same way.
- Up and up, to whole documents, images and recordings.

Every piece of content that already exists is stored exactly once, no matter how many times it appears or where. "The" is stored once for all of human writing.

Take an all-white image:

```text
image  = [row × 1080]            one entity
row    = [white pixel × 1920]    one entity
pixel  = [255, 255, 255]         one entity
255    = [2, 5, 5]               one entity
```

A billion white pixels cost a handful of entities, and every white row in every other image ever ingested is the same entities, already there.

The same structure makes lookups instant. My computer can decompose "Sherlock Holmes" on its own, compute its ID without asking the database anything, and then ask one question: where is this? Every sentence, every document, every occurrence comes back from that one ID.

And because it's deterministic, my install of Laplace and yours agree on every ID and every coordinate without ever talking to each other. Syncing is just exchanging what the other side doesn't have.

Under the hood, each entity's ID is packed into the bits of 4D coordinates, and each composition's content is a path through its constituents' IDs, so the database can search all of it with the same spatial indexes that power maps. More on that soon.

Full technical details, research and sources: the Laplace wiki.
