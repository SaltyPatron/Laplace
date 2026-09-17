# Persistent CuteChess under the existing user manager

CuteChess runs through the owned Xpra 6.5.3 runtime in the existing lingering systemd user manager. On 2026-09-17, the installed service passed two authenticated GTK client attaches and detaches while preserving the same native CuteChess process and window. Its authenticated TCP listener is fixed to `127.0.0.1:14501`. Connection from an operator's own machine remains unverified.

## Installed host and runtime

The service account is `laplace-runner`, UID/GID 994, with home `/var/lib/agents/laplace-runner` and login shell `/usr/sbin/nologin`. Its existing user manager uses `/run/user/994` and has linger enabled. The application launcher is `/opt/laplace/bin/laplace-cutechess`. The [initial read-only inventory](https://github.com/SaltyPatron/Laplace/actions/runs/35233437307) established these account and platform facts.

The [actual acquisition and installation](https://github.com/SaltyPatron/Laplace/actions/runs/35242293321) selected runtime `54e42f0c75add23677235a0e237be3ef64089bbecf3e283e86466ff2c904c5de`, manifest SHA-256 `69cb4b265eac8c09470cbe5884affc2bc8856124dff00b4aba2699f897b7357a`. Subsequent GUI repairs reused that runtime.

For a first installation or an intended dependency update, run the acquisition through the existing authorized service-account runner context:

```sh
/usr/bin/python3 -B scripts/chess-xpra-runtime.py \
  --release deploy/cutechess-user-runtime-release.json \
  --evidence-output /build/laplace/recovery/<owned-run>/xpra-acquisition
```

The owner authenticates official Xpra packages against pinned signing authority and signed metadata, verifies package hashes, and extracts an owned generation under `/opt/laplace/tools/chess/xpra-runtime`. Its isolated APT state resolves missing Ubuntu dependencies without installing host packages or running maintainer scripts. The selected private X11 runtime supplies Xvfb. Native module, library, file-identity and containment checks must pass before atomic selection publication; failure preserves the previous selection and diagnostic evidence.

## Install or update the service

From the selected source in the same authorized service-account context:

```sh
/usr/bin/python3 -B scripts/setup-cutechess-user-session.py --loopback --start
```

Managed files live under `~/.local/lib/laplace-cutechess`, `~/.config/laplace`, and `~/.config/systemd/user/laplace-cutechess.service`. The installer uses the existing user manager; it does not change the account shell, enable linger, edit system units or install a database service.

The installer creates the credential if absent and retains it across updates. `~/.config/laplace/cutechess-session-password` is an account-owned mode-0600 file containing exactly 64 lowercase hexadecimal bytes without a newline. Its value is never printed or placed in arguments. `--password-file` can select an existing appropriate private file.

The service retains its private Unix socket and requires file authentication on `127.0.0.1:14501`. It has no public TCP listener. Audio, printing, webcam, file transfer and arbitrary command-start facilities remain disabled. Xvfb disables TCP and uses an Xauthority cookie. Without loopback selection, the installer chooses private Unix transport, which requires an already authorized route to that account's socket.

Updates accept only fixed managed paths whose current contents and modes match a valid prior receipt. Unknown files, local edits and symlinks are preserved and refused. Changed files are replaced atomically with rollback on publication failure; the credential is retained. A changed `--start` installation restarts the service and ends its current application session. An exact reinstall of an already activated selection preserves the running session. A receipt-bound pending marker ensures that a later `--start` also activates files-only updates or retries failed activation.

Service output appends to `~/.config/laplace/cutechess-session.log`; standard error uses the same sink. The installer creates a single-link, account-owned mode-0600 log inside the private mode-0700 configuration directory and only reuses it when authorized by the prior receipt. It refuses unsafe or unrelated files. The raw log stays private. Delivery artifacts retain only a bounded projection with the selected credential redacted, allowing diagnosis without changing system journal permissions.

## Connect from an authorized operator machine

Use an existing working operator SSH account and obtain a private local copy of the session credential through that account's established authorized administrative channel. Tunnel creation does not grant permission to read another account's files. Never put the credential value in a URL, terminal command, chat message, artifact or log.

For an operator whose usual SSH command is `ssh EXISTING_OPERATOR@hart-server`:

```sh
ssh -N -L 127.0.0.1:14501:127.0.0.1:14501 EXISTING_OPERATOR@hart-server
```

Then use a compatible Xpra desktop client locally:

```sh
xpra attach tcp://127.0.0.1:14501/ --password-file=/absolute/private/local-password-file
```

Keep the local password file private. SSH encrypts the remote leg, and Xpra also authenticates the client. Detach to leave CuteChess running; reconnect with the same tunnel and client command. Closing the application ends its Xpra session.

The host-local proof does not establish an operator's credential retrieval, SSH tunnel or desktop connection. Direct SSH login as `laplace-runner`, whose shell is `/usr/sbin/nologin`, is not an established access path.

## Actual verification and diagnostics

Run the bounded local proof through the same authorized service-account context:

```sh
/usr/bin/python3 -B scripts/check-cutechess-user-session.py \
  /build/laplace/recovery/<owned-run>/cutechess-session-proof.json
```

It waits within a finite deadline for the same active service and authenticated native window, rejects anonymous and incorrect credentials, and uses a separate private Xvfb display for two real GTK attaches. Both clients detach through Xpra's SIGINT handler. The proof checks unchanged server/native process identities, native window and loopback listener, then reaps only its own clients and display. Its exclusive receipt retains selected fields, hashes and bounded client diagnostic tails with credential and Xauthority redaction.

[Run 35247146460](https://github.com/SaltyPatron/Laplace/actions/runs/35247146460), job `105290198287`, passed the complete installed-session proof:

| Check | Actual result |
| --- | --- |
| Anonymous / wrong password | Rejected, exits 3 / 28 |
| Both authenticated attaches | Native window `4194311` mapped at 986 × 720 |
| Both client detaches | Clean SIGINT detach, exit 130 |
| Server before and after | PID `1990859`, start ticks `92025406` |
| Native CuteChess across both detaches | PID `1990937`, start ticks `92026338` |
| Xpra TCP listener | Only `127.0.0.1:14501`, unchanged |
| Verification display | Proof-owned Xvfb reaped |
| Remote operator connection | Not verified |

The executed proof source was `ad29dcf989da11f66ce112b92196faf1a43dfc64`. Its receipt SHA-256 is `638644bbe39156c6cfb0ad2477f47ae62b758f9063b50eb753cb24d9fd1f4803`. Artifact `10508291854` has SHA-256 `289dad206dd0ebf06be29d3b916b6a1444a40583192bbea3cf7adeba8f1478af` and retains the proof and bounded redacted log projection.

The initial positive attach failed with exit 18. The [private-log diagnostic run](https://github.com/SaltyPatron/Laplace/actions/runs/35246370716) captured `ModuleNotFoundError: No module named 'xpra.net.mmap.common'; 'xpra.net.mmap' is not a package`. Xpra 6.5.3's window encoder imports a common exception even when mmap is disabled. The launcher sets the supported `XPRA_ENFORCE_FEATURES=0` after environment construction to permit that import; `--mmap=no` and all selected feature booleans remain unchanged. Upstream shows [feature selection before optional enforcement](https://github.com/Xpra-org/xpra/blob/v6.5.3/xpra/server/features.py) and [the common exception import](https://github.com/Xpra-org/xpra/blob/v6.5.3/xpra/server/window/compress.py). No vendor patch or package reacquisition was needed. Earlier failed receipts remain retained.

Separate source qualifications passed [68 acquisition/installer/proof controls](https://github.com/SaltyPatron/Laplace/actions/runs/35236679987), [four readiness controls](https://github.com/SaltyPatron/Laplace/actions/runs/35237191676), [six private-logging controls](https://github.com/SaltyPatron/Laplace/actions/runs/35246118519), and [one feature-configuration regression](https://github.com/SaltyPatron/Laplace/actions/runs/35247042007), with unchanged launcher controls retained from the earlier qualification. The complete installed proof supplies the actual client result.

This route reuses the installed CuteChess 1.5.1 / Qt 6.11.2 application and selected engines. It does not change frozen recording samples or engine tuning. The 8-thread, 16-MiB setting came from a depth-12 latency profile; it is not a general claim of optimal strength for full-game time controls.
