# Selecting where the Lichess bot runs

The monorepo's Chess Lab has a **Lichess routing** selector. Every configured UI
addresses the same account controller. Select an installed engine profile, or Off,
and Apply. The page shows the requested profile, active profile, connection state,
game count and initialization failures. Changing a selection stops new challenges
and drains existing games before starting the new profile. Concurrent edits are
rejected until the stale page refreshes.

An operator must sign in with a browser account explicitly listed in the host's
`api-client.json`. Tenant ownership does not grant control of the shared bot.
The browser never receives the bot token or the API-to-controller credential.

## Engine and knowledge binding

The controller runs `Laplace-UCI` with a named connector profile. Connectors can
launch a process, invoke a configured SSH command, or open an authenticated
WebSocket. The profile declares the implementation, environment and knowledge
target. Configuration paths belong to the installation; users do not need mounts.

Before accepting challenges, the controller checks the writer's PostgreSQL cluster
identifier and database against the identity reported by the actual UCI engine.
The monorepo engine also implements the UCI `Refresh Knowledge` button. Each bot
decision refreshes the engine's provider snapshots after the confirmed game state
has been admitted. This crosses process boundaries; a writer's process-local cache
epoch cannot update a remote engine. Refresh time is charged against the game clock.

Confirmed moves and real outcomes continue through `ChessLiveGameHost` and the
existing ingestion pipeline. Curated seed promotion does not suspend live admission.
Stockfish and Lc0 are explicit comparison engines, not substitutes for Laplace.
Refactor and modular implementations require their own engines and admission
contracts; the monorepo controller does not claim to implement those contracts.

## Installation and operation

The release pipeline publishes `app/uci/laplace-uci` and `app/lichess-runtime`
with the same native dependency closure as the monorepo API. The independent
[Laplace-UCI repository](https://github.com/SaltyPatron/Laplace-UCI) owns transport.

Host setup consumes a declared JSON installation and an OpenBao AppRole enrollment:

```sh
sudo bash deploy/linux/site-host.sh --chess-setup \
  --chess-configuration /path/to/host-installation.json \
  --chess-enrollment /path/to/private-enrollment.json
```

The declaration contains explicit service identities, paths, public profile
configuration and secret field destinations. An optional `uci_setup` installs its
declared connector dependency first. Setup retains the enrollment root-only, installs
the credential agent, requires a fresh workload fetch, and verifies authenticated
controller status. Later full `site-host.sh` runs reconcile the installed declaration
at `/etc/laplace-mono/chess/agent/installation.json`, or
`LAPLACE_CHESS_CONFIGURATION`. Human OpenBao login is used only for enrollment.

The controller listens on loopback. A Tailscale-only nginx location lets the other
host APIs reach it; public ingress cannot call that location. The controller token
is read from its private file for each request. The Lichess token is read from
`LICHESS_TOKEN_FILE` whenever the bot starts. Changing environment selection needs
no sudo and no service restart.

Inspect the declared controller and credential service journals with `journalctl`.
A live HTTP listener proves installation only. Connector `verify` performs a real
readiness/new-game/search exchange, and live admission remains a separate behavior
to verify with actual confirmed games.

On Windows development, `deploy/windows/Install-ChessClient.ps1` installs the UI's
controller credential without elevation. Its declaration gives the OpenBao address,
single credential record, controller URL, private directory and permitted browser
user IDs. Pass `-Configuration <file> -Enrollment <private-file>` once. The installer
protects the enrollment with Windows DPAPI, starts OpenBao Agent, checks authenticated
controller status and consumes the source enrollment. A per-user startup shortcut
restarts the agent at sign-in. The agent consumes its temporary SecretID file; the
encrypted enrollment remains for subsequent starts. Repeating installation reuses a
verified running agent. Restart the development API after first installation so it
inherits `LAPLACE_LICHESS_CONTROL_CLIENT` from the user's environment.

Linux credential destinations may name a `service` instead of a fixed `user`.
The renderer resolves that unit's actual unprivileged identity and verifies it can
read the rendered file. This accommodates hosts where the API runs as `laplace-api`
rather than assuming the build runner is also the application identity.
