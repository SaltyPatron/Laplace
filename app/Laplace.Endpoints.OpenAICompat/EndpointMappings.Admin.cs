using System.Text.Json.Nodes;
using Laplace.Agents;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// The operator surface's own endpoints — the parts that are NOT an installed SQL
/// operation, and therefore cannot go through <c>POST /v1/op</c>.
///
/// Two things fall outside the catalog by nature:
///
///   * <b>Agent routing.</b> <c>agents.json</c> is a file on the host, not a
///     substrate relation. Nothing in <c>ops.api()</c> can read or write it.
///   * <b>VACUUM.</b> Postgres refuses it inside a transaction block, and a
///     PL/pgSQL procedure body is always in one, so it cannot be wrapped as an
///     installed op at any nesting. It has to be issued by a client on a
///     connection that is not in a transaction — this one.
///
/// Everything else the console does — activity, cancel, reindex, analyze, evict —
/// IS an installed operation and goes through /v1/op against the live catalog,
/// which is why those are absent here.
///
/// NO PRIVILEGE BOUNDARY. These endpoints sit beside the rest of the surface and
/// inherit the deployment's auth mode; with auth stubbed they are open to anyone
/// who can reach the host, exactly like /v1/op. The console banner says the same.
/// </summary>
internal static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        // ---- agent routing ------------------------------------------------

        app.MapGet("/v1/admin/agents", () =>
        {
            try
            {
                var catalog = AgentCatalog.Load();
                return Results.Json(new JsonObject
                {
                    ["object"] = "agent.routes",
                    ["config"] = catalog.ConfigPath,
                    ["searched"] = new JsonArray(AgentCatalog.ConfigCandidates()
                        .Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
                    ["default"] = catalog.DefaultReference(),
                    ["secret_file"] = AgentProviders.SecretFile,
                    ["rows"] = new JsonArray(catalog.Describe().Select(d => (JsonNode)new JsonObject
                    {
                        ["name"] = d.Name,
                        ["provider"] = d.Provider,
                        ["model"] = d.Model,
                        ["base_url"] = d.BaseUrl,
                        // The VARIABLE NAME, never its value: this response is
                        // rendered in a browser and read by models.
                        ["key_env"] = d.KeyEnv,
                        ["credential_source"] = d.CredentialSource,
                        ["auth"] = d.Auth,
                        ["credentialed"] = d.Credentialed,
                        ["alias"] = d.IsAlias,
                        ["default"] = d.IsDefault,
                    }).ToArray()),
                });
            }
            catch (AgentException ex)
            {
                return EndpointJson.BadRequest("agent_config_error", ex.Message);
            }
        }).WithTags("admin");

        app.MapGet("/v1/admin/agents/config", () =>
        {
            var path = SafeDiscover(out var error);
            if (error is not null) return EndpointJson.BadRequest("agent_config_error", error);

            return Results.Json(new JsonObject
            {
                ["object"] = "agent.config",
                ["path"] = path,
                ["exists"] = path is not null && File.Exists(path),
                // A missing file is not an error — it is the state before the first
                // write, and the console needs a target path to write to.
                ["write_path"] = path ?? AgentCatalog.ConfigCandidates().FirstOrDefault(),
                ["content"] = path is not null && File.Exists(path) ? File.ReadAllText(path) : null,
            });
        }).WithTags("admin");

        app.MapPut("/v1/admin/agents/config", async (HttpRequest request, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var content = await reader.ReadToEndAsync(ct);
            if (string.IsNullOrWhiteSpace(content))
                return EndpointJson.BadRequest("invalid_request_error", "Body is empty.");

            // PARSE BEFORE WRITING. A config saved and only rejected on the next
            // `ask` breaks a lane that was working, at a moment nobody connects to
            // the edit. Parse() applies every rule the runtime applies — including
            // the refusal of an inline api_key.
            AgentCatalog parsed;
            try
            {
                parsed = AgentCatalog.Parse(content, configPath: null, AgentCatalog.DefaultEnvReader);
            }
            catch (AgentException ex)
            {
                return EndpointJson.BadRequest("agent_config_invalid", ex.Message);
            }

            var target = SafeDiscover(out _) ?? AgentCatalog.ConfigCandidates().FirstOrDefault();
            if (target is null)
                return EndpointJson.BadRequest("agent_config_error",
                    "No writable config location — set LAPLACE_AGENTS_CONFIG or LAPLACE_APP_DIR.");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // Write-then-move: a half-written agents.json is read per call by
                // every `ask`, so the file must never be observable mid-write.
                var temp = target + ".tmp";
                await File.WriteAllTextAsync(temp, content, ct);
                File.Move(temp, target, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return EndpointJson.BadRequest("agent_config_error",
                    $"could not write {target}: {ex.Message}");
            }

            return Results.Json(new JsonObject
            {
                ["object"] = "agent.config",
                ["path"] = target,
                ["written"] = true,
                ["routes"] = parsed.Describe().Count,
            });
        }).WithTags("admin");

        // Prove a route end to end from the console, with the same client the MCP
        // `ask` tool uses. Without this, "is this agent configured correctly?" is
        // only answerable by leaving the portal.
        app.MapPost("/v1/admin/agents/ask", async (JsonObject payload, CancellationToken ct) =>
        {
            var prompt = payload["prompt"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(prompt))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'prompt' is required.");

            try
            {
                var target = AgentCatalog.Load().Resolve(
                    payload["model"]?.GetValue<string>(),
                    payload["provider"]?.GetValue<string>());

                using var client = new ExternalAgentClient();
                var reply = await client.AskAsync(
                    target,
                    new AgentRequest(
                        prompt!,
                        payload["system"]?.GetValue<string>(),
                        payload["max_tokens"]?.GetValue<int>(),
                        payload["temperature"]?.GetValue<double>()),
                    TimeSpan.FromSeconds(Math.Clamp(
                        payload["timeout_seconds"]?.GetValue<int>() ?? 180, 1, 3600)),
                    ct);

                return Results.Json(new JsonObject
                {
                    ["object"] = "agent.reply",
                    ["agent"] = reply.Agent,
                    ["provider"] = reply.Provider,
                    ["model"] = reply.Model,
                    ["reply"] = reply.Text,
                    ["finish_reason"] = reply.FinishReason,
                    ["input_tokens"] = reply.InputTokens,
                    ["output_tokens"] = reply.OutputTokens,
                    ["attempts"] = reply.Attempts,
                    ["provider_ms"] = Math.Round(reply.LatencyMs, 1),
                    ["note"] = reply.Note,
                });
            }
            catch (AgentException ex)
            {
                return EndpointJson.BadRequest("agent_error", ex.Message);
            }
        }).WithTags("admin");

        // ---- op policy ----------------------------------------------------

        // Which installed operations may actually write, and which destroy
        // testimony. The console previously GUESSED this from a regex over the
        // operation name (`_close|_delete|_reset|…`), which is wrong in both
        // directions: ops.evict_source matches nothing in that pattern and is the
        // most destructive call on the surface, while any future ops.*_reset that
        // is not allow-listed would be badged as a write it cannot perform. The
        // server holds the real list; this hands it over rather than re-deriving it.
        app.MapGet("/v1/admin/ops/policy", () => Results.Json(new JsonObject
        {
            ["object"] = "op.policy",
            ["writable"] = new JsonArray(InstalledOpInvoker.WritableOps
                .OrderBy(n => n, StringComparer.Ordinal)
                .Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
            ["destructive"] = new JsonArray(InstalledOpInvoker.DestructiveOps
                .OrderBy(n => n, StringComparer.Ordinal)
                .Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
        })).WithTags("admin");

        // ---- maintenance SQL cannot express -------------------------------

        app.MapPost("/v1/admin/maintenance/vacuum", async (
            AdminPostgresDataSources dataSources,
            JsonObject payload,
            CancellationToken ct) =>
        {
            var table = payload["table"]?.GetValue<string>()?.Trim();
            var full = payload["full"]?.GetValue<bool>() ?? false;
            var analyze = payload["analyze"]?.GetValue<bool>() ?? true;
            var timeout = payload["timeout_seconds"]?.GetValue<int>() ?? 0;
            if (timeout < 0)
                return EndpointJson.BadRequest(
                    "invalid_request_error",
                    "timeout_seconds must be zero (unbounded) or a positive number of seconds.");

            var clock = System.Diagnostics.Stopwatch.StartNew();
            string sql;
            try
            {
                // The table name reaches the planner as an identifier, so it is
                // RESOLVED against the catalog rather than quoted and hoped for: the
                // lookup both refuses an unknown name and returns the schema-qualified
                // form, so the statement never depends on search_path.
                string? qualified = null;
                if (table is not null)
                {
                    qualified = await NpgsqlMaintenance.ResolveSubstrateTableAsync(
                        dataSources.Serving, table, ct);
                    if (qualified is null)
                        return EndpointJson.BadRequest("invalid_request_error",
                            $"'{table}' is not a table in the substrate schemas.");
                }

                // Ingest policy, not Serving: its timeout is unbounded and its
                // auto-prepare is off, which is what an hours-long VACUUM needs.
                sql = await NpgsqlMaintenance.VacuumAsync(
                    dataSources.Ingest, qualified, full, analyze,
                    timeout, ct);
            }
            catch (PostgresException ex)
            {
                return EndpointJson.BadRequest("substrate_error", $"[{ex.SqlState}] {ex.MessageText}");
            }
            catch (NpgsqlException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
            catch (TimeoutException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }

            return Results.Json(new JsonObject
            {
                ["object"] = "maintenance.result",
                ["statement"] = sql,
                ["elapsed_ms"] = Math.Round(clock.Elapsed.TotalMilliseconds, 1),
            });
        }).WithTags("admin");
    }

    /// <summary>
    /// A typo'd LAPLACE_AGENTS_CONFIG is an error on the call path — a caller who
    /// asked for that file must not silently get a different one. On the CONFIG
    /// endpoints it is reported instead of thrown, because that is the screen an
    /// operator opens to fix exactly this.
    /// </summary>
    private static string? SafeDiscover(out string? error)
    {
        error = null;
        try { return AgentCatalog.DiscoverConfigPath(); }
        catch (AgentException ex) { error = ex.Message; return null; }
    }

}