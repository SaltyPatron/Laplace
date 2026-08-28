# Legacy chess runtime repair — 2026-08-28

Scope: legacy `SaltyPatron/Laplace`, PR #1331. No refactor checkout, live
ingest, database contents, or running service was changed for these checks.

## Reproduced defects

- The supplied cutechess transcript failed before Laplace's UCI handshake.
  Launching `/opt/laplace/app/laplace-uci` directly reproduced the missing
  `laplace-uci.dll` error. Deployment copied just the .NET apphost, not its
  publish closure. It now stages an immutable full UCI runtime and tests the
  installed candidate before replacing the API payload.
- Ubuntu's `/usr/games/stockfish` was version 14.1. Setup selected that old
  distro binary. Setup/CI now install the checksum-locked upstream 18 release,
  verify its executable and preserve previous releases and unmanaged files.
  API, CLI and ingest path discovery all recognize the managed binary.
  cutechess 1.5.1 was already the current upstream release.
- The runner always enabled `UCI_LimitStrength`. A new `limitStrength=false`
  option reaches both preview and execution and omits `UCI_Elo`. 2000 was a
  default, not a hard cap: the old live preview already accepted 2300. No
  browser lock to 2000 was reproduced. The new browser regression exercises
  changing it to 2300 and starting in both limited and unlimited modes.
- A successful exit plus any score line could mark a zero-game tournament
  complete. Completion now requires every expected game to have scored.

## Verification boundary

Local checks on hart-server (isolated build root
`/tmp/laplace-managed-build.nB2AVV`):

- `dotnet test app/Laplace.Chess.Tests/Laplace.Chess.Tests.csproj` with
  `Tier!=db&Tier!=perf`: 568 passed, no skips.
- `ChessRuntimeContractTests`: read-only API preview, strength options, and
  lazy runtime initialization tests pass.
- `npm run typecheck` and `npm run test:chess-ui`: pass. The latter creates an
  ephemeral loopback Vite server and mocks API requests; it never launches a
  live job. It verifies UI values and submitted configuration.
- `test-deploy-payload-sync.sh`, `test-stockfish-release.py`, and
  `test-uci-publish.py`: pass. Deliberate missing apphost companions, invalid
  archives, version/handshake failures and installed-file drift are rejected.
  Repeat installation and rollback preserve prior release files and unrelated
  environment values.
- `test-cutechess-runtime.py` using cutechess 1.5.1, candidate `laplace-uci`,
  and isolated Stockfish 18: one game, eight plies, valid PGN, adjudicated draw
  at the configured move bound. Both engines use depth 1; Laplace substrate is
  explicitly off. This proves executable interoperability, not chess strength,
  learning, database recording, or corpus quality.
- Managed-service and pipeline contracts, shellcheck and docs inventory pass.

The same packaging, real bounded match, API, chess and browser tests now run
in the existing CI unit gate **before live installation**. The pinned engine is
installed in a disposable directory for that gate. CI publish snapshots and
restores the prior Stockfish launch pointer/config during rollback.

## Not resolved by this packaging repair

Interactive `/chess/play/bestmove` executes Laplace's conventional search with
optional substrate bias and learned PST; it does not delegate play to Stockfish.
The endpoint name itself does not prove a cheat, and these repairs do not turn
that architecture into a purely substrate-driven learned player.

The prior full service rollout failed the semantic election evaluation and
rolled back. That gate has not been bypassed, weakened or re-baselined. These
changes require CI verification and successful delivery before the live host
can be described as upgraded. A green test-only run is not a deployment.
