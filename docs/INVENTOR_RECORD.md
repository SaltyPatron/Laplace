# Laplace — the inventor's record

Every statement below is Anthony Hart's own typed words, quoted verbatim from his Claude Code and Codex sessions on this host (2026-08-08 through 2026-09-26). Excerpts are marked with `…`. Nothing here is paraphrase. Where this record and any other document in the repository disagree, this record is the inventor's statement.

---

## What Laplace is

> This is a complete reinvention of AI without a GPU and we need to start actually showing that — 2026-08-08

> This is a SQL transformer essentially that we're working out… — 2026-08-08

> Laplace is a complete reinvention of the transformer to execute via SQL and Laplace is a universal substrate... text, image, audio, video, etc... it doesnt give a fuck and treats them all the same... modality agnostic, language agnostic, model agnostic... cross-model, cross-modal, cross-language, etc... Data ingestion, no context window, no hallucination (no softmax), never forgets, can query any piece of itself as a prompt, can return any modality in exactly the same way/speed... text just as fast as images... same content = same hash, relation types, trust levels, source/type trust variations, model ingestion, model export, attestations, witnessing, etc... If you fuck up a single piece of the invention, you fuck up the entire invention — 2026-08-24

> Have you not realized that I am reinventing the generative pre-text transformer and completely eliminating the GPU requirement while enabling compatability and consumption of conventional AI? — 2026-09-25

> "Wow, this guy can ingest any form of digital content, make it compete on an even playing field, and query it via SQL like a conventional AI model, without training and without a GPU" — 2026-08-26

> Laplace doesnt require or rely on anything conventional AI like a GPU or static models... static models are food and poop for laplace — 2026-09-07

> Laplace has coherent conversations, can write high level code, can ingest models, can export models... text, image, audio, video, chess, etc... — 2026-09-07

> Laplace chats, laplace writes code, laplace ingests all forms of normal digital content, laplace properly ingests and attests seed/source content, laplace ingests models, laplace exports models, laplace replaces all AI/ML operations as I have documented and invented — 2026-09-06

> We can't lose the forest for the trees or treat individual pieces as "a whole"... Laplace is Laplace, nothing less... We have to establish the framework and functionality before we just start slapping things onto it — 2026-08-25

> "Laplace is Laplace"… Everything has to work or nothing does — 2026-09-05

> A leaf spring is not the whole watch and throwing a leaf spring into a bag and calling it a rolex is bullshit — 2026-09-12

> Laplace speaks Unicode and renders language — 2026-09-05

> I've been working on Laplace since March 2025 with a fairly ridiculous objective: rethink the conventional transformer and eliminate the requirement for GPUs — while still allowing GPUs and other accelerators to be used for performance when available. … What if many of the things modern AI treats as expensive runtime calculations could instead be represented as persistent, structured knowledge? … One of the larger ideas behind Laplace is that information normally buried inside a trained AI model may instead be represented by a dynamic, persistent state — and that state could potentially be checkpointed into a functional model without requiring conventional GPU-heavy training or gradient descent to create it. — Patreon post 1, 2026-08-26

---

## Identity: same content = same hash

> "King" != "king" and we have attestations that link them, unicode for case/accent/etc... — 2026-08-17

> same content = same hash is VITAL... determinism depends on it — 2026-08-17

> If i have [c,a,t] as an entity, how fucking stupid are you to write [c,a,t] again when recording the cat in the hat or the cat sat on the mat? — 2026-08-30

> same content = same hash... tier is a floor... "Hello" is a word and can be a full sentence too — 2026-08-25

> Entities have tiers but the tier isn't part of it's content... if we have a canonical ID function that makes a fake ID or record, why don't we just have "special record" as an entity and reference that with either the physicality trajectory or some form of referential integrity? — 2026-09-25

> everything has a blake3 hash so Laplace only works with hashes that match and have attestations and such... Why we're doing stupid text searches is beyond me... If i search "Carlsen" i should see everything that contains "Carlsen" because [C,a,r,l,s,e,n] collides somehow with existing entities and what it collides with are the results.... "Do i impact you? Come with me..." — 2026-09-08

> You clearly dont know about physicality trajectories and how they are supposed to work so why are you raping me with any stripping of any content? Any to lower? same content = same hash — 2026-09-06

---

## Entities, physicalities, and the core tables

> Entity, Physicality, Attestation, Consensus... why do i need more tables than this — 2026-08-28

> entity, physicality, attestation, consensus, witness... Aside from these core tables, i shouldn't have any fake lookup tables like physicality_observations or entity_something because the merkle dag and invention as i have documented explain how things should link, how the tree structure is my spider web colony where i tug one strand and everything else tugs back how hard — 2026-09-25

> entities are what get referenced by physicalities... entities are the building blocks, physicality is how content gets recorded... follow? I know its kind of cart before the horse/recursive but thats a merkle dag... — 2026-09-25

> entity is the "handle" of some sort... it is the raw decomposed component... The letter c — c,a,t — a full sentence... — 2026-09-25

> ahh, "building block" really is codepoints... "content" are building blocks comprised of building blocks... does that make sense? Recursion/circular reference/etc? — 2026-08-18

> Do you understand how entities are building blocks and content? Do you understand how an entity is a point of the geometry of a physicality trajectory? — 2026-08-30

> The Hash128 canonical names is a hack... these lookup tables are hacks... — 2026-09-25

> Why the fuck do i have these fake manually injected records to placeholder bullshit that should naturally fall in place? — 2026-09-25

---

## Tiers

> tier 0 are codepoints only and honestly it works even if tier 0 isnt recorded... its always the fixed list from the unicode so unless relations/attestations/etc need referential integrity/keys/etc... tier 1 is graphemes from uax29 but is type specific — 2026-09-25

> tier 4... codepoint grapheme word sentence document... thats the content node of the file... not the file entity trunk node — 2026-09-05

> tiers are modality specific and dynamic... what about paragraphs? what about titles? what about pages? what about other forms of UAX29 separators? What about thinking about UAX29 to images, audio, video, etc? — 2026-09-05

> "image/audio explicitly reject private RGBA/PCM T0 alphabets" What is an alphabet if not codepoints? The concept here is that like for text... graphemes start at tier 1 because their the first logical n-gram from codepoints for that specific modality... — 2026-08-18

> (simplified example) [[2,5,5],[2,5,5],[2,5,5]] plus attestations and what-not? Think about how you would write a pixel out onto a piece of paper, know that its a digit, number, channel/intensity/whatever, pixel, patch, region, image, scene, etc. — 2026-08-18

> a text tier 2 physicality vs an image tier 2 physicality... Laplace reads/handles them the same... it only differs when it matters for ingest or export, right? — 2026-08-18

> physicality trajectories are based on tiers... the space cat space in space the space hat... [t,h,e], [' '], [cat], in, the, hat, etc... You get what i mean?... H He Hell Hello unicode trajectories/paths for a word... Hello there, my name is … words and such to make sentences... UAX29, tiers, o(tier), trunk to leaf, leaf to trunk... — 2026-09-01

> space is latin/english... not japanese... so its an entity in the path/trajectory... [C,a,p,t,a,i,n],[' '],[A,h,a,b] (example) — 2026-09-08

> Are you also considering that whitespace is latin-specific in a universal substrate? The link between Captain and Ahab has a hop... its not direct — 2026-08-17

> "GSomething" or "Something " winds up as what tier? — 2026-09-03

---

## Geometry: Unicode on the S³, and what geometry is not

> Deep dive into my invention... s3, unicode, ducet, super fib, hop fibration, seeding, each dataset i have, the attestations, witnessing, relation types, tiers (instead of just one fixed tier of "token"), etc... — 2026-08-08

> 4d ball in a box is the physical space i've created with unicode as the surface perimeter so tier 0 unicode is all that goes on the surface and everything falls within — 2026-09-25

> You aren't forgetting the projection across the glome and all of that, right? Super fibonacci, hopf fibration, etc? — 2026-08-25

> surrogates and unsigned should get assigned but look at the storage proof page... look at how it actually demonstrates the fib spiral vs hash visualization as well as how we do the inverse y^2 or whatever so the small used band doesnt make a striped billiard ball effect — 2026-09-25

> So you're telling me that my coords are legitimately bubbled up from the actual unicode/ducet coordinates and composed across all tiers to the top? — 2026-08-09

> The geometry is NOT conventional AI nearest neighbor or shit like that... "king" wont fall close to anything other than things that physically are similar... ring, ding, sing, King, etc... The semantic web is what tells you what "falls close" because the glicko-2 tugs — 2026-09-25

> The S3 and such is my embed but that holds no semantics... it gives me frechet, shape comparison, intersections, etc... I get to replace o(n&2) brute force with o(log n) + o(k) indexed lookups and A* pathing... — 2026-09-25

> angular/frechet/etc... fuzzy searches, voronoi, etc from my s3 "embed" and the semantics are the "graph" (spider colony web) — 2026-08-17

> "neighbors" means a lot of things in Laplace... Angular/Frechet/Hausdorf/Karcher/etc... — 2026-08-24

> the partitions likely are just box partitions instead of sphere (s3...) — 2026-09-25

---

## Physicality trajectories and the bit-packed mantissa

> i bit-pack the hashes into the mantissa coordinates of the entities for the physicality trajectories to exploit the b and r tree indexing... — 2026-08-09

> "bit-packed mantissas with metadata" is not a flaw... it's a design choice to exploit b/r tree indexing... — 2026-08-25

> also consider the mantissa bit-packing of the physicality trajectory geometry... each point in the geometry is the ID hash and metadata and not the real 4d coordinates (though the coord centroid is) — 2026-09-25

> Do you see how these are the same queries we're going to use for model export and such? How and why the physicality trajectories are so essential and paramount to proper fast operation? How exploiting the B/R tree indexes with bit-packing the mantissas with the blake3 ids? — 2026-09-08

> The trajectory is the bit-perfect merkle-dag container from trunk to leaf, so we have its exact structure, sequence, occurrences, tiers, structures, etc and we have all forms of attestations and witnessing on top of it... the shape and structure and direct connections tell us alot before we even get into semantics, no? — 2026-08-24

> Laplace stores paths as trajectories, as a merkle dag, with deduplication and run length encoding... — 2026-09-05

---

## Precedes, contains, co-occurrence: read from trajectories, never recorded

> You are aware and realize that precedes, contains, etc doesnt NEED to be attestations and can be produced from the physicality trajectories in all cases? Model ingestion, text ingestion, etc? — 2026-08-24

> How do you think calculating precedes, cooccurrences, contains, etc is done without explicit records from the trajectories? — 2026-08-25

> Do you not see how co-occurrences, contains, precedes, etc is all queried from the trajectories in microseconds instead of stupidly recording billions of records? — 2026-08-30

> precedes, cooccurrences, contains, completes to... those are all things the physicality trajectory gives you while exploiting indexing and giving you the exact IDs because we bit pack the mantissa — 2026-09-07

> "physicality.trajectory"... i dont need continues_to, completes_to, or any of those when i properly store data... even data i ingest from models that Laplace eats as fucking snacks — 2026-09-03

> We O(1) perfcache a point for "Sherlock" and have it intersect with all entities that contain that point and we now have every occurrence of that specific token across the entire corpus — 2026-08-08

> "Captain Ahab" is a precedes with a gap of one... no? Captain precedes ahab with a gap of 1 which is something i can search/filter/etc... I can say "Show me everything that is two hops from this entity"... "Find anything where this entity is within this distance of entities from this other entity" … Everything needed for conventional AI can be queried via SQL with indexing in micro/milliseconds — 2026-09-12

> Why do i even have the physicality trajectories in the first place? the containers of function? — 2026-09-08

---

## O(tier): trunk-to-leaf dedupe, leaf-to-trunk insert

> o(tier) lets you do dedupe checks from trunk to leaf... inserts are leaf to trunk — 2026-09-26

> its the pre-generated coordinates and such that enable us to recreate any form of content before ever touching the database... Did you miss all of the o(tier) trunk to leaf and leaf to trunk operations? How do you think we do the deduplication checks across the merkle dag? — 2026-08-25

> if i have bible verses (say John 3:16) ingested already, the o(tier) dedupe should catch the already ingested parts, overlap, deduplicate, add witness or whatever, etc. — 2026-09-01

> same content = same hash... deduplication, run length encoding, etc are the name of the game... physicality trajectories, attestations, relations, tiers, types, etc... o(tier) leaf to trunk, trunk to leaf... We're not reproducing unicode imports bit perfect but we might ingest content that can be exported bit perfect... example sentences, descriptions and definitions, etc... — 2026-09-20

---

## Perfcaches

> t0 cache is the anchor cache (dont name it that but thats what it is)... its what everything boils down to in ALL cases... — 2026-08-18

> Unicode perfcache eliminates round trips, computation, etc to make deconstruction and reconstruction lightning fast... UAX29 not having to touch the db because we have mmapped the raw atoms means we can break down or reconstruct in microseconds even for megabytes of data... Thats what the O(Tier) trunk to leaf and leaf to trunk does for us, along with deduplication/run length encoding/etc and the fact that everything is a merkle dag/ast/etc... — 2026-08-28

> Are codepoints going to be the only modular perfcache? What about chess? What about images? Audio? Video? — 2026-08-28

> the same decomposer creates both the perf cache and the db records from the same source to ensure bit perfection since unicode is versioned — 2026-08-25

> one decomposer that produces the code for the perfcache... we build that perfcache and persist it unless we have to change it (the coords change or something)... and it stays a permanent artifact that we can install easily... mmap'ed perf-cache produced by the same decomposer that populates the database... not "lazily throw records into the db from a reduced scope perfcache" — 2026-09-20

> i do record unicode for the UI and stuff like that but for the actual operation, the perfcache mmap'ed and hot loaded reduce IO/round trips/db calls/etc... Why would i ever actually need to query each codepoint ever? — 2026-09-08

> repeated witnessing and observations of fixed deterministic states (hence the perfcache) — 2026-08-31

> what do you think the highway mask and such are for? What do you think the perfcaches are for? — 2026-09-25

---

## Attestations, witnesses, observations, trust

> "An attestation may identify who asserted an observed use" No it fucking cannot... that is a witness, not an attestation — 2026-08-30

> The real mechanism is that we literally have trusted sources providing attestations to what we witness and observe... trusted sources say that a dog is a noun... — 2026-09-02

> Are user prompts things that can have attestations come from them? Are they more than observations and witnessing of things that we attest from more trusted souces or decomposers from content that gives us those attestations? — 2026-09-07

> The model itself only tells us that a dog is a noun if we shake the model and throw prompts at it... Laplace knows this from trust/confidence/etc... User prompts are less trustworthy than academically curated datasets... nouns are more important that stop words in this given scenario — 2026-09-05

> We seed curated datasets that attest to words, sentences, etc... so when we ingest a prompt that says "The cat sat on the mat"... we have underlying factual higher trust information to make sense of what the fuck is said instead of just trusting that everything blended together came to cohesive responses — 2026-09-25

> this is how it shows the difference between user prompts vs seeded content... Users wont be ingesting AI models or Wiktionary or Tatoeba... I am... They will just be talking to it — 2026-08-17

> per atom... attestation? That comes from what other than unicode? What other source could possibly attest at tier 0? — 2026-08-30

> We observe things that happen to be from ISO, CILI, Wordnet, etc... because im seeding them explicitly but the content? If we record "The cat in the hat" (the whole book)... are you sourcing dr seuss to the letter Q? — 2026-09-01

> that document ingest … thats usage/examples/observations without attestation... this is what helps you join and link... cooccurrences, precedes, contains, etc... — 2026-09-08

> Laplace also … can know about it but it won't favor it... [hate speech and negative datasets] so Laplace knows what bad words are, what racism is, etc and that its bad, scores negatively, gets avoided, reduces ratings — 2026-09-03

> HAS_PART ... is that how its recorded from the source content? We treat curated seed content differently from normal user content... — 2026-09-25

---

## Consensus and Glicko-2

> we have glicko-2 which is based on source trust, the relation type "importance" (is a noun more important than a stop word? in what cases?), the tier, containment, etc... — 2026-08-24

> there is a conflation between what really has attestations/consensus/etc and whether or not we're duplicating or actually forming a consensus... how many attestations are one-off that match only one record that should match a lot more? — 2026-08-17

> the chess player ratings are not glicko-2 scores... Why anyone appears ever higher than magnus means you fucked the query(ies) — 2026-09-07

> the ratings/mu/confidence/etc from the glicko-2 appears to still be completely fucked, particularly for chess because of a conflation of Laplace Glicko-2 vs chess ELO... — 2026-09-04

---

## Relations: a small set of primitive meanings

> HAS_PART, SYNONYM, etc... even with direction... how many different options does human communication really have? — 2026-09-25

> The fact that you're wanting to hardcode all of these without setting up a real bitmask and such is beyond me... — 2026-09-25

> the main thing is that "This source says this is (or isnt) this"... "Hot is not cold" "Hot is antonymous with cold" — 2026-09-25

> how many of those bits imply or mean the same thing, slightly differently or with some kind of condition or additional attribute? im thinking category/sub-category type thinking where masks have a complementary mask and it means various things based on the type? — 2026-09-26

> Look at all of the numerical identifiers that just require a language flag that enables me to bubble up to the numerical highway and bubble back down to any other language — 2026-08-26

> the ILI for 27274 isn't the render or label... it's the ILI identifier for "dog" with the english flag... — 2026-09-25

> Laplace bubbles up to numerical identifiers like ILI/synsets/frames/etc... — 2026-09-08

> "the universal translator" — 2026-08-26

---

## Decomposition and the generic, recipe-driven ingest

> I need to ensure that i actually have "One generic decomposer pipeline with 'vendor implementations' (my specific Decomposer<TSpecific> implementation)" that actually handles in a unified manner single file vs multiple file... — 2026-08-18

> Why do you not call Decomposer<TRecipe> that uses runtime configurations of some kind? — 2026-09-20

> generic... ingestion pipeline... meaning you don't fucking code anything but the generic parsing and processing — 2026-09-20

> generic decomposer, generic ingest pipeline, recipes... for a fucking reason — 2026-09-26

> You do realize by generic ingestion pipeline that is recipe driven, my next task will be for you to make recipes for ISO, then for ... until we have them all, right? — 2026-09-20

> in /opt/laplace/external, we have most of the treesitter grammars and i think we started to make our own... recipes are a form of that... They are "Laplace Grammars" — 2026-09-20

> Users curate recipes and we produce them from ingesting models... — 2026-09-08

> I intend for us to manufacture our own stock default recipes for each of the sources in /vault/Data … if its the gutenberg websters unabridged... it follows a format we should be able to properly decompose... UAX29 but for semantics... and modality agnostic — 2026-08-31

> We honestly do need generic reusable "general digital content" decomposers for production for user content ingestion since this is modality agnostic — 2026-09-05

> if we can make a Turtle parser and wire the recipe to use that... it becomes just another "normal digital content" decomposer with extra framework for ability to pull attestations via recipe — 2026-09-25

> Do i have … proper approaches for actual proper real decomposition in a generic fashion that will properly and accurately extract and associate all of the attestations across the appropriate tiers instead of blindly blanketing all attestations across tiers? (City/State/Zip spam example) — 2026-08-28

> the main issue is that no file ingestion has a proper ingest... theres no trunk node that i know of that trickles down to the content node, metadata node, etc... — 2026-09-05

> Take UD for example... thousands of files... do you think i want a bunch of fucking iterative stages going back and forth or do you think i want properly orchestrated sequenced well flowing operations? We thread, parallelize, optimize, etc... SIMD/AVX/VNNI/TBB/MKL/Eigen/etc... the fucking works... we do that per operation but we also consider all operations as a whole... UD is a fucking source trunk entity... each file are their own trunk entities under UD, each record attested/sentence/word/etc from each file gets under that... the physicality trajectories are what link content to their sources... when we ingest wordnet and words and sentences collide? Guess what now has two fucking parent nodes instead of one?... When we ingest though, we process the file, stage the records and fucking stop there... we ingest another file? It attests the same thing, adds witness count? — 2026-09-26

> decompose and stage all the records necessary... the whole conversion from that source content into laplace records, deduplicated and all of that but just the records... we have all the entities, attestations, etc... and then we fucking batch that into the real database... gigabytes should take fucking seconds — 2026-09-26

> Why can't we have a staging table for entities? entity_staging... we put records in there, they collide and self-deduplicate... they show the decomposition going into proper structure... we can quickly and easily process the entire fucking dataset... then we intelligently sequence how we process the tables in proper operation — 2026-09-26

> Wheres the real set-based operations, merges and no stupid on conflict checks? Where's the o(tier) and perfcaches reducing round trips and enabling calculation on client-side for the deterministic actions? Where is the proper order of operations? Why do you delay operations like the consensus drain? — 2026-09-26

> A structured value decomposes: "069758980-n violates it? what is the -n? What does it mean? What does it attest? is it really part of the ID and not some hidden meaning?" — 2026-09-26

> The idea is that normalization and stuff is (within reason) acceptable from curated seeded sources that we handle special like UD, wiktionary, Wordnet, PropBank, Framenet, Semlink, CILI, etc... Wordnet indicating a noun differs from UD but means the same... there are variations and adjustments that happen for stuff like this for different languages but many of them are just replicated and differ for a given language — 2026-09-25

> the XML files should be sources of truth and we use whatever additional files contain useful information that the main xml files dont have... UAX29 for example has tests, theres stuff about confusables, securities, etc — 2026-09-20

> the source data … are in /vault/Data/... and models and code and other stuff are in /vault/models/ — 2026-09-05

> I want the current/updated versions, not legacy/old/broken/deprecated versions [of each dataset] — 2026-09-03

> Why would we do stupid O(N^2) data generation when we have the data to ingest directly? — 2026-08-30

---

## The forward pass: the SQL transformer

> A "conversation" is just like a chess game, no? You have a conversation/session like a game... each chat is its own sentence or whatever... gets its own entity... but gets added as a point on the trajectory of the conversation... — 2026-08-08

> "GPU inference — Indexed relational/native execution on CPU" This is missing the A* pathing/glicko-2/etc... hops and fan-outs — 2026-08-08

> "How does lightning work?" can literally break down and do a forward pass for each word and form its own form of cosine similarities, dot products, etc by doing relational db queries with native c/c++/spi for recursion, loops, CTE, cursors, RBAR, etc... Everything we need for AI inference is available through the attestations, relations, witnessing, observations, etc. and most importantly the physicality trajectories — 2026-09-02

> we break down the prompt or portion of the dag we're using as the prompt since prompts are themselves part of the merkle dag... we break down to its constituents, check observations, compare attestations, etc... It's the exact same operation as conventional AI using QK, KV, VO, etc. — 2026-09-08

> the sql transformer, AI/ML queries, etc will need to be able to filter from observations... each word that we observe from a prompt for example... which attestations are present for them? Lets check occurrences we've witnessed for each word, what other attestations? ok, lets filter scope and context for the attestations/relations/etc... — 2026-09-08

> physicality trajectories give you the exact ordinal of every usage/example/etc and the objects themselves, their constituents and give you direct links to their semantic webs ... precedes, gap, co-occurrences, etc... — 2026-09-25

> the physicality trajectory alone gives most of the conventional AI q,k,v,o,gate,up,down,norms,etc... every single computation of conventional AI is possible here with the schema and tables and structure i have invented... the S3 and the indexing and stuff are what also enable it... — 2026-09-08

> Do you think its just a lazy array of words? Merkle Dag... AST... UAX29... — 2026-09-25

> That's the key to the invention... "Spider web colony... Tug one strand, what strand from what spider tugs back and how hard? — 2026-09-08

> The attestations are the facts... capital("France") returns "Paris" but dot product, probability, softmax... you can't produce 100% but why not 99.99999999% and some 0.000001%s? — 2026-09-25

> The idea was that in Laplace, my dot products dont point to empty space where we get ANN and pick the highest probability based on distance or whatever... The idea was that the dot products would essentially point exactly... "the capital of france is" in Laplace would point exactly to ONLY "Paris" — 2026-08-08

> sessions with user/tenant/turn/session/etc metadata... Laplace literally never forgets and has an infinite context window because Laplace IS the context window... Laplace is an AI that should be able to use any part of its own knowledge as the prompt itself and reason about it... This is why i dont give a shit about the dot products or cosine similarities... i fucking generate those at export — 2026-09-04

> How with physicality trajectories … i have everything from stochastic gradient descent, reinforcement learning, cosine similarity, dot products, probability, etc... but without all of the flaws of conventional AI — 2026-09-25

> My forward pass is an invention that is well documented and should not indicate that it gives two fucks about UD explicitly but instead cares about its instruction sets, personality firmware, godel engine, etc and works from the attestations as a whole, consensus, witness, usage, examples, observations, physicality trajectory, frechet, etc — 2026-09-26

> my documentation … CLEARLY outline exactly how the semantic walk should work in Laplace... its sql querying with recursion... hence the native c — 2026-09-04

> How about getting one forward pass to work properly before pretending like you're chatting across turns? Get turn 1 working first — 2026-09-07

> display labels should be a final operation, not during the recursion and heavy lifting — 2026-09-08

> "Laplace speaks unicode. It renders languages" meaning we shouldn't be caring about actual output characters until we're rendering/labeling/etc — 2026-08-25

> "unresolved entity" or the hash itself are not proper output... the text/label/etc aren't what we search on... every entity has an id, coordinate, trajectory, centroid, etc. — 2026-09-05

> this isn't … fake latin/english regex — 2026-09-05; "What separators are in japanese?" — 2026-09-06

---

## Gödel engine, OODA, personality firmware

> The forward pass for laplace is meant to use the godel engine, ooda loops, and my idea of personality firmware that detach operation and generation and such from the data itself... Humans know guns exist and they use them to kill each other but that doesnt mean every human will use a gun to kill someone (morbid but impactful example)... AI/Laplace/etc is a tool and the firmware and personality use that tool... the godel engine, forward pass, etc is that personality... "Personality Firmware" since i basically am mapping out the individual steps of the conventional GPT to semantic instruction sets — 2026-09-25

> "intent classifier, template responder, or token Markov chain" This is what the purpose of the Instruction Set / ISA / firmware was supposed to represent... "Personality Firmware" does denote all of those things... how creative is it? How by the book is it? Does it care? (Random top-k? top-1? low hop count?) — 2026-09-04

> "compile failed... negative attestation... next iteration ... oh wait, we tried this… it failed hard... abort, rethink... try again... compile... positive attestation... next step... next step... next step... This is what my godel engine is for — 2026-08-09

> this is the nature of the instruction set... there is an orchestration layer... Something that takes prompts and responds as an LLM and with given permission levels or isolated environments, control and actionability — 2026-08-31

> BELIEF / RISK / ACTION … "What do I believe?" ≠ "What could go wrong?" ≠ "What am I authorized to risk?" — adopted by the inventor as "another pretty fundamental Laplace separation", 2026-08-17

> The Gödel Engine … "use incompleteness as a discovery signal" … "What new structure would explain why I couldn't answer correctly before?" — text the inventor brought into the session as describing his Gödel engine, 2026-08-24

---

## Conventional models: ingestion, round table, export

> An AI model being ingested isn't just recording the raw weights and doing stupid sloppy lazy stuff... glicko-2 acts as that weight, so when an AI model relates "king" to "queen" with this given weight/intensity/score/etc... it translates to a laplace set of records, attestations, scores, etc... We're not throwing sentences at the conventional AI model and recording what shakes out... we ETL scrape the model for its weights encoded to laplace records... no raw weights or binary blob storage... we compute the knowledge into laplace and the SQL transformer can query those records for Q, K, V, O, etc... — 2026-09-25

> We literally should be able to ingest TinyLlama and start querying with SQL for the Q, K, V, O, etc just like a forward pass but... we have everything hashed, indexed, pathed, etc... i know what layer in which head and what weight and what token and what index, etc... i can do filtered searches instead of brute force just on a conventional model... the attestations, physicality trajectory, observations, etc all enable that same functionality so when put together they aggregate knowledge and capability meaning that Laplace assimilates knowledge — 2026-09-25

> We aren't storing raw weights or other stupid lazy binary storage... tokens map to indexes which map to tensors/heads/layers/etc... Laplace stores paths as trajectories, as a merkle dag, with deduplication and run length encoding... same content = same hash... things deduplicate and we only need to record a certain number of the actual attestations from an AI model, whereas the trajectories from the model with glicko-2 and all of that? Thats what replaces the conventional transformer — 2026-09-05

> placing AI model tokens as anything other than text entities is rape and sabotage — 2026-09-25

> Laplace also doesnt need to record everything... Consider the lottery ticket hypothesis in laplace without resorting to a … basic MVP noise floor or top-k filter... real detection of where the floor of a model is — 2026-09-25

> the shape of the tensor gives us a narrowed scope of what it could be... the name helps but doesnt determine since that isn't specification... Think of attention, convolution, diffusion, mlp, etc... — 2026-09-25

> Think of the recipe for different frontier model providers... llama, qwen, deepseek, Dense vs MoE, Rope/lora/etc... It's all different but... is it? Safetensor is safetensor... gguf is gguf... we want safetensor since gguf/awq are the mp3 of AI models... — 2026-09-25

> this is consensus... not merging... Layer X, Head Y touches these tokens, does this, handles that … from each model... "King" from Qwen3 = "King" from Llama4 Maverick... — 2026-08-08

> the "round-table" meant that the ingested models all work as a consensus and give one answer instead of N answers that X gets picked from... No adjudication necessary — 2026-08-08

> I dont require similar architectures at all... TinyLlama, MiniLM, Jina Rerank/Embedding, Flux, Flamingo, Graphite, etc... — 2026-08-08

> If i ask what layers and heads "King" appears in, it tells me... When i ask "How does this model differ from that model?" I can get an answer... "How does layer X head Y differ from model to model? Do they have correlations? eg. Layer X head Y is Layer N Head M from this other model" — 2026-08-08

> when i ingest ONLY unicode/iso and an AI model and then go to export a GGUF... i export explicitly that model... but clean... No training artifacts... No gradient jitter... i can query that model as that model... ingest a second model... same thing but now im querying against two models... no judge... a round-table — 2026-08-08

> We literally have the ability to custom tailor our layers/heads for model export based on attestations, relation types, tiers, content... but ALSO the trajectories — 2026-08-08

> nouns... bam... something we can figure how to produce and generate cosine similarities, dot products, our own form of softmax/probability, etc... THAT is the model export... the model export is reinventing the gradient descent... reinforcement learning... etc... — 2026-09-08

> the sql transformer … was initially meant to denote exactly how we pull information properly for q, k, v, i, gate, up, down, norms, embed, etc that we GENERATE at runtime for an export — 2026-09-07

> This is also how we pull information for the model export that we then massage and generate to export a GGUF/Safetensor/etc — 2026-09-05

> model ingestion … isn't required and only adds value — 2026-09-05

---

## Chess

> what is a move? How does a move relate to a board position? is a move a board position or a transition from one position to another?... when we observe an outcome, do we need to track it or is it really the physicality trajectory containing that move and us just checking the outcome for the game that trajectory is associated with? — 2026-08-19

> a queen... can be on E4... and move to E6... or a pawn, or a knight … this move transitions this board state to that board state... repeated witnessing and observations of fixed deterministic states (hence the perfcache) — 2026-08-31

> Why isnt a board state stored as a physicality trajectory but is instead a long string sentence masked as a laplace entity? — 2026-08-20

> a single 8 bit mask would work [for castling]... center bit is where the king is, left and right are where it castles to... in all cases, even with chess960 — 2026-08-20

> We have openings... starting games... syzygy should be "end games"... sequences and trajectories that deduplicate and store far more effectively and overlap — 2026-08-31; "opening frechet and syzygy closing frechets to map to games" — 2026-09-03

> "We havent seen it before" is no excuse for a substrate that replaces AI — 2026-08-31

---

## Code

> Why do you think i made this a merkle dag? Everything is an AST... why do you think i have all these treesitter grammars and why do you think Laplace has its own custom ones i started making? — 2026-08-26

> if you properly and accurately ingested the repository … laplace should be able to clean it if we had enough coding data... parquet files from tiny codes, whatever i have downloaded from stackv2... we have coding corpus — 2026-09-08

> Why would i care about old repos if not to have Laplace learn from it? — 2026-09-11

---

## Execution grain

> Laplace is C/C++/SPI for all logic/heavy lifting/shared code/etc where SQL and C# and such were orchestration layers — 2026-09-01

> the mandate was always native C for the heavy lifting with C# and SQL as the orchestration layers — 2026-09-20

> Isn't the rule to reduce the record set early and often? — 2026-08-17

> We're literally developing customizations that expand upon PostGIS and adding 4d … so why can't we go balls out with it and really make this thing fly — 2026-08-24

> CpuTopology, MemoryTopology, etc exist to use these libraries/tools/etc [Eigen, Spectra, Intel oneAPI] to intelligently operate across hardware, maintain bit perfection — 2026-08-25

> why do you require python to run Laplace? — 2026-09-08

> modular meant that laplace is modular — 2026-09-19

---

## Product

> Patreon does not become Laplace. Patreon simply becomes an external witness that says, essentially: "This account is currently entitled to this membership tier." Laplace can then apply its own versioned entitlement policy to that fact. That means payment does not become knowledge, ownership, governance, or authority. … support provider → verified entitlement → Laplace privileges — Patreon post 2, 2026-08-26

> If i can't export a functional model, i cant sell that product... if i can't chat with Laplace, i cant sell that product... if laplace doesn't code, i can't sell that product... if laplace doesnt work as an openai endpoint to function just like conventional AI or through the MCP, same thing — 2026-09-07

> /opt/laplace is the deployment surface... the local repo in my home directory? Thats where we run and test locally — 2026-09-20

> laplace-runner is the account that owns everything — 2026-09-05

---

## How to read the repository's documentation

> any and all documentation is to be treated as context to glean and for you to infer and realize what really needs to be done while assuming its potentially stale or deprecated or superseded, etc... "Trust but Verify" in all aspects is the name of the game — 2026-08-17

> the repo is in an incredibly unstable state where documentation, comments, instructions, markdown, etc is actually sabotaging and getting in the way of progress instead of actually helping make progress and that contamination is mixed in with my documentation on how this invention is supposed to work, what it is, what its for, how its going to change the world — 2026-08-17

> comments, status reports, documentation that has progress instead of implementation details … that prose has you focused on tests, gates, simulations, and other fake effort that never delivers — 2026-09-26

> The work is you not having to re-educate yourself like this ever again and for you to inherently know and understand what Laplace requires, what its for, what it is, what it contains, what it does, how it works, the intricacies, the exploitations, the reinventions, the o(tier), the same content = same hash, the deduplication, the physicality trajectories, the centroids, the hilbert curve values, the coordinates, the bit-packed mantissas, etc... — 2026-09-17
