# Persistent operator CuteChess session

The repository's desktop launcher opens the genuine configured CuteChess Qt
application. The existing X11 acceptance process exits after its test; it is not
an operator desktop. This optional service keeps one CuteChess application
session available across SSH client disconnects. It is independent of corpus
recording, cache correctness, API readiness and Lichess service operation.

## Transport and ownership

The service runs as the existing operator account, retaining that account's
CuteChess settings and PostgreSQL peer identity. Systemd owns Xpra, Xvfb,
CuteChess and their child engines in one service cgroup. It does not run the GUI
as root or borrow API secrets.

Xpra's Unix socket is private to the operator. The client uses the existing
OpenSSH server; the service opens no TCP, WebSocket, HTTP or discovery listener.
A client disconnect leaves the application running. Closing CuteChess exits
the session normally; start the service again to reopen it. A server failure
uses systemd's ordinary restart policy. The fixed display is :120; deployment
must first confirm it is free and must not kill or take over an unrelated display.

This candidate selects official Xpra **6.5.3** for qualification, tag
`v6.5.3`, tree `c1a20f10f6be17b29392193c8e538b2e9db9eaae`.
The source entry points are the
[official release](https://github.com/Xpra-org/xpra/releases/tag/v6.5.3),
[SSH transport](https://github.com/Xpra-org/xpra/blob/v6.5.3/docs/Network/SSH.md),
[seamless sessions](https://github.com/Xpra-org/xpra/blob/v6.5.3/docs/Usage/Seamless.md)
and [package declaration](https://github.com/Xpra-org/xpra/blob/v6.5.3/packaging/debian/xpra/control).
A newer version needs the same parser/session qualification before changing the
owner's explicit selected-version check and package declaration. No source build of Xpra is claimed by
an official binary package installation.

## Dependency preparation

Use the official stable repository for the machine's actual distribution;
Ubuntu Jammy is supported. Authenticate the official signing key against
fingerprint `B4993B57323148E37977E5D873254CAD17978FAF` and retain the exact
InRelease, selected package versions and package SHA256 values. The pinned
[Jammy repository declaration](https://github.com/Xpra-org/xpra/blob/v6.5.3/packaging/repos/jammy/xpra.sources)
uses `https://xpra.org`, suite `jammy`, component `main`, with an explicit
`Signed-By` keyring. Do not use a distribution's obsolete Xpra package or mix a
source installation with packaged modules.

Select `xpra-common`, `xpra-server` and `xpra-x11` at exact Debian version
`6.5.3-r0-1`, plus
`python3-gi-cairo`, `gir1.2-gtk-3.0`, `dbus-x11`, `xauth` and
`x11-xkb-utils`, using the normal signed package resolver with
`--no-install-recommends`. The resolver supplies the matching common Python
components, PyGObject/GLib, Cairo, Pillow, X11 libraries and Xvfb. Record the
actual selected dependencies and run the session test against them; this list
is not evidence that they are already installed.

**Package lifecycle matters:** official Debian packaging enables the generic
`xpra-server.socket` proxy. Its upstream socket listens on port 14500. This
application service does not use that proxy. On a host without an independently
managed Xpra installation, prepare masks for both naming variants
`xpra-server.socket`, `xpra-server.service`, `xpra.socket` and
`xpra.service` before installing the new packages. Do not stop or replace an
existing independently configured Xpra service. Package preparation must
inspect that state first and retain it. Do not install the broad Xpra
metapackage, enable the upstream proxy, or add a firewall rule.

Audio, printing, webcam, HTML5 assets and GPU video encoders are not required by
this session. The board is rendered through the actual Qt/X11 application.

## Deployment

The implementation owner is `deploy/linux/laplace-cutechess-bootstrap`, reached
through the ordinary `scripts/setup-host.sh cutechess-session` mode. The version,
official source tree, package bytes and dependency selection are declared in
`deploy/cutechess-session-release.json`. It authenticates the official key,
uses signed apt metadata, matches package size/hash/control identity, and keeps
the selected packages and authenticated apt lists under
`/var/lib/laplace-cutechess`. It checks the complete pre-existing Xpra unit set
before creating fresh proxy masks. Existing independently managed Xpra packages,
services or edited Laplace session files are preserved rather than taken over.

Prepare the actual dependencies and run the real package parser before starting:

```sh
sudo bash scripts/setup-host.sh cutechess-session --prepare-only
```

Run these commands from the repository root of the tracked branch containing
this guide and the seven session-owner files. `setup-host.sh` resolves the
operator once from `SUDO_USER`, with `logname` as its fallback, then passes that
account explicitly to the bootstrap. The bootstrap requires an existing non-root
account; it does not reject an account because of its name. Run the command
through the intended operator account's ordinary sudo session; setting an
inherited environment variable does not replace setup-host's selection. An
explicitly selected service account is eligible under the same requirements:
the selected account must already have
the intended SSH access and access to the installed launcher and its engine/data
dependencies.

After the selected dependencies and service are qualified, the ordinary mode
starts and enables the instance using that same operator selection:

```sh
sudo bash scripts/setup-host.sh cutechess-session
```

The mutable root-owned `/var/lib/laplace-cutechess/receipt.json` records selected
package metadata, authenticated apt-list hashes, downloaded package bytes,
installed versions, owner/unit hashes, parser result, operator and observed
service process state. `prepared` means dependency/parser/unit preparation;
`started` means the systemd process was active, not that remote-window input or
reconnect has passed. The separate acceptance evidence must cover those facts.

The PostgreSQL dependency name comes from the existing
`scripts/bootstrap-laplace-runner.sh`: `bootstrap_laplace_pg_cluster` generates
`/etc/systemd/system/laplace-postgresql.service` and its `20-ready.conf`
pg_isready startup wait. Setup-host already invokes that producer. The session
uses its existing After/Wants relationship and does not modify or restart it.

The bootstrap performs the following fixed installation steps through the
ordinary host setup owner, with the actual operator account selected by
setup-host.
The example account placeholder must be replaced with the intended account.
A CI service identity is not selected implicitly: it needs the same intended SSH
access, private socket ownership and application access as any other account.

1. Install `deploy/linux/laplace-cutechess-session` as root-owned mode 0755 at
   `/usr/local/libexec/laplace/laplace-cutechess-session`.
2. Install `deploy/linux/laplace-cutechess@.service` as root-owned mode 0644 at
   `/etc/systemd/system/laplace-cutechess@.service`.
3. Run `systemd-analyze verify` against the installed unit, then
   `systemctl daemon-reload`.
4. Enable and start the one chosen instance:
   `systemctl enable --now laplace-cutechess@OPERATOR.service`.
5. Verify its real UID, cgroup, process tree, Xpra version/socket permissions,
   CuteChess/Qt/engine mappings and absence of a new network listener. Retain
   the service state and exact package/launcher hashes.

The service needs no user-login linger setting. It uses the system service
manager's fixed `User=` identity and starts at boot. It does not change the
existing API, MCP, Lichess, nginx or service-control API.

## Connect and reconnect

Install the official native Xpra client on the operator's own machine. Use its
ordinary desktop or Windows installer; the server installation does not
silently install a client on a different machine.

```sh
xpra attach --ssh=ssh ssh://OPERATOR@hart-server/120
```

External OpenSSH uses the existing SSH configuration and host-key checks.
Use the actual SSH hostname and account if different. Disconnect and run the
same command to reattach to the same application. Service controls remain:

```sh
sudo systemctl status laplace-cutechess@OPERATOR.service
sudo systemctl start laplace-cutechess@OPERATOR.service
sudo systemctl stop laplace-cutechess@OPERATOR.service
journalctl -u laplace-cutechess@OPERATOR.service
```

HTML5 is an alternative, not part of this implementation: it requires the
separate official `xpra-html5` assets and a loopback HTTP/WebSocket listener
forwarded through SSH. Exposing it through the existing LAN web application
would additionally require deliberate authenticated WebSocket routing.
The current service deliberately selects native SSH attachment only.

## Qualification boundary

Run the ordinary Python controls and the actual installed Xpra parser control:

```sh
python3 scripts/test-cutechess-session.py
LAPLACE_TEST_XPRA_PARSER=1 /usr/bin/python3 scripts/test-cutechess-session.py
```

A source review or parser pass does not prove a usable persistent session.
The actual deployment proof must start the real service, attach a client,
observe the CuteChess window and input, detach, and reattach while retaining
the same server/application process and a visible application-state witness.
Stopping the service must terminate only its owned cgroup. This proof is
separate from the existing complete-game GUI acceptance and makes no claim
about recorded-game throughput.

## Recorded source and host status — 2026-09-17

The seven implementation files were source-reviewed and exercised by hosted
[run 35211214015, job 105168898036](https://github.com/SaltyPatron/Laplace/actions/runs/35211214015/job/105168898036):
**11 controls passed and one control was skipped**. The skipped control is the
actual Xpra 6.5.3 parser; the selected package was not installed in that hosted
qualification. The subsequent account-selection correction removes only the username-specific
`laplace-runner` refusal while retaining the existing-account and non-root UID
checks. The complete updated suite in
[run 35224504107, job 105212513662](https://github.com/SaltyPatron/Laplace/actions/runs/35224504107/job/105212513662)
passed **13 controls with one skipped actual-Xpra-parser control**. Both new
controls passed: explicit non-root account selection, including `laplace-runner`,
and rejection of a root UID, absent selection or nonexistent account. The
retained artifact is `10498251891`, SHA-256
`50d2f9c03dbc617de15720c50e5384b168c087abb40c9440c47128257add66d0`.
The original passing controls cover the fixed session environment,
private-directory ownership, exact package metadata/key selection, existing
service preservation, root-owned file replacement policy, and failure/retry
receipts. They are not a package installation or persistent-session result.

The observed `laplace-runner` sudo policy in
[run 35165117823, job 105024363277](https://github.com/SaltyPatron/Laplace/actions/runs/35165117823/job/105024363277)
allowed fixed managed-service and PostgreSQL/API lifecycle commands. It did not
include `setup-host.sh`, a generic root shell/Python command, apt installation,
or the CuteChess session service. A later read-only policy query in
[run 35213009386, job 105174775546](https://github.com/SaltyPatron/Laplace/actions/runs/35213009386/job/105174775546)
returned exit 1 for the then-prepared root deployment invocation. That query
was not execution of the tracked setup command above. No successful root
session setup, installed Xpra parser, service start, or human client
attach/detach/reattach has been recorded.

The existing managed deployment verbs reconcile only their fixed application
policy; they do not provision Xpra or arbitrary system units. This integration
therefore remains an ordinary host-administrator setup operation. The commands
above use the intended owner; no sudo policy expansion or repurposing of another
privileged helper is part of this source delivery.

The independent current CuteChess acceptance in
[run 35221551456](https://github.com/SaltyPatron/Laplace/actions/runs/35221551456)
completed a 60+1 game of 75 plies, verified the full PGN and engine protocol, and
exited normally. Its owned Xvfb display closed afterward. That successful
functional game does not change the pending persistent-session status described
here.
