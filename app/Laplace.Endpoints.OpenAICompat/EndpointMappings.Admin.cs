using System.Text.Json.Nodes;
using Laplace.Agents;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Admin endpoints for work that is not an installed SQL operation and so cannot go
/// through <c>POST /v1/op</c>:
///
///   * <b>Agent routing.</b> <c>agents.json</c> is a host file, not substrate state;
///     <c>ops.api()</c> cannot read or write it.
///   * <b>VACUUM.</b> PostgreSQL refuses it inside a transaction block, and every
///     procedure body is in one, so a client issues it on a connection outside a
///     transaction.
///
/// These endpoints carry no privilege boundary of their own; they inherit the
/// deployment's auth mode, as /v1/op does.
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
                    ["defaults"] = AgentCatalog.DefaultsSource,
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
                        // The environment variable's name, never its value.
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
                // A missing file is the state before the first write; write_path
                // names where the PUT will create it.
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

            // The body is parsed with the runtime's rules (including refusal of an
            // inline api_key) before anything is written.
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
                // Write-then-move: every `ask` reads agents.json per call, so a
                // partial file is never observable.
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

        // Sends one prompt through a configured route with the same client the MCP
        // `ask` tool uses.
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

        // The server's own lists of installed operations that may write and that
        // destroy testimony, as InstalledOpInvoker enforces them.
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
                // The table name is resolved against the catalog: an unknown name is
                // refused, and the schema-qualified form means the statement does not
                // depend on search_path.
                string? qualified = null;
                if (table is not null)
                {
                    qualified = await NpgsqlMaintenance.ResolveSubstrateTableAsync(
                        dataSources.Serving, table, ct);
                    if (qualified is null)
                        return EndpointJson.BadRequest("invalid_request_error",
                            $"'{table}' is not a table in the substrate schemas.");
                }

                // The Ingest datasource: unbounded timeout and no auto-prepare.
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
    /// Config discovery that returns an unresolvable LAPLACE_AGENTS_CONFIG as
    /// <paramref name="error"/> instead of throwing, so the config endpoints can report it.
    /// </summary>
    private static string? SafeDiscover(out string? error)
    {
        error = null;
        try { return AgentCatalog.DiscoverConfigPath(); }
        catch (AgentException ex) { error = ex.Message; return null; }
    }

}
