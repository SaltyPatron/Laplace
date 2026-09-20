# Laplace capability synthesis

This document is a binding synthesis of consequences already implied by the invention laws in `INVENTION.md`, `INVENTIONS.md`, the forward-pass/ISA specs, and current inventor direction. It exists because isolated implementation rules, tests, gates, historical audits, and issue bodies have repeatedly hidden the actual product behind its proof machinery.

A test, gate, benchmark, issue, plan, audit, branch, or receipt proves or tracks a capability. It does **not** redefine the capability downward. When a local document conflicts with this synthesis or the higher-authority invention, repair the local document and implementation rather than narrowing the invention.

## One world, three independent boundaries

Laplace keeps these axes separate:

```text
KNOWLEDGE
  what canonical structures, observations, testimony and calculations exist

AUTHORITY / GOVERNANCE
  who may discover, inspect, couple, traverse, derive, realize, persist, export,
  execute or delegate which parts of that world

COMPUTE
  how much work one operation may spend: hops, fanout, candidates, providers,
  trajectories, geometry, calculations, CPU, memory, I/O and output
```

Commercial tiers must not be implemented as progressively less knowledgeable models. The same admitted world remains the world. A request may have a smaller compute envelope, and a principal may have a narrower authorization scope, but neither rule is permission to manufacture a dumber Laplace.

### Knowledge packages and effective mind

A knowledge package is an authority manifest over the shared world, not a duplicated mini-model. It may grant a subtree/scope, source/context set, private tenant corpus, curriculum, licensed domain, or other explicitly governed collection.

Effective operation scope is conceptually:

```text
tenant/world boundary
∩ principal/role/relationship grants
∩ purchased/institutional/private knowledge grants
∩ active caller-selected scope
∩ kernel governance
= effective knowledge/operation scope
```

Grant inheritance follows explicit authority/package relations, not arbitrary semantic relations. A Formula 1 package may include its declared engineering subtree; it must not accidentally grant missile knowledge merely because both connect to fluid dynamics.

Capabilities are separate from scope. Useful operation rights include:

```text
DISCOVER
INSPECT
SEARCH
COUPLE
TRAVERSE
DERIVE
REALIZE
PERSIST
EXPORT
EXECUTE
DELEGATE
```

The current tenant owner/admin/member roles remain workspace administration. Knowledge authority is a richer RBAC/ReBAC/capability layer over the substrate.

## Governance does not erase knowledge

Knowledge that something exists, is dangerous, illegal, private, restricted, harmful, useful, uncertain, disputed, or benign remains knowledge with provenance. Governance must not pretend the fact disappeared.

A policy decision is therefore not:

```text
delete dangerous knowledge
→ model cannot know it
```

It is:

```text
knowledge + evidence about consequences
+ principal authority
+ kernel/org/user policy
+ requested operation
→ allow / deny / abstain / require stronger authority
```

An abstention can be honest and receipted: relevant knowledge may exist while the current execution lacks authority to couple it into cognition, derive from it, realize it, export it, or act on it. Even disclosure that restricted knowledge exists can itself require `DISCOVER`.

Standing and permission remain distinct. Evidence consensus answers how strongly a proposition is supported; authority answers who may perform an operation. Permission is not identity and is not a popularity vote.

## Red Spear, Blue Shield, White Judge

The cybersecurity model follows the same authority substrate.

- **Red Spear** performs authorized adversarial exploration: privilege/scope crossing, inference leakage, stale grants, confused-deputy paths, unsafe derivation/export, cross-tenant leakage, and other boundary attacks.
- **Blue Shield** enforces effective scope and capabilities at the actual cognition/action boundaries. Forbidden knowledge must not merely be redacted after it has already influenced a result.
- **White Judge** is explicit policy/authority adjudication plus receipts, not a hidden judge model. It determines whether a requested transition is lawful under declared authority, governance, provenance and evidence.

All three operate over the same explicit world and must preserve WHY/WHY_NOT receipts.

## Compute depth is the product tier

Laplace sells measured cognition over the same intelligence, not model branding.

```text
hops   = how deep the web may be pursued
fanout = how broadly responding strands may be pursued
```

A sophisticated question may be cheap when strong evidence is near the root. A simple question may be expensive when the caller requests exhaustive depth/breadth. Presets such as quick/standard/deep/custom are presentation over explicit execution envelopes.

The economic loop is:

```text
request
→ compile operation program
→ EXPLAIN semantic + physical work
→ calculate/estimate machine work
→ reserve a hard ceiling
→ execute under counters
→ receipt actual work
→ reconcile/refund unused reserve
```

Semantic work and implementation waste remain distinguishable. An inefficient implementation that burns excess SQL/SPI/PInvoke cycles is a defect, not permanent pricing authority.

## Reusable numeric/scalar structure

Numeric values follow the same content-addressed reuse law as words, AST subtrees and chess positions.

~~~text
0.34567
-> ['0','.','3','4','5','6','7']
-> canonical scalar composition/root
~~~

The root is created/reused as content. Audio samples, image channels, model coordinates, measurements and other uses add typed occurrences around that root. Repeating an exact amplitude a million times does not record the scalar a million times.

The current dense 0..255 number perfcache is only an accelerator for common integer roots; it is not the numeric universe. Long finite constants such as pi prefixes are just wider ordered compositions.

## Deterministic machine-cost derivation

Laplace's decomposition law applies to programs and machines as well as documents.

```text
source / executable / JAR / bytecode / object/container
→ exact syntax/container recovery
→ universal typed AST / bytecode / machine-instruction structure
→ control-flow + data/dependency structure
→ explicit execution-count variables
→ target ISA
→ target microarchitecture/scheduling model
→ memory/initial-state assumptions
→ clock
→ resource-constrained cycles
→ machine time
```

Unknown loop counts, cache state, branch history, scheduler interference, I/O and other unfixed quantities remain symbolic/conditional instead of being replaced by benchmark averages.

A measured execution is a witness against the calculated model. It can validate, calibrate, or expose missing state; it does not replace derivable machine semantics with “it took N ms on my box.”

This machinery is also the long-term basis for Laplace preflight billing of its own cognition.

## Software construction is structural construction, not text prediction

Laplace should construct programs under an exact target grammar and semantic environment rather than probabilistically emit source text.

For code:

```text
requirement / obligation
→ exact target language + grammar generation
→ search known canonical structures first
→ reuse existing structure where possible
→ compose existing structures where possible
→ adapt the smallest close structure where possible
→ construct only genuinely novel AST structure
→ satisfy type/name/API/dependency constraints
→ realize source
→ compile/test/simulate/analyze
→ witness outcomes
→ repair the smallest divergent subtree
→ repeat until obligations close or WHY_NOT is produced
```

If the target is Bash, Bash grammar and runtime semantics govern construction. If the target is Zsh and no qualified Zsh provider exists, Bash is not an acceptable probabilistic substitute. The correct state is an explicit missing-provider/unsupported construction until the requested grammar is available.

AST validity is not sufficient for correctness; type/link/runtime/behavioral/toolchain evidence remains separate. The important change is that syntactic illegality does not need to be part of the generation search space.

## Duplicate code should converge

Content addressing makes exact duplicate code the trivial case: the same canonical AST subtree under the same recipe has the same identity even when observed in many repositories.

Duplicate detection then rises through increasingly stronger equivalence classes:

```text
exact bytes
→ exact canonical AST
→ normalized AST
→ equivalent control/data-flow shape
→ algebraic equivalence
→ behaviorally equivalent implementation under a declared contract
```

The system should continuously ask before creating code:

```text
Does this exact implementation already exist?
Can existing structures compose to satisfy the requirement?
Is there a close structure that requires only a bounded mutation?
Only then: is novel structure required?
```

This makes duplicate elimination part of generation rather than a cleanup phase. Same semantics implemented several times should become an explicit consolidation candidate; same apparent purpose with different semantics should expose the distinction and evidence instead of hiding behind names.

## Development attempts are witnessed repair trajectories

A development run is an ordered trajectory:

```text
application root R0
→ candidate mutation Δ1
→ compiler/test/runtime failures
→ mutation Δ2
→ different failures
→ ...
→ successful verified root R1
```

Compiler diagnostics, exceptions, test failures, static-analysis results, simulator outcomes, runtime observations and successful verification are typed witnessed outcomes attached to exact artifact/AST/toolchain/environment identities.

Failures are not discarded. They become reusable negative evidence. A later repair can avoid previously failed transformations when the relevant environment/structure matches.

## Cross-repository analogical maintenance

Once repositories are admitted as exact canonical structures, a repair episode can tug the known code world.

Laplace should be able to ask:

```text
Which other authorized repositories contain:
- this exact defective subtree?
- a normalized equivalent?
- a similarly shaped control/data dependency?
- a similar failure/repair trajectory?
- a close Fréchet/trajectory form?
- the same causal prerequisites under a different API/name/language surface?
```

Fréchet is one typed comparison plane, not the whole detector. AST shape, dependency/call structure, control/data flow, source/toolchain context, testimony, geometry and prior outcomes remain typed.

A successful repair may therefore surface other repositories that warrant inspection before they fail. Governance determines whether Laplace may merely discover/report, propose a patch, validate it, write it, merge it, or deploy it.

## The repository root is the application object

Laplace should logically construct the complete application state every time while physically recomputing only the changed recursive ancestry.

If one leaf changes:

```text
expression*
→ statement*
→ block*
→ method*
→ file*
→ directory ancestors*
→ repository root R1
```

Every unrelated canonical subtree remains shared with R0. The new application exists as a complete root even though only a tiny delta required new structure.

A checkout is a realization/export of that root, not the semantic authority.

This is why full-repository realization can be fast: reasoning/construction work follows the semantic delta; output materialization follows requested output bytes.

## Patch/deployment as a proven root transition

A patch need not be defined primarily as byte edits. It can be a transition:

```text
FROM application root R0
TO   application root R1

transfer = closure(R1) - objects already present
proof    = derivation + compile/test/analysis/runtime receipts
activate = R1
rollback = R0
```

Stateful schema/config/secret/external-system transitions remain explicit dependencies; immutable-tree semantics do not magically erase mutable-world obligations.

Deployment can itself execute as a governed Laplace program: resolve current state, verify authority, stage missing closure, perform required migrations, verify target state, activate, observe, witness, and roll back when the observed result violates the declared obligation.

## “AI for every human”

Individual intelligence does not require one separately trained model per person. A personal effective mind is composed from:

```text
shared Laplace world
+ licensed/institutional knowledge grants
+ private organizational/personal knowledge
+ witnessed personal experience
+ governance/firmware
+ current active scope
+ current compute envelope
```

Knowledge packages can therefore be purchased, assigned by a school, granted by an employer, delegated by role, or temporarily activated without copying opaque weights.

“I know kung fu” is the product verb: grant or materialize an authorized, receipted portion of the existing witnessed world.

## Implementation priority

When these consequences are in accepted scope, an implementation agent must implement and exercise the real capability. Tests, gates, audits, plans and issue edits are supporting proof/anti-regression work.

Non-success includes:

- stopping after writing prose/spec/issues when executable work is in scope;
- implementing a toy vertical slice instead of the common machine;
- replacing grammar/AST construction with token-ish code generation;
- replacing whole-application structural mutation with file-by-file regeneration;
- treating duplicate-code findings as reports without wiring reuse/consolidation into construction;
- hiding compile/test/runtime failures instead of witnessing them;
- using final-output redaction when unauthorized knowledge already influenced cognition;
- replacing measured compute depth/breadth with smaller/dumber model tiers;
- reducing machine-cost derivation to benchmark averages when exact/symbolic derivation is available.
