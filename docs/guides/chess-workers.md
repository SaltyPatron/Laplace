# Chess execution across installed workers

Open **Chess → Lab → Engine Gauntlet**. Choose the match conductor, **Laplace worker**
and **Stockfish worker** independently. `This installation` runs that engine beside
the API. Named connections run it through the installed Laplace-UCI HTTPS service.
The browser supplies profile names only. Credentials remain in the API's private
OpenBao-rendered files; clients need no database login.

The API accepting the match owns its authenticated requester, job, opening schedule,
recording and cancellation. Choosing an engine worker does not change those, the
active Lichess bot, or the recording database. The worker profile's knowledge label
describes its configured read source; the actual Laplace cluster/database identity
returned by the engine is retained in the experiment's UCI configuration. A transport
failure fails the run instead of silently substituting another engine or worker.

Match concurrency opens separate UCI sessions. The connector's installed `maxSessions`
limits admissions; an exhausted worker rejects the connection. Stop cancels the
conductor and its local connector processes, closing their remote sessions. This
selection does not implement a distributed queue or automatic migration of games.

## Installation

`LAPLACE_CHESS_UCI_CONNECTOR` names an installed absolute `laplace-uci-connect`
executable. `LAPLACE_CHESS_UCI_CONFIG` names its private JSON client configuration.
Both must be declared together. These settings use the same environment/shared
chess configuration resolution as the other chess tools. Configuration version 1
and named WSS profiles are required. The monorepo match accepts `laplace-mono` profiles
for Laplace and `stockfish` profiles for Stockfish. Refactor and modular profiles
retain their separate implementation identities.

For Windows, add `chess.match_engines` to the IIS installation declaration:

```json
{
  "connector": "D:/Applications/Laplace-UCI/laplace-uci-connect.exe",
  "profiles": {
    "mono-staging": {
      "label": "Laplace staging worker",
      "implementation": "laplace-mono",
      "environment": "staging",
      "knowledge": "configured-world",
      "endpoint": "wss://staging.example.com/uci/mono-staging",
      "token_field": "uci_staging_token"
    }
  }
}
```

Use the actual installed paths and origins. Run the existing IIS administrator setup.
Its credential agent renders the named token fields from the existing `records.chess`
OpenBao record, alongside the controller credential. It writes the connector JSON
and tokens beneath the installation's private `state/engine-connections`, and grants
the API pool read/execute access to the declared connector publication.

On Linux the equivalent `match_engines` object belongs to the existing `chess-host.py`
installation declaration. It additionally requires `directory`, an absolute private
directory for that API's connector configuration/tokens. The existing `api_service`
identifies the consumer; the installer gives only that service account read access,
installs the two environment settings and uses the existing OpenBao Agent to refresh
the tokens. No second database, mandatory mount, human credential or new daemon is used.

An already-enrolled Linux installation can add these profiles without restarting
the Lichess controller:

```sh
sudo python3 deploy/linux/chess-host.py configure-match-engines \
  --configuration /path/to/retained/chess/agent/installation.json \
  --engines /path/to/match-engines.json
```

This setup authenticates the installed AppRole, verifies a real engine search for
every declared profile, persists the declaration and restarts the API with its
configured connections. Future credential renders use the same installed declaration.
Use the retained installation path declared on that host. The match-engine directory
must be a dedicated credential directory for that API.

Remote Stockfish receives no coordinator-local Syzygy path. Configure its tablebases
on its worker. Explicit remote path overrides are rejected. Threads/hash options
continue to describe that engine session; they are not a cluster-wide resource budget.

The HTTP job configuration fields are `laplaceProfile` and `stockfishProfile`, empty
for the local engine. Unknown names and mismatched implementation identities return
HTTP 400. The catalog returns public profile metadata, never credential paths/tokens.
The retained experiment includes the selected profiles, conductor arguments and
engine-reported identities. The connector executable's hash identifies transport;
it is not the remote engine binary's hash.

Conductor argument formats follow the upstream [Fastchess manual](https://github.com/Disservin/fastchess/blob/master/man.md)
and [Cute Chess CLI reference](https://github.com/cutechess/cutechess/blob/master/projects/cli/res/doc/help.txt).
