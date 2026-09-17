# Persistent CuteChess under the existing user manager

This route runs the installed CuteChess application through an owned Xpra runtime and an existing lingering systemd user manager. It uses ordinary package data acquisition and user-service operations. The system-wide Xpra bootstrap remains a separate option.

The source candidate is separate from the previously qualified system-unit route. A successful source test run does not establish that Xpra has been acquired, the service is running, or a remote desktop client has connected.

## Established host facts

The read-only inventory in run [35233437307](https://github.com/SaltyPatron/Laplace/actions/runs/35233437307), job `105243223415`, completed on 2026-09-17 using authenticated source `40237db5b06ad98455e9a4a9edb5317d66c2066d`.

| Observation | Actual value |
| --- | --- |
| Account | `laplace-runner`, UID/GID 994 |
| Home | `/var/lib/agents/laplace-runner`, owned 994:994, mode 2770 |
| Login shell | `/usr/sbin/nologin` |
| User manager | Running; linger enabled; `/run/user/994` mode 0700 |
| Platform | Ubuntu 22.04 Jammy, system Python 3.10.12 |
| Available Python dependencies | GI 3.42.1, Pillow 9.0.1, DBus 1.2.18 |
| Missing dependencies | Cairo and Gtk/Gdk 3 typelibs; system Xpra and Xvfb |
| Selected X11 runtime | `fc67541a867b5b7e40704369d11bfd58a4fc140adfbe4f4ce1861c1fe4ee902f` |
| Installed application | `/opt/laplace/bin/laplace-cutechess` exists and is executable |

The selected X11 manifest identity was `bf8ea7a1754c02e733dc0feb788f786caf837a1695471abf23c8549998f56ebf`. The inventory only observed these identities; the acquisition owner revalidates the selected X11 files before using them.

The actual inventory artifact is `10502535053`, SHA-256 `931f913d59bf730e1811c408d109e3dc8f5e95341a0fe2d78d9fb3a6fe5c76ab`. Its source artifact is `10502174063`, SHA-256 `04bdba567418d3b26f2a2a2de87cebf4e4809462d613f156007f9e714597f1c0`.

## Acquire the owned runtime

Run the source-reviewed acquisition as the existing service account through its authorized runner context:

```sh
/usr/bin/python3 -B scripts/chess-xpra-runtime.py \
  --release deploy/cutechess-user-runtime-release.json \
  --evidence-output /build/laplace/recovery/<owned-run>/xpra-acquisition
```

The acquisition owner uses `/opt/laplace/tools/chess/xpra-runtime`. It authenticates official Xpra 6.5.3 Jammy packages against the pinned signing key and signed APT metadata, verifies exact package hashes and sizes, and extracts package data into an owned generation. The five-package selection contains common, server, X11 bindings, the base client, and the GTK client. The client packages make genuine local desktop-client verification possible.

Missing Ubuntu dependencies are selected with isolated APT configuration, lists, cache, and logs. The host package database is read only. No package installation, maintainer script, system unit, or global APT configuration is run or changed. Selection fails if the resolved closure requires replacing the host C runtime or Python.

The owner checks Python 3.10 compatibility, required GI/Cairo/Pillow/DBus and native Xpra modules, selected helpers, native library resolution, content hashes, and the referenced X11 selection before atomically publishing `current.json`. Failure retains diagnostic evidence and preserves the previous selection.

The supplied Xvfb is selected explicitly from the existing private X11 runtime. The acquisition does not need an Xorg wrapper, a privileged display server, or a new system package installation.

## Install and start the user service

From the same selected source and account:

```sh
/usr/bin/python3 -B scripts/setup-cutechess-user-session.py --loopback --start
```

The installer validates the existing lingering user manager and acquired runtime. It installs the application session launcher and its runtime helpers under `~/.local/lib/laplace-cutechess`, the configuration under `~/.config/laplace`, and `laplace-cutechess.service` under `~/.config/systemd/user`.

With `--loopback`, the installer creates a random private credential only if absent, retains it across restarts and exact reinstalls, and never prints it or puts its value in process arguments, reports, or hashes. The password file is `~/.config/laplace/cutechess-session-password`, contains exactly 64 lowercase hexadecimal bytes without a newline, and is owned by the account with mode 0600. An explicitly selected existing private file can be used with `--password-file`.

The service keeps its private Unix socket and adds only the fixed authenticated listener `127.0.0.1:14501`. File authentication is mandatory on that listener. There is no public TCP bind, web endpoint, mDNS advertisement, arbitrary command-start facility, audio, printer, webcam, or file-transfer service. The underlying Xvfb also disables its TCP listener and uses an Xauthority cookie.

Without either loopback selection option, the installer chooses the private Unix transport. This is useful only when the client already has an authorized route to the same account's Unix socket. The observed service account has a nologin shell, so direct SSH login as that account is not an established access path.

The unit runs in the existing user manager and uses `RuntimeDirectory=laplace-cutechess`, `WantedBy=default.target`, and process-group shutdown. Closing a client leaves CuteChess running; closing the application ends its Xpra session. The installer does not enable linger, alter the account shell, edit system units, or add a PostgreSQL dependency to the user manager.

Existing primary-group-writable home directories remain unchanged. Newly managed private directories and files have private modes. On an update, the installer accepts only the fixed managed paths named by a valid prior receipt and verifies that every existing file still matches its recorded content and mode. It atomically replaces changed managed files and publishes the new receipt last; a publication failure restores the previous managed bytes and modes. Unknown files, local edits, incomplete prior installations, and symlinks are preserved and refused.

The credential remains the same across managed updates. Running `--start` after a changed installation restarts an already active service so it uses the updated code and selected runtime; this ends that application's current session. An exact reinstall of an already activated selection does not restart the active service and preserves the running session. A private receipt-bound pending marker records files-only updates and unsuccessful activation, so a later `--start` still applies the selected code and runtime. An activation failure is reported with the newly installed receipt and pending marker retained for diagnosis; the installer does not claim that source publication proves a successful service start.

## Connect from an authorized operator machine

Use an existing working operator SSH account and a private local copy of the session credential obtained through that account's established authorized administrative channel. Credential retrieval is separate from tunnel creation; ordinary SSH forwarding does not grant permission to read another account's files. Never place the credential value in a URL, terminal command, chat message, artifact, or log.

For an operator whose normal SSH command is `ssh EXISTING_OPERATOR@hart-server`:

```sh
ssh -N -L 127.0.0.1:14501:127.0.0.1:14501 EXISTING_OPERATOR@hart-server
```

Then run an installed compatible Xpra desktop client locally:

```sh
xpra attach tcp://127.0.0.1:14501/ --password-file=/absolute/private/local-password-file
```

Keep the local password file private. The SSH connection encrypts the remote leg and the Xpra listener also authenticates its clients. The same tunnel and client command can reconnect to the existing application after detaching.

These commands do not establish that an operator account can read the credential or that a particular desktop client has connected. Actual delivery evidence must identify the existing authorized credential retrieval path and distinguish a local verified client from a remote operator client.

## Verification and limits

Source tests exercise acquisition boundaries, package identity, extraction containment, installer ownership and preservation, fixed transport construction, credential handling, and user-manager behavior. Runtime delivery additionally requires:

1. Successful authenticated package acquisition and native-module qualification.
2. An enabled and active user service using the selected runtime and existing CuteChess launcher.
3. A real client rejected without the credential and accepted with it.
4. A real GTK client displaying the exported application, detaching, and reconnecting while the server application identity remains the same.
5. Evidence that the only Xpra TCP listener is the selected loopback address.

After installation, the bounded local proof can be run in the same authorized service-account context:

```sh
/usr/bin/python3 -B scripts/check-cutechess-user-session.py \
  /build/laplace/recovery/<owned-run>/cutechess-session-proof.json
```

It uses a separate private Xvfb display for the real GTK clients, rejects anonymous and incorrect credentials, verifies the fixed loopback listener, and checks that two attaches and detaches preserve the same server, native process, and native window. It closes only the clients and display it created. The receipt contains selected process and window identifiers, statuses, and hashes; it excludes credentials, protocol dumps, and raw window titles.

The first GUI acceptance already established a working CuteChess 1.5.1 / Qt 6.11.2 application under the selected X11 runtime. It did not establish Xpra installation or persistent remote access. This user-session route reuses that application and its selected engines. It does not change the frozen recording sample, engine tuning, or calibrated defaults. A depth-limited latency profile is not a claim that a particular hash size is optimal for every full-game time control.

Official implementation references: [Xpra authentication syntax](https://github.com/Xpra-org/xpra/blob/v6.5.3/docs/Usage/Authentication.md), [Xpra path overrides](https://github.com/Xpra-org/xpra/blob/v6.5.3/xpra/platform/paths.py), [Xvfb environment and command handling](https://github.com/Xpra-org/xpra/blob/v6.5.3/xpra/x11/vfb_util.py), and [systemd 249 user/runtime directory semantics](https://github.com/systemd/systemd/blob/v249/man/systemd.exec.xml).
