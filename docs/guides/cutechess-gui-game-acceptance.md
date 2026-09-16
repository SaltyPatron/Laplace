# Installed CuteChess GUI game acceptance

This opt-in observer starts the installed public GUI as the named operator on an
owned virtual X11 display and isolated D-Bus/accessibility session. It chooses the
installed Stockfish and Laplace entries through the actual New Game dialog, verifies
the named CPU/combo controls, and plays one standard game at 60+1. Neither search depth,
nodes nor total plies is capped. The finite whole-job deadline reports an incomplete
check if the game has not ended.

The direct configured-engine observer remains an independent prerequisite. It must
have passed for the same operator and unchanged installed files. The GUI observer
then requires both actual GUI-child executable identities, Laplace's loaded native
core and per-search substrate receipts, every accepted move matching the GUI debug
traffic, a completed game window, normal GUI exit, and the complete PGN written by
CuteChess. The read-only catalog command uses the existing native PGN grammar and
legal replay owner. Clock losses remain distinguished from terminal board outcomes.
Adjudication, disconnection, illegal move and interrupted collection cannot pass.

The accessibility packages are operator-test dependencies, separate from engine and
Qt runtime requirements. Use the ordinary missing-only package owner:

`timeout --signal=TERM --kill-after=10s 300s bash scripts/bootstrap-laplace-runner.sh chess-gui-acceptance-tools`

The command inventories/installs `dbus-daemon`, `at-spi2-core`,
`gir1.2-atspi-2.0` and `python3-gi`. It does not change authentication or engine
configuration. The selected Qt SDK must actually expose its AT-SPI bridge; absence
fails the real widget check. There is no alternate synthetic GUI.

After the corresponding source is built and installed, invoke the observer under the
mapped non-root operator, with an existing permanent output parent:

```bash
python3 scripts/check-cutechess-gui-game.py \
  --expected-user "$OPERATOR_USER" \
  --engine-acceptance-receipt "$PASSED_DIRECT_ENGINE_RECEIPT" \
  --catalog "$QUALIFIED_CHESS_CATALOG_SURFACES" \
  --x11-runtime-receipt "$SELECTED_X11_RECEIPT" \
  --output-dir "$NEW_PERMANENT_GUI_EVIDENCE" \
  --timeout-seconds 900
```

All paths are absolute. The evidence directory must be new and beneath
`/build/laplace`; the observer does not use the operator's usual XDG configuration.
The catalog executable is the ordinary `ChessCatalogSurfaces` output, with its
Chess/Core/native closure matching the installed UCI generation. The existing
public Unix PostgreSQL route is used; conflicting ambient connection settings and
credential overrides are refused.

`receipt.json`, `game.json`, `game.pgn`, `pgn-verification.json`, `engine-debug.log`
and the bounded X11/Qt/session logs retain successful and partial observations.
The PGN verifier's `verify-gui-game` mode performs no database writes. A passing GUI
game establishes neither durable recorded-game throughput nor the operator's
physical desktop session. Those require their own existing acceptance owners.

The ordinary policy profile runs local protocol, process-ownership and optional
package-selection controls. Native-backed managed controls exercise complete mate,
genuine clock completion and malformed/incomplete PGN refusal. These controls do not
claim an installed GUI game has run.
