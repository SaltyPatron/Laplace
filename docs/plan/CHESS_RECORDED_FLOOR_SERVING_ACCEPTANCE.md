# Verify a recorded chess floor in the serving process

Run this after installing a sealed recorded-floor pair and starting the ordinary API. The caller must hold the normal shared-host reservation so deployment cannot replace the payload halfway through observation. The checker does not restart the service, record games, or contact Lichess.

Use the installed prefix, its exact built position producer, a catalog publication containing the same managed/native payload as the installed API, and the openings directory used by the installed build:

```bash
python3 scripts/verify-chess-floor-serving.py \
  --prefix "$LAPLACE_PREFIX" \
  --catalog-dll "$CHESS_CATALOG_DLL" \
  --position-emitter "$CHESS_POSITION_EMITTER" \
  --openings-dir "$CHESS_OPENINGS_DIR" \
  --api-base http://127.0.0.1:5187 \
  --output "$LAPLACE_EVIDENCE_ROOT/recorded-floor-serving"
```

All filesystem arguments must be absolute. The output directory must be new. The default app directory is PREFIX/app; use --app-dir only to identify the actual installed API publication. The API credential, when needed, comes from LAPLACE_API_KEY or the environment-variable name selected by --api-key-env. No credential value is retained. The HTTP target is restricted to a plain loopback origin, uses no proxy, and refuses redirects.

The command authenticates the completed export receipt and its witnessed JSONL through the existing artifact owner. It regenerates the finite seed surfaces and transition floor through ChessCatalogSurfaces, requires the surfaces hash to match the installed pair receipt, and runs the exact native position producer bound by that receipt. The producer keeps its explicit memory and spill bounds (64 MiB and 2 GiB by default); the whole observer has a 600-second deadline. Override those finite limits only for the actual environment.

The selector replays a complete retained playing through the ordinary typed chess owner. It selects a legal, nonterminal position with more than seven pieces, absent from both reproduced seed floors. After unloading the seed position map it derives canonical geometry through the existing native composition owner and compares the installed native record bit for bit. It also requires the installed transition to return the canonical successor through the persistent lookup source. This is derived cache evidence; it does not create entities, physicalities, observations, or a second source of canonical identity.

The live check reads /health/ready, binds the reported PID and process start time, and requires /proc/PID/maps to name the exact device/inode of both immutable generation files and the selected native and managed serving payloads. It sends one /chess/bestmove request with the selected FEN, depth 1, and substrate=true, then repeats readiness, process, file and export validation. The returned move must belong to the selector's real legal frontier and the process's persistent transition counter must increase. A process-local novel hit cannot satisfy this check.

The retained receipt distinguishes canonical selected-record equality, actual installed mappings, and a serving-process persistent hit observed during the request interval. The public counters are process-wide; concurrent requests are not excluded, so the receipt does not claim exclusive attribution of that counter increment to one request or one transition key. A repeated request may already have its immutable legal frontier cached and produce no new persistent lookup. In that case the checker fails honestly; a new evidence directory with --skip-eligible N can select a later eligible transition. It never replaces a missing persistent hit with Remember evidence.

The receipt does not claim a position lookup hit inside the API, new recorded games, machine cold boot, or throughput. It retains selector output, exact input/owner hashes, regenerated seed artifacts, HTTP request/response, readiness and process mapping observations. A failed attempt retains its failure and completed earlier observations.

The protocol and kernel identity controls can run without an API or native chess fixture:

```bash
python3 scripts/test-chess-floor-serving.py
```

They require the normal absolute TMPDIR test workspace. Native-backed selector controls are part of Laplace.Chess.Tests under ChessRecordedFloorWitnessTests; compilation alone does not establish that those controls or the live installed proof passed.
