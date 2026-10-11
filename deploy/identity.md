# Browser identities

The monorepo owns its Google/Microsoft enrollment and Linux credential refresh.
`identity-environments.json` declares the provider project, callbacks, OpenBao
record and AppRole for each environment. The other implementation families must
declare their own identities when their browser surfaces are installed.

| Consumer | OpenBao KV v2 record | Runtime authentication |
| --- | --- | --- |
| Desktop development | `kv/hosts/hart-desktop/laplace-mono-identity` | Existing Windows user DPAPI enrollment |
| Server staging | `kv/hosts/hart-server/laplace-mono-identity` | `laplace-mono-hart-server-identity` AppRole |
| Production | `kv/hosts/hart-prod/laplace-mono-identity` | `laplace-mono-hart-prod-identity` AppRole |

Google client JSON downloads are enrollment inputs. `import-google-clients.py`
receives `{"configuration": <declaration>, "clients": [<downloaded web objects>]}`
on stdin on a host where the operator has authenticated to OpenBao. It checks the
project and exact callback sets, preserves the Microsoft provider, writes with
KV compare-and-set, reads back the values, and emits public metadata only.
`verify-google-callbacks.py <metadata-receipt.json>` checks Google's authorization
response. A successful response does not establish a completed user login.

For Linux, an operator enrolls each workload once:

```sh
python3 deploy/linux/identity-runtime.py enroll --environment staging \
  --enrollment /private/enrollment.json
```

The enrollment contains the AppRole credential, not an administrator token. It
is mode 0600 and must be transferred only to its declared host. The role can read
exactly its identity record. It can renew/revoke its own short-lived token but
cannot read another environment or write provider secrets. The enrollment
command verifies reads, denied reads, renewal, revocation and fresh login.
The reusable SecretID is protected by host filesystem permissions and does not
expire during normal operation; revoke/reissue it when replacing the host or
recovering a compromised enrollment.

Install routing and credentials together on a declared private browser host:

```sh
sudo bash deploy/linux/site-host.sh --browser-setup \
  --identity-enrollment /private/enrollment.json
```

On a public host whose routing is already installed:

```sh
sudo python3 deploy/linux/identity-runtime.py install \
  --enrollment /private/enrollment.json
```

Subsequent full `site-host.sh` runs reconcile the installed enrollment without an
operator login. `LAPLACE_IDENTITY_DIRECTORY` selects the private installation
directory (default `/etc/laplace-mono/identity`). Standalone commands accept
`--directory`. Enrollment accepts `--api-url`, `--api-service`, and `--lock` for
the actual application listener, service and deployment lock. They are recorded
once; a repair does not invent different values.

The root-owned `laplace-mono-identity.service` runs OpenBao Agent with no listener
and no token sink. Agent authenticates and renews its token in memory. It reads
the KV record every minute and writes a private candidate. The installed helper
validates all four provider fields, locks against deployment, atomically updates
the API environment, restarts the API only when values changed, and checks
readiness plus the actual provider client IDs emitted by its login endpoints.
Failed activation restores the preceding environment and restarts it. The agent
never runs with an operator token. Its root execution permits systemd restart
and protected-file updates; its OpenBao policy still has only the record read.

The directory is root-only 0700, credential files 0600. A systemd API drop-in
loads the managed identity file after legacy environment files; applications and
CI do not need filesystem access to the enrollment. Credentials persist across
agent restarts. During an OpenBao outage the API retains its last verified
configuration, while Agent retries; this does not extend a provider credential
that has been revoked at Google or Microsoft.

```sh
sudo python3 deploy/linux/identity-runtime.py verify
systemctl status laplace-mono-identity.service
sudo journalctl -u laplace-mono-identity.service
```

The private `receipt.json` records the last checked client ID and activation.
Never publish `enrollment.json`, `candidate.json`, `identity.env`, role/secret ID
files, provider downloads or a token. Vaultwarden remains for human login and
recovery material; the API does not fetch credentials from a person's unlocked
vault. OpenBao's existing backup/recovery process must include these KV records.
Keep the old Google registration until every consumer has migrated and actual
user login through each intended browser origin is verified.

References: [OpenBao AppRole auto-auth](https://openbao.org/docs/agent-and-proxy/autoauth/methods/approle/),
[Agent templates and renewal](https://openbao.org/docs/agent-and-proxy/agent/template/).
