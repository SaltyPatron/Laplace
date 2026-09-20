# Knowledge authority, governance and security

Laplace uses one canonical knowledge world, but not one universal permission set.

Knowledge, authority and compute are separate axes.

~~~text
KNOWLEDGE
  what canonical structures, observations, testimony and calculations exist

AUTHORITY
  which parts of that world a principal may use, and for which operations

COMPUTE
  how deeply/broadly one operation may search the authorized world
~~~

This separation is required for enterprise isolation, schools/curricula, commercial knowledge packages, personal/private knowledge, honest abstention and security.

## Knowledge packages

A knowledge package is an authority manifest over shared canonical state. It is not a smaller model and does not need to duplicate its content.

Examples include Grade 7 mathematics, Formula 1 engineering, an employer's private project corpus, a department's internal procedures, a personal/private history scope, or a temporary incident-response package.

A package may contain other declared scopes/packages. Inheritance follows explicit authority/package relations, not arbitrary semantic graph edges.

Buying Formula 1 knowledge must not accidentally grant unrelated restricted aerospace knowledge merely because both domains connect through fluid dynamics.

## Knowledge package versus cache profile

A knowledge package may ship or recommend a matching perfcache deployment profile, but the two objects are intentionally different.

~~~text
knowledge grant:
  what this principal may use

cache profile:
  what this device/process keeps resident or precomputed
~~~

Examples:

~~~text
Grade 7 package
  may recommend:
    unicode/basic-text
    number/common
    curriculum/hot-concepts

Formula 1 package
  may recommend:
    engineering/hot-structures
    image/team-palette
    telemetry/hot-numerics

speech assistant deployment
  may map:
    unicode/ascii-or-selected-script
    audio/speech-band
    hot audio windows/phrases
~~~

Removing a cache module does not revoke knowledge. Revoking a knowledge grant does not require rewriting the canonical cache identity of shared public structures, though runtime enforcement must ensure unauthorized cached records cannot enter COUPLE.

This separation lets deployment/storage economics be optimized independently of authorization.

## Capabilities

Scope and operation rights are independent.

~~~text
DISCOVER  know that a scope/entity exists
INSPECT   retrieve explicit content
SEARCH    locate members
COUPLE    allow state to influence cognition
TRAVERSE  follow authorized relations
DERIVE    calculate new knowledge
REALIZE   disclose/render a result
PERSIST   save derived state
EXPORT    take state outside the current boundary
EXECUTE   cause an external action
DELEGATE  grant authority to another principal
~~~

A user may be allowed to reason from private material but not export it. A security analyst may discover a restricted scope but not inspect its contents. An automated agent may propose a deployment but not execute it.

## Effective authority

~~~text
tenant/world boundary
∩ principal identity
∩ role and relationship authority
∩ knowledge-package grants
∩ active caller-selected scope
∩ capability grants
∩ kernel/org/user governance
= effective operation boundary
~~~

The effective boundary is part of execution state and receipts.

## Governance does not rewrite knowledge

A fact remains knowledge even when acting on it is forbidden.

The system must not implement governance by deleting knowledge, corrupting standing, or pretending an entity does not exist.

~~~text
knowledge/evidence
+ requested operation
+ effective authority
+ governance
-> allow / deny / abstain / require stronger authority
~~~

Standing answers epistemic support/uncertainty. Permission answers authority. They are not the same variable.

## Honest abstention

Because authority is explicit, Laplace can distinguish unknown, unsupported, ambiguous, resource exhausted, known but not authorized to couple, authorized to couple but not realize, authorized to inspect but not export, and authorized to propose but not execute.

The public explanation is itself governed. Saying that a secret exists can leak information, so DISCOVER is a real capability.

## Enforcement must precede redaction

A forbidden source must not influence COUPLE and then merely be removed from final text.

~~~text
RESOLVE  principal + grants + policy
COUPLE   authorized cognition inputs only
DERIVE   calculation authority
SELECT   act eligibility
REALIZE  disclosure authority
PERSIST  write authority
EXPORT   boundary-crossing authority
EXECUTE  external-action authority
~~~

This makes the execution receipt explain the actual permitted mind used for the operation.

## Red Spear / Blue Shield / White Judge

Red Spear performs authorized adversarial exploration: privilege/scope escalation, cross-tenant leakage, confused-deputy paths, stale or transitive grants, inference leakage, restricted derivation, and export/execute bypasses.

Blue Shield enforces effective authority at the actual cognition/action boundaries.

White Judge performs explicit deterministic adjudication over policy, authority, provenance and evidence. It is not a hidden LLM that votes on Red versus Blue outputs.

## Schools and "AI for every human"

A person's effective mind can be composed rather than separately trained:

~~~text
shared Laplace world
+ institutional/licensed knowledge grants
+ private organizational/personal knowledge
+ witnessed personal experience
+ governance/firmware
+ active scope
+ compute envelope
~~~

A school can grant curriculum packages by grade/course. An employer can add/remove project access. A user can activate only the portion relevant to one task.

"I know kung fu" is therefore a literal product operation: grant or materialize a governed, receipted portion of the existing world.

## Billing is orthogonal

A principal may own a broad knowledge package and request a cheap surface scan. Another request may use the same package but purchase deep/wide analysis.

More compute never grants additional knowledge authority. Lower compute never means a deliberately dumber model.

See docs/BILLING_PLACEHOLDER_MIGRATION.md and docs/guides/machine-cost-analysis.md.


## Current implementation owner

#1708 owns first-class knowledge packages/grants/capabilities, forward-pass enforcement, honest authority receipts, and Red Spear / Blue Shield / White Judge execution.
