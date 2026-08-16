# External agents guide — querying models outside the substrate

Operational how-to for the outbound model lane: the MCP `ask` and `agents` tools,
the routing table in `agents.json`, and where credentials come from. The inbound
direction — other clients calling Laplace as an OpenAI-compatible server — is
`app/Laplace.Endpoints.OpenAICompat`, not this. Verify commands against the live
`help` tool if this drifts.

## What it is

`Laplace.Agents` is a provider-neutral client for hosted chat models. One tool
call goes out to OpenAI, Anthropic, xAI, Google, OpenRouter, Groq, DeepSeek,
Mistral, an Ollama or vLLM box, or Laplace's own endpoint, and comes back as one
row. Three request/response shapes cover all of them — `chat/completions`,
Anthropic Messages, Google `generateContent` — so a provider costs a table row in
`AgentProviders`, not a client.

The library is referenced by the MCP server today and is deliberately free of MCP
types, so the CLI and the HTTP surface can reach the same table instead of each
growing a second one that drifts.

## Naming a model

`ask` resolves `model` three ways, in this order:

1. **An alias** from `agents.json` — `house`, `reviewer`.
2. **`provider/model`** — `xai/grok-4`. Split **once**, so OpenRouter's own
   vendor-qualified ids survive: `openrouter/anthropic/claude-opus-5` calls
   OpenRouter with the model `anthropic/claude-opus-5`.
3. **A vendor-branded bare name** whose prefix identifies the provider:
   `claude-*`, `gpt-*`/`o1`/`o3`/`o4`/`chatgpt-*`, `grok-*`, `gemini-*`,
   `deepseek-*`, `mistral-*`/`magistral-*`/`codestral-*`.

A bare name with **no vendor prefix** (`llama-3.3-70b`, `qwen-max`) is refused,
not guessed. A dozen hosts serve those names at different prices; inferring one
bills the wrong vendor and the call still succeeds, which is the worst available
outcome. Qualify it or alias it.

Omitting `model` uses `LAPLACE_AGENT_DEFAULT`, else the config file's `"default"`.

The `provider` argument **forces** the route and makes `model` a literal vendor
model id — the alias table is skipped entirely. Overlaying a provider onto an
alias that already names one would leave "which vendor ran this" unanswerable
from the arguments, and that is the question a bill is settled with.

## Configuration

Two files, different jobs.

**`agents.json` — routing, never secrets.** Searched in order:
`$LAPLACE_AGENTS_CONFIG`, `$LAPLACE_APP_DIR/agents.json` (default
`/opt/laplace/app/agents.json`), `<repo>/config/agents.json`,
`~/.config/laplace/agents.json`. Template: `config/agents.json.example`. The
parser **rejects an inline `api_key`** — this file rides the deploy payload into
a shared app directory, unlike `laplace-api.env`, which the payload sync
explicitly excludes. Name the variable with `api_key_env` instead.

**`/opt/laplace/secrets/agents.env` — the keys.** Template:
`deploy/linux/agents.env.example`. Values are read from the process environment
first and from this file second, through the same `LaplaceInstall.TryReadConfig`
reader the API service already uses for Stripe and Lichess. That fallback is
load-bearing: `laplace-mcp` is not a systemd unit, it is a stdio child of
whatever agent client launched it, and such clients usually cannot inject
environment variables into it.

The catalog is re-read **per call**. Nothing owns a restart of a stdio child
(GH #809), so an edited `agents.json` has to take effect without one.

## OAuth and SSO

Static API keys are the only option most vendors offer for inference — OpenAI,
xAI, Groq, DeepSeek and Mistral have no OAuth on the completions API, and
OpenRouter's PKCE flow *issues a key* rather than replacing one. Where OAuth or
SSO does exist it is a different endpoint and flow per provider, not a flag:
Anthropic profiles (`ant auth login`), Google via Vertex AI's ADC, Azure/Foundry
via Entra ID, Bedrock via SigV4 and IAM Identity Center.

Three per-agent fields cover every case that is a bearer token:

| Field | What it does |
|---|---|
| `auth` | `bearer` moves the credential onto `Authorization: Bearer`; `api_key` puts it on the provider's own header (`x-api-key`, `x-goog-api-key`). Defaults per provider. |
| `token_command` | A command run **per call** whose single line of stdout is the credential. |
| `headers` | Extra request headers the flow needs. `Authorization` is refused here — that is what the other two are for. |

Anthropic OAuth needs all three, because its OAuth mode is not its key mode:

```json
"claude-oauth": {
  "provider": "anthropic", "model": "claude-opus-5",
  "auth": "bearer",
  "token_command": "ant auth print-credentials --access-token",
  "headers": { "anthropic-beta": "oauth-2025-04-20" }
}
```

`token_command` is **not cached**. One process spawn is nothing beside a
multi-second model turn, and a cache would need an expiry this layer cannot
observe — the token carries its own lifetime and nothing here can read it. A
stale token served from a cache behind a working command is the exact failure the
command exists to prevent. It is also **not a shell**: the string is split on
whitespace honouring quotes and executed directly, so no pipeline or substitution
runs. Name a shell explicitly if you need one.

What these three fields do **not** cover: AWS SigV4 (Bedrock) and Google ADC
(Vertex) are request *signing*, not a header, so they need real signing code.

## Checking a route before blaming the vendor

```
agents            # every alias + every installed provider
```

Each row carries the model it would call, the base URL, the variable its key is
read from, and whether that key resolves right now. Two failures look identical
from a failed `ask` and have different fixes:

- `credentialed: false` — the named variable is unset in both the environment and
  `agents.env`.
- `model: null` — nothing has said which model to call. Only `anthropic` ships a
  default (`claude-opus-5`), because it is the only vendor whose current model id
  this repository states from a checked-in reference rather than from memory. An
  invented id 404s at the vendor and reads as "the agent is down".

Key **values are never returned** — only the variable name — so the output is
safe to read into a model's context.

## Per-wire behaviour the caller does not have to know

- **Anthropic** gets a required `max_tokens` defaulting to 16000, because it
  bounds thinking as well as text and current Claude models think by default. It
  gets **no sampling parameters unless you name one**: `temperature`/`top_p`/
  `top_k` return 400 on Claude Opus 4.7 and later, so an unrequested default
  would break every current Anthropic model on this lane.
- **OpenAI** gets `max_completion_tokens`; its clones get `max_tokens`. The field
  name is provider data, not a branch.
- **Google** gets `contents` / `systemInstruction` / `generationConfig`, and its
  key rides `x-goog-api-key`.
- A **refusal** (Anthropic `stop_reason: "refusal"`) or an upstream block
  (Google `promptFeedback.blockReason`) returns an **empty reply with a
  finish_reason and a note**, not an error. Both arrive as HTTP 200 with an empty
  content array; code that reads `content[0]` before `stop_reason` breaks on
  them, and collapsing them into a thrown error discards the reason that decides
  the next move.
- **Retries** cover 408/429/500/502/503/504 up to 3 attempts, honouring
  `Retry-After` capped at 30s. 401/403/404 are never retried and name the
  variable or the model id to fix.
- `timeout_seconds` (default 180, clamped to 1–3600) is the wall clock for the
  whole call including retries. Reasoning models routinely run for minutes; the
  HTTP client itself sets no second ceiling.

## Running it

```
ask   prompt="Summarise this consensus chain" model="house"
ask   prompt="..." model="grok-4" timeout_seconds=300
ask   prompt="..." provider="ollama" model="llama3.2"
```

Everything else — routing precedence, credential resolution, request shaping,
response reading, retry policy — is covered by `app/Laplace.Agents.Tests` against
a scripted handler, with no network.
