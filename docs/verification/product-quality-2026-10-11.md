# Product quality repair verification

Verified from HART-DESKTOP on 2026-10-11 (America/Chicago), against base revision
`0317ab33ce76d7bc7e11fecc145e0da7d8fe3ce7` and the installed substrate on hart-server.

## Repairs

- Text routes decode the encoded pathname once, retaining `%`, literal `%2F`, slashes,
  and Unicode. Malformed percent escapes remain readable. Matchup inputs follow navigation.
- Failed shared GETs release their deduplication key without creating an unhandled rejection.
- Topic panels report independent failures; successful panels remain usable. Catalog stages
  distinguish an unavailable read from a missing stage. Superseded responses are ignored.
- Home pulse waits for authentication discovery and stops after 401/403 until the identity
  scope changes. This removes the former four-second refusal loop.
- Placement projection preserves interior radius, including points below 0.02. Trajectory
  keyboard navigation requires focus and includes every ordinal in a packed run.
- Band facts retain incoming versus outgoing relation direction. Live `entropy` has an
  incoming `HAS_DEFINITION` from `熵`; the previous formatter incorrectly displayed it as
  outgoing. Executing the changed query against the live rows returns `HAS_DEFINITION ← 熵`.
- Staged ingestion exposes inserted entity, physicality, and attestation counts only after
  its partition swap commits. The runner adds the per-run delta to its receipt. Existing
  entities touched by mask updates and replayed claims are excluded from novel-row counts.

## Evidence

- 48 Node tests passed: transport, data workspaces, route text, projection.
- Frontend production build and TypeScript check passed.
- Managed substrate and test projects built; eight ingestion accounting tests passed on
  HART-DESKTOP, using its existing native runtime and perfcache artifacts.
- Two PostgreSQL integration tests passed on hart-server in the dedicated disposable
  `laplace_substratecrud_test` database. They exercise replayed claims and existing-entity
  mask updates, including new entity and physicality counts. Tests used the changed managed
  assembly and the installed native closure. No native source changed in this repair.
- Browser interaction checks exercised live authenticated browse and topic reads, and the
  controlled fixture scenarios for independent errors, absent stages, auth transitions,
  literal route text, focus, and packed-run navigation. The matching Playwright specs are
  supplied for repeat runs with Vite and `LAPLACE_E2E_FIXTURES=1`; their CLI runner was not
  used for this browser verification.

The normal installed `ingest framenet` command completed all **14,903 files**, **119,178
input records**, and **45,940 intents**, with zero failures in **451.5 seconds**. Journal run
`04dfec0b-0243-496c-bb57-55f672d2b5cd` is `ok`. Physicality closure reported **823,911 new
entity identities**, zero missing. The source folded **725,375 cells** from **1,968,609
observations**. This run used the existing installed parser; no parser change is claimed.

That run exposed the accounting defect: its installed CLI reported zero inserted rows and
`unmeasured` throughput, despite the committed load. Its historical receipt is unchanged;
the new accounting is separately verified by the tests above.

## Remaining measured work

- Rebuilding 256 partition leaves took **181.9 seconds** of the FrameNet run, approximately
  40% of total elapsed time. This is a concrete performance target, not an inferred parser
  bottleneck. No ingestion speedup is claimed by this repair.
- The staged loader's detailed phase timings are in its log, while aggregate fold timing
  fields in the installed run journal remain zero.
- Operator key issuance for an unknown tenant returns a foreign-key failure as HTTP 503.
  Verification used the existing verification tenant through the supported issue/revoke flow.

Application repairs are source changes pending normal review and release. The full-corpus
ingestion above did run against the installed substrate.
