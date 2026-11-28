using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Laplace.Data;
using Laplace.Engine.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Reads structured JSONL progress streams emitted by <c>laplace ingest</c>
/// (<c>$LAPLACE_WORK/logs/ingest/runs/*.jsonl</c>), synchronizes them into
/// <c>app.ingest_runs</c> and <c>app.ingest_run_files</c>, and exposes unified
/// run and file receipts to the operator UI regardless of whether a run was
/// started from the UI, CLI, CI, or MCP.
/// </summary>
internal sealed class IngestJournalService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<IngestJournalService> _logger;
    private readonly ConcurrentDictionary<string, JournalFileState> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _scanGate = new();
    private bool _appTablesWarned;

    private sealed class JournalFileState
    {
        public string Path { get; init; } = "";
        public long Offset { get; set; }
        public DateTime LastWriteUtc { get; set; }
        public bool Dirty { get; set; }
        public RunState Run { get; } = new();
        public Dictionary<string, FileState> Files { get; } = new(StringComparer.Ordinal);
    }

    internal sealed class RunState
    {
        public Guid RunId { get; set; }
        public string SourceName { get; set; } = "";
        public string? SourceId { get; set; }
        public string? Host { get; set; }
        public int? Pid { get; set; }
        public string Status { get; set; } = "running";
        public string? Phase { get; set; }
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? EndedAt { get; set; }
        public long FilesDone { get; set; }
        public long FilesTotal { get; set; }
        public long BytesDone { get; set; }
        public long BytesTotal { get; set; }
        public long Entities { get; set; }
        public long Physicalities { get; set; }
        public long Attestations { get; set; }
        public bool AttestationsMeasured { get; set; }
        public long ElapsedMs { get; set; }
        public string? Error { get; set; }
        public string? JournalPath { get; set; }
    }

    internal sealed class FileState
    {
        public Guid RunId { get; set; }
        public string FileLabel { get; set; } = "";
        public string SourceName { get; set; } = "";
        public string? FileId { get; set; }
        public string Status { get; set; } = "running";
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? EndedAt { get; set; }
        public long Bytes { get; set; }
        public long Records { get; set; }
        public long Entities { get; set; }
        public long Physicalities { get; set; }
        public long Attestations { get; set; }
        public string? Error { get; set; }
    }

    public IngestJournalService(IServiceProvider services, ILogger<IngestJournalService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public static IReadOnlyList<string> ResolveRunsDirectories()
    {
        var dirs = new List<string>();
        var explicitDir = Environment.GetEnvironmentVariable("LAPLACE_INGEST_RUNS_DIR");
        if (!string.IsNullOrWhiteSpace(explicitDir))
            dirs.Add(explicitDir.Trim());

        var work = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_WORK")
            ?? LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_WORK_ROOT");
        if (!string.IsNullOrWhiteSpace(work))
        {
            var trimmed = work.Trim();
            dirs.Add(Path.Combine(trimmed, "logs", "ingest", "runs"));

            var clientsRoot = Path.Combine(trimmed, "clients");
            if (Directory.Exists(clientsRoot))
            {
                try
                {
                    foreach (var userDir in Directory.GetDirectories(clientsRoot))
                    {
                        dirs.Add(Path.Combine(userDir, "laplace", "logs", "ingest", "runs"));
                        dirs.Add(Path.Combine(userDir, "logs", "ingest", "runs"));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        var opsLogs = Environment.GetEnvironmentVariable("LAPLACE_OPS_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(opsLogs))
        {
            var opsTrimmed = opsLogs.Trim();
            dirs.Add(Path.Combine(opsTrimmed, "ingest", "runs"));
            if (Path.GetDirectoryName(Path.GetFullPath(opsTrimmed)) is { Length: > 0 } stateRoot)
                dirs.Add(Path.Combine(stateRoot, "logs", "ingest", "runs"));
        }

        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void ScanDiskJournals()
    {
        var runsDirs = ResolveRunsDirectories();
        if (runsDirs.Count == 0) return;

        lock (_scanGate)
        {
            var files = new List<string>();
            foreach (var runsDir in runsDirs)
            {
                if (!Directory.Exists(runsDir)) continue;
                try
                {
                    files.AddRange(Directory.GetFiles(runsDir, "*.jsonl", SearchOption.TopDirectoryOnly));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            var hostName = Environment.MachineName;
            foreach (var filePath in files)
            {
                try
                {
                    var fi = new FileInfo(filePath);
                    if (!fi.Exists || fi.Length == 0) continue;

                    var state = _cache.GetOrAdd(filePath, p => new JournalFileState { Path = p });
                    if (fi.Length < state.Offset)
                    {
                        // File was truncated or replaced; re-read from start.
                        state.Offset = 0;
                        state.Files.Clear();
                    }

                    if (fi.Length > state.Offset)
                    {
                        ReadIncremental(state, fi);
                        state.LastWriteUtc = fi.LastWriteTimeUtc;
                        state.Dirty = true;
                    }

                    // Detect orphaned local runs whose process died without emitting a terminal frame.
                    if (state.Run.RunId != Guid.Empty
                        && string.Equals(state.Run.Status, "running", StringComparison.OrdinalIgnoreCase)
                        && state.Run.Pid is > 0
                        && string.Equals(state.Run.Host, hostName, StringComparison.OrdinalIgnoreCase)
                        && (DateTime.UtcNow - state.LastWriteUtc) > TimeSpan.FromSeconds(15)
                        && !IsProcessAlive(state.Run.Pid.Value))
                    {
                        state.Run.Status = "failed";
                        state.Run.Phase = "orphaned";
                        state.Run.EndedAt = DateTimeOffset.UtcNow;
                        state.Run.UpdatedAt = DateTimeOffset.UtcNow;
                        state.Run.Error ??= $"Process {state.Run.Pid.Value} exited without writing a completion frame.";
                        state.Dirty = true;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    _logger.LogDebug(ex, "Skipping unreadable ingest journal {Path}", filePath);
                }
            }
        }
    }

    private static void ReadIncremental(JournalFileState state, FileInfo fi)
    {
        using var stream = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(state.Offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 16384, leaveOpen: true);

        long lastGoodPosition = state.Offset;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            // Only advance offset through complete lines ending with '\n'.
            // If we hit EOF mid-write, leave the trailing partial line for the next scan.
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                ApplyFrame(state, doc.RootElement, fi.FullName, fi.CreationTimeUtc);
                lastGoodPosition = stream.Position;
            }
            catch (JsonException)
            {
                // Incomplete JSON line currently being flushed; retry on next poll.
                break;
            }
        }
        state.Offset = lastGoodPosition;
    }

    private static void ApplyFrame(JournalFileState state, JsonElement root, string filePath, DateTime createdUtc)
    {
        var run = state.Run;
        run.JournalPath = filePath;

        if (run.RunId == Guid.Empty)
        {
            if (TryGetString(root, "run_id", out var rid) && Guid.TryParse(rid, out var parsedId))
                run.RunId = parsedId;
            else if (Guid.TryParse(Path.GetFileNameWithoutExtension(filePath), out var fileId))
                run.RunId = fileId;
            else
                return;
        }

        var ts = TryGetTimestamp(root, "ts") ?? new DateTimeOffset(DateTime.SpecifyKind(createdUtc, DateTimeKind.Utc));
        if (run.SourceName.Length == 0)
            run.StartedAt = ts;
        run.UpdatedAt = ts;

        if (TryGetString(root, "source", out var src) && !string.IsNullOrWhiteSpace(src))
            run.SourceName = src;
        if (TryGetString(root, "source_id", out var sid) && !string.IsNullOrWhiteSpace(sid))
            run.SourceId = sid;
        if (TryGetString(root, "host", out var host) && !string.IsNullOrWhiteSpace(host))
            run.Host = host;
        if (TryGetInt(root, "pid", out var pid))
            run.Pid = pid;
        if (TryGetLong(root, "files_total", out var ft)) run.FilesTotal = ft;
        if (TryGetLong(root, "files_done", out var fd)) run.FilesDone = fd;
        if (TryGetLong(root, "bytes_done", out var bd)) run.BytesDone = bd;
        if (TryGetLong(root, "bytes_total", out var bt)) run.BytesTotal = bt;
        if (TryGetLong(root, "entities", out var ent)) run.Entities = ent;
        if (TryGetLong(root, "physicalities", out var phy)) run.Physicalities = phy;
        if (TryGetLong(root, "attestations", out var att)) run.Attestations = att;
        if (root.TryGetProperty("attestations_measured", out var am) && am.ValueKind is JsonValueKind.True or JsonValueKind.False)
            run.AttestationsMeasured = am.GetBoolean();
        if (TryGetLong(root, "elapsed_ms", out var ems)) run.ElapsedMs = ems;
        if (TryGetString(root, "error", out var err) && !string.IsNullOrWhiteSpace(err))
            run.Error = err;

        var ev = TryGetString(root, "event", out var evStr) ? evStr : "";
        switch (ev)
        {
            case "inventory":
                run.Status = "running";
                run.Phase = "inventory";
                run.StartedAt = ts;
                break;
            case "file-start":
                run.Status = "running";
                run.Phase = "decomposing";
                UpsertFile(state, root, "running", ts, ended: false);
                break;
            case "file-composed":
                run.Status = "running";
                run.Phase = "loading";
                UpsertFile(state, root, "composed", ts, ended: true);
                break;
            case "file-known":
                run.Phase ??= "deduplicating";
                UpsertFile(state, root, "known", ts, ended: true);
                break;
            case "progress":
                run.Status = "running";
                run.Phase = "streaming";
                UpsertFile(state, root, "running", ts, ended: false);
                break;
            case "source-trunk":
                run.Status = "running";
                run.Phase = "source-trunk";
                break;
            case "source-publication":
                run.Status = "running";
                run.Phase = "source-publication";
                break;
            case "complete":
                run.Status = "completed";
                run.Phase = "complete";
                run.EndedAt = ts;
                break;
            case "failed":
                run.Status = "failed";
                run.Phase = "failed";
                run.EndedAt = ts;
                break;
            case "cancelled":
                run.Status = "cancelled";
                run.Phase = "cancelled";
                run.EndedAt = ts;
                break;
        }
    }

    private static void UpsertFile(JournalFileState state, JsonElement root, string status, DateTimeOffset ts, bool ended)
    {
        if (!TryGetString(root, "file", out var label) || string.IsNullOrWhiteSpace(label)) return;
        if (!state.Files.TryGetValue(label, out var fs))
        {
            fs = new FileState
            {
                RunId = state.Run.RunId,
                FileLabel = label,
                SourceName = state.Run.SourceName,
                StartedAt = ts,
            };
            state.Files[label] = fs;
        }
        fs.Status = status;
        if (ended) fs.EndedAt = ts;
        if (TryGetString(root, "file_id", out var fid) && !string.IsNullOrWhiteSpace(fid)) fs.FileId = fid;
        if (TryGetLong(root, "file_bytes", out var fb)) fs.Bytes = fb;
        if (TryGetLong(root, "records", out var rec)) fs.Records = rec;
        if (TryGetString(root, "error", out var err) && !string.IsNullOrWhiteSpace(err)) fs.Error = err;
    }

    public async Task SyncDirtyToAppDbAsync(CancellationToken ct)
    {
        ScanDiskJournals();
        var appDs = _services.GetKeyedService<NpgsqlDataSource>(AppComposition.ApplicationDatabaseKey);
        if (appDs is null) return;

        List<JournalFileState> dirty;
        lock (_scanGate)
        {
            dirty = _cache.Values.Where(s => s.Dirty && s.Run.RunId != Guid.Empty).ToList();
        }
        if (dirty.Count == 0) return;

        try
        {
            await using var conn = await appDs.OpenConnectionAsync(ct).ConfigureAwait(false);
            foreach (var state in dirty)
            {
                RunState run;
                List<FileState> files;
                lock (_scanGate)
                {
                    run = CloneRun(state.Run);
                    files = state.Files.Values.Select(CloneFile).ToList();
                }

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = """
                        INSERT INTO app.ingest_runs (
                            run_id, source_name, source_id, host, pid, status, phase,
                            started_at, updated_at, ended_at,
                            files_done, files_total, bytes_done, bytes_total,
                            entities, physicalities, attestations, attestations_measured,
                            elapsed_ms, error, journal_path
                        ) VALUES (
                            $1, $2, $3, $4, $5, $6, $7,
                            $8, $9, $10,
                            $11, $12, $13, $14,
                            $15, $16, $17, $18,
                            $19, $20, $21
                        )
                        ON CONFLICT (run_id) DO UPDATE SET
                            source_name = EXCLUDED.source_name,
                            source_id = COALESCE(EXCLUDED.source_id, app.ingest_runs.source_id),
                            host = COALESCE(EXCLUDED.host, app.ingest_runs.host),
                            pid = COALESCE(EXCLUDED.pid, app.ingest_runs.pid),
                            status = CASE
                                WHEN app.ingest_runs.status = 'cancelled' THEN 'cancelled'
                                ELSE EXCLUDED.status
                            END,
                            phase = EXCLUDED.phase,
                            updated_at = EXCLUDED.updated_at,
                            ended_at = COALESCE(app.ingest_runs.ended_at, EXCLUDED.ended_at),
                            files_done = GREATEST(app.ingest_runs.files_done, EXCLUDED.files_done),
                            files_total = GREATEST(app.ingest_runs.files_total, EXCLUDED.files_total),
                            bytes_done = GREATEST(app.ingest_runs.bytes_done, EXCLUDED.bytes_done),
                            bytes_total = GREATEST(app.ingest_runs.bytes_total, EXCLUDED.bytes_total),
                            entities = GREATEST(app.ingest_runs.entities, EXCLUDED.entities),
                            physicalities = GREATEST(app.ingest_runs.physicalities, EXCLUDED.physicalities),
                            attestations = GREATEST(app.ingest_runs.attestations, EXCLUDED.attestations),
                            attestations_measured = app.ingest_runs.attestations_measured OR EXCLUDED.attestations_measured,
                            elapsed_ms = GREATEST(app.ingest_runs.elapsed_ms, EXCLUDED.elapsed_ms),
                            error = COALESCE(EXCLUDED.error, app.ingest_runs.error),
                            journal_path = COALESCE(EXCLUDED.journal_path, app.ingest_runs.journal_path);
                        """;
                    cmd.Parameters.AddWithValue(run.RunId);
                    cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(run.SourceName) ? "unknown" : run.SourceName);
                    cmd.Parameters.AddWithValue((object?)run.SourceId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue((object?)run.Host ?? DBNull.Value);
                    cmd.Parameters.AddWithValue((object?)run.Pid ?? DBNull.Value);
                    cmd.Parameters.AddWithValue(run.Status);
                    cmd.Parameters.AddWithValue((object?)run.Phase ?? DBNull.Value);
                    cmd.Parameters.AddWithValue(run.StartedAt);
                    cmd.Parameters.AddWithValue(run.UpdatedAt);
                    cmd.Parameters.AddWithValue((object?)run.EndedAt ?? DBNull.Value);
                    cmd.Parameters.AddWithValue(run.FilesDone);
                    cmd.Parameters.AddWithValue(run.FilesTotal);
                    cmd.Parameters.AddWithValue(run.BytesDone);
                    cmd.Parameters.AddWithValue(run.BytesTotal);
                    cmd.Parameters.AddWithValue(run.Entities);
                    cmd.Parameters.AddWithValue(run.Physicalities);
                    cmd.Parameters.AddWithValue(run.Attestations);
                    cmd.Parameters.AddWithValue(run.AttestationsMeasured);
                    cmd.Parameters.AddWithValue(run.ElapsedMs);
                    cmd.Parameters.AddWithValue((object?)run.Error ?? DBNull.Value);
                    cmd.Parameters.AddWithValue((object?)run.JournalPath ?? DBNull.Value);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                foreach (var file in files)
                {
                    await using var fcmd = conn.CreateCommand();
                    fcmd.CommandText = """
                        INSERT INTO app.ingest_run_files (
                            run_id, file_label, source_name, file_id, status,
                            started_at, ended_at, bytes, records, entities, physicalities, attestations, error
                        ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
                        ON CONFLICT (run_id, file_label) DO UPDATE SET
                            file_id = COALESCE(EXCLUDED.file_id, app.ingest_run_files.file_id),
                            status = EXCLUDED.status,
                            ended_at = COALESCE(EXCLUDED.ended_at, app.ingest_run_files.ended_at),
                            bytes = GREATEST(app.ingest_run_files.bytes, EXCLUDED.bytes),
                            records = GREATEST(app.ingest_run_files.records, EXCLUDED.records),
                            error = COALESCE(EXCLUDED.error, app.ingest_run_files.error);
                        """;
                    fcmd.Parameters.AddWithValue(file.RunId);
                    fcmd.Parameters.AddWithValue(file.FileLabel);
                    fcmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(file.SourceName) ? run.SourceName : file.SourceName);
                    fcmd.Parameters.AddWithValue((object?)file.FileId ?? DBNull.Value);
                    fcmd.Parameters.AddWithValue(file.Status);
                    fcmd.Parameters.AddWithValue(file.StartedAt);
                    fcmd.Parameters.AddWithValue((object?)file.EndedAt ?? DBNull.Value);
                    fcmd.Parameters.AddWithValue(file.Bytes);
                    fcmd.Parameters.AddWithValue(file.Records);
                    fcmd.Parameters.AddWithValue(file.Entities);
                    fcmd.Parameters.AddWithValue(file.Physicalities);
                    fcmd.Parameters.AddWithValue(file.Attestations);
                    fcmd.Parameters.AddWithValue((object?)file.Error ?? DBNull.Value);
                    await fcmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                lock (_scanGate) { state.Dirty = false; }
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            if (!_appTablesWarned)
            {
                _appTablesWarned = true;
                _logger.LogWarning("app.ingest_runs is not migrated yet; serving live ingest runs directly from JSONL files.");
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            _logger.LogDebug(ex, "Transient error syncing ingest journals to app.ingest_runs");
        }
    }

    public async Task<JsonArray> ListRunsAsync(int limit, ISubstrateClient? substrate, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 500);
        ScanDiskJournals();

        var byRunId = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var coveredSourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Read persisted runs from app.ingest_runs when available.
        var appDs = _services.GetKeyedService<NpgsqlDataSource>(AppComposition.ApplicationDatabaseKey);
        if (appDs is not null)
        {
            try
            {
                await using var conn = await appDs.OpenConnectionAsync(ct).ConfigureAwait(false);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT run_id::text, source_name, source_id, status, phase,
                           started_at, ended_at, files_done, files_total,
                           bytes_done, bytes_total, entities, physicalities,
                           attestations, attestations_measured, elapsed_ms, error
                    FROM app.ingest_runs
                    ORDER BY (status = 'running') DESC, started_at DESC
                    LIMIT $1;
                    """;
                cmd.Parameters.AddWithValue(limit);
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var runId = r.GetString(0);
                    var sourceId = r.IsDBNull(2) ? null : r.GetString(2);
                    if (!string.IsNullOrWhiteSpace(sourceId)) coveredSourceIds.Add(sourceId);
                    byRunId[runId] = BuildRunJson(
                        runId: runId,
                        sourceName: r.GetString(1),
                        sourceId: sourceId,
                        status: r.GetString(3),
                        phase: r.IsDBNull(4) ? null : r.GetString(4),
                        startedAt: r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
                        endedAt: r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
                        filesDone: r.GetInt64(7),
                        filesTotal: r.GetInt64(8),
                        bytesDone: r.GetInt64(9),
                        bytesTotal: r.GetInt64(10),
                        entities: r.GetInt64(11),
                        physicalities: r.GetInt64(12),
                        attestations: r.GetInt64(13),
                        attestationsMeasured: r.GetBoolean(14),
                        elapsedMs: r.GetInt64(15),
                        error: r.IsDBNull(16) ? null : r.GetString(16));
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                _logger.LogDebug(ex, "app.ingest_runs unavailable; falling back to disk journals");
            }
        }

        // 2. Overlay fresh in-memory JSONL runs so live progress is always sub-second fresh.
        lock (_scanGate)
        {
            foreach (var state in _cache.Values)
            {
                var run = state.Run;
                if (run.RunId == Guid.Empty) continue;
                var id = run.RunId.ToString("D");
                if (!string.IsNullOrWhiteSpace(run.SourceId)) coveredSourceIds.Add(run.SourceId);
                // Preserve cancelled state if DB already closed it.
                var status = byRunId.TryGetValue(id, out var existing)
                    && string.Equals(existing["status"]?.GetValue<string>(), "cancelled", StringComparison.OrdinalIgnoreCase)
                    ? "cancelled"
                    : run.Status;
                byRunId[id] = BuildRunJson(
                    runId: id,
                    sourceName: string.IsNullOrWhiteSpace(run.SourceName) ? "ingest" : run.SourceName,
                    sourceId: run.SourceId,
                    status: status,
                    phase: run.Phase,
                    startedAt: run.StartedAt,
                    endedAt: run.EndedAt,
                    filesDone: run.FilesDone,
                    filesTotal: run.FilesTotal,
                    bytesDone: run.BytesDone,
                    bytesTotal: run.BytesTotal,
                    entities: run.Entities,
                    physicalities: run.Physicalities,
                    attestations: run.Attestations,
                    attestationsMeasured: run.AttestationsMeasured,
                    elapsedMs: run.ElapsedMs,
                    error: run.Error);
            }
        }

        // 3. Append admitted substrate witnesses that pre-date JSONL journaling so historical data is visible.
        if (byRunId.Count < limit && substrate is SubstrateClient realSubstrate)
        {
            try
            {
                await using var conn = await realSubstrate.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
                await using var cmd = conn.CreateCommand();
                cmd.CommandTimeout = 5;
                cmd.CommandText = """
                    SELECT encode(public.blake3_send(w.witness), 'hex') AS witness_hex,
                           coalesce(nullif(w.name, ''), encode(public.blake3_send(w.witness), 'hex')) AS src_name,
                           w.parts, w.readings
                    FROM public.laplace_witnesses_named() w
                    ORDER BY w.readings DESC NULLS LAST, w.name
                    LIMIT $1;
                    """;
                cmd.Parameters.AddWithValue(limit);
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var witnessHex = r.GetString(0);
                    if (coveredSourceIds.Contains(witnessHex) || byRunId.ContainsKey(witnessHex)) continue;
                    var parts = r.IsDBNull(2) ? 0L : r.GetInt64(2);
                    var readings = r.IsDBNull(3) ? 0L : r.GetInt64(3);
                    byRunId[witnessHex] = BuildRunJson(
                        runId: witnessHex,
                        sourceName: r.GetString(1),
                        sourceId: witnessHex,
                        status: "completed",
                        phase: "admitted",
                        startedAt: null,
                        endedAt: null,
                        filesDone: null,
                        filesTotal: null,
                        bytesDone: null,
                        bytesTotal: null,
                        entities: parts,
                        physicalities: parts,
                        attestations: readings,
                        attestationsMeasured: readings > 0,
                        elapsedMs: 0,
                        error: null);
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                _logger.LogDebug(ex, "Substrate witness fallback unavailable while listing ingest runs");
            }
        }

        var ordered = byRunId.Values
            .OrderByDescending(o => string.Equals(o["status"]?.GetValue<string>(), "running", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(o => o["started_at"]?.GetValue<string>() ?? "")
            .ThenByDescending(o => o["attestations"]?.GetValue<long>() ?? 0L)
            .Take(limit)
            .ToArray();

        return new JsonArray(ordered);
    }

    public async Task<JsonArray> ListFilesAsync(string runId, int limit, ISubstrateClient? substrate, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 5000);
        ScanDiskJournals();
        var rows = new List<JsonObject>();

        if (Guid.TryParse(runId, out var guid))
        {
            var byLabel = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            var appDs = _services.GetKeyedService<NpgsqlDataSource>(AppComposition.ApplicationDatabaseKey);
            if (appDs is not null)
            {
                try
                {
                    await using var conn = await appDs.OpenConnectionAsync(ct).ConfigureAwait(false);
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                        SELECT run_id::text, file_label, source_name, file_id, status,
                               started_at, ended_at, bytes, records, entities, physicalities, attestations, error
                        FROM app.ingest_run_files
                        WHERE run_id = $1
                        ORDER BY (status IN ('running', 'failed')) DESC, started_at DESC
                        LIMIT $2;
                        """;
                    cmd.Parameters.AddWithValue(guid);
                    cmd.Parameters.AddWithValue(limit);
                    await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await r.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var label = r.GetString(1);
                        byLabel[label] = BuildFileJson(
                            r.GetString(0), label, r.GetString(2),
                            r.IsDBNull(3) ? null : r.GetString(3),
                            r.GetString(4),
                            r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
                            r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
                            r.GetInt64(7), r.GetInt64(8), r.GetInt64(9), r.GetInt64(10), r.GetInt64(11),
                            r.IsDBNull(12) ? null : r.GetString(12));
                    }
                }
                catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
                {
                    _logger.LogDebug(ex, "app.ingest_run_files unavailable; falling back to disk journal");
                }
            }

            lock (_scanGate)
            {
                var cached = _cache.Values.FirstOrDefault(s => s.Run.RunId == guid);
                if (cached is not null)
                {
                    foreach (var fs in cached.Files.Values)
                    {
                        byLabel[fs.FileLabel] = BuildFileJson(
                            guid.ToString("D"), fs.FileLabel, fs.SourceName, fs.FileId, fs.Status,
                            fs.StartedAt, fs.EndedAt, fs.Bytes, fs.Records, fs.Entities, fs.Physicalities, fs.Attestations, fs.Error);
                    }
                }
            }

            rows.AddRange(byLabel.Values
                .OrderByDescending(o =>
                {
                    var s = o["status"]?.GetValue<string>() ?? "";
                    return s is "running" or "failed" ? 1 : 0;
                })
                .ThenBy(o => o["file_label"]?.GetValue<string>() ?? "")
                .Take(limit));
            return new JsonArray(rows.ToArray());
        }

        // Historical 32-hex witness ID fallback.
        if (substrate is SubstrateClient realSubstrate)
        {
            try
            {
                await using var conn = await realSubstrate.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
                await using var cmd = conn.CreateCommand();
                cmd.CommandTimeout = 5;
                cmd.CommandText = """
                    SELECT encode(public.blake3_send(w.witness), 'hex'),
                           coalesce(nullif(w.name, ''), encode(public.blake3_send(w.witness), 'hex')),
                           encode(public.blake3_send(w.record_id), 'hex'),
                           w.parts, w.readings
                    FROM public.laplace_witnesses_named() w
                    WHERE encode(public.blake3_send(w.witness), 'hex') = lower($1)
                       OR lower(coalesce(w.name, '')) = lower($1)
                    LIMIT $2;
                    """;
                cmd.Parameters.AddWithValue(runId);
                cmd.Parameters.AddWithValue(limit);
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var name = r.GetString(1);
                    var parts = r.IsDBNull(3) ? 0L : r.GetInt64(3);
                    var readings = r.IsDBNull(4) ? 0L : r.GetInt64(4);
                    rows.Add(BuildFileJson(
                        r.GetString(0), name, name, r.IsDBNull(2) ? null : r.GetString(2),
                        "admitted", null, null, null, readings, parts, parts, readings, null));
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                _logger.LogDebug(ex, "Substrate witness file fallback failed for {RunId}", runId);
            }
        }
        return new JsonArray(rows.ToArray());
    }

    public async Task<JsonObject> CloseRunAsync(Guid runId, string status, CancellationToken ct)
    {
        status = string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ? "failed" : "cancelled";
        var now = DateTimeOffset.UtcNow;
        bool closed = false;

        lock (_scanGate)
        {
            var cached = _cache.Values.FirstOrDefault(s => s.Run.RunId == runId);
            if (cached is not null)
            {
                cached.Run.Status = status;
                cached.Run.Phase = status;
                cached.Run.EndedAt ??= now;
                cached.Run.UpdatedAt = now;
                cached.Dirty = true;
                closed = true;
            }
        }

        var appDs = _services.GetKeyedService<NpgsqlDataSource>(AppComposition.ApplicationDatabaseKey);
        if (appDs is not null)
        {
            try
            {
                await using var conn = await appDs.OpenConnectionAsync(ct).ConfigureAwait(false);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE app.ingest_runs
                    SET status = $2,
                        phase = $2,
                        ended_at = COALESCE(ended_at, now()),
                        updated_at = now()
                    WHERE run_id = $1;
                    """;
                cmd.Parameters.AddWithValue(runId);
                cmd.Parameters.AddWithValue(status);
                var updated = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (updated > 0) closed = true;
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                _logger.LogWarning(ex, "Unable to update app.ingest_runs while closing {RunId}", runId);
            }
        }

        return new JsonObject
        {
            ["run_id"] = runId.ToString("D"),
            ["status"] = status,
            ["closed"] = closed,
            ["detail"] = closed ? "Ingest run journal marked closed." : "Run ID not found in active or persisted journals.",
        };
    }

    private static JsonObject BuildRunJson(
        string runId, string sourceName, string? sourceId, string status, string? phase,
        DateTimeOffset? startedAt, DateTimeOffset? endedAt,
        long? filesDone, long? filesTotal, long? bytesDone, long? bytesTotal,
        long entities, long physicalities, long attestations, bool attestationsMeasured,
        long elapsedMs, string? error)
    {
        double? rate = null;
        long totalRows = entities + physicalities;
        if (elapsedMs > 0 && totalRows > 0)
            rate = Math.Round(totalRows / (elapsedMs / 1000.0), 1);

        return new JsonObject
        {
            ["run_id"] = runId,
            ["source_name"] = sourceName,
            ["source_id"] = sourceId,
            ["layer"] = null,
            ["status"] = status,
            ["phase"] = phase,
            ["started_at"] = startedAt?.ToString("O"),
            ["ended_at"] = endedAt?.ToString("O"),
            ["units_attempted"] = filesTotal,
            ["units_applied"] = filesDone,
            ["units_failed"] = string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ? 1L : 0L,
            ["entities"] = entities,
            ["physicalities"] = physicalities,
            ["attestations"] = attestations,
            ["attestations_measured"] = attestationsMeasured,
            ["file_counters_measured"] = true,
            ["files_done"] = filesDone,
            ["files_total"] = filesTotal,
            ["input_units_done"] = bytesDone,
            ["input_units_total"] = bytesTotal,
            ["fold_drain_ms"] = null,
            ["writer_maintenance_ms"] = null,
            ["throughput_rows"] = totalRows > 0 ? totalRows : null,
            ["throughput_elapsed_ms"] = elapsedMs > 0 ? elapsedMs : null,
            ["throughput_rows_per_s"] = rate,
            ["throughput_baseline_rows_per_s"] = null,
            ["throughput_slowdown_ratio"] = null,
            ["throughput_compared"] = false,
            ["throughput_status"] = null,
            ["evidence_persisted"] = attestations > 0 || string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase),
            ["error"] = error,
        };
    }

    private static JsonObject BuildFileJson(
        string runId, string fileLabel, string sourceName, string? fileId, string status,
        DateTimeOffset? startedAt, DateTimeOffset? endedAt,
        long? bytes, long? records, long? entities, long? physicalities, long? attestations, string? error) => new()
    {
        ["run_id"] = runId,
        ["file_label"] = fileLabel,
        ["source_name"] = sourceName,
        ["file_id"] = fileId,
        ["status"] = status,
        ["started_at"] = startedAt?.ToString("O"),
        ["ended_at"] = endedAt?.ToString("O"),
        ["bytes"] = bytes,
        ["records"] = records,
        ["entities"] = entities,
        ["physicalities"] = physicalities,
        ["attestations"] = attestations,
        ["error"] = error,
    };

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetString(JsonElement el, string name, out string value)
    {
        if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString() ?? "";
            return true;
        }
        value = "";
        return false;
    }

    private static bool TryGetInt(JsonElement el, string name, out int value)
    {
        if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out value))
            return true;
        value = 0;
        return false;
    }

    private static bool TryGetLong(JsonElement el, string name, out long value)
    {
        if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out value))
            return true;
        value = 0;
        return false;
    }

    private static DateTimeOffset? TryGetTimestamp(JsonElement el, string name)
    {
        if (TryGetString(el, name, out var raw)
            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;
        return null;
    }

    private static RunState CloneRun(RunState s) => new()
    {
        RunId = s.RunId, SourceName = s.SourceName, SourceId = s.SourceId, Host = s.Host, Pid = s.Pid,
        Status = s.Status, Phase = s.Phase, StartedAt = s.StartedAt, UpdatedAt = s.UpdatedAt, EndedAt = s.EndedAt,
        FilesDone = s.FilesDone, FilesTotal = s.FilesTotal, BytesDone = s.BytesDone, BytesTotal = s.BytesTotal,
        Entities = s.Entities, Physicalities = s.Physicalities, Attestations = s.Attestations,
        AttestationsMeasured = s.AttestationsMeasured, ElapsedMs = s.ElapsedMs, Error = s.Error, JournalPath = s.JournalPath,
    };

    private static FileState CloneFile(FileState s) => new()
    {
        RunId = s.RunId, FileLabel = s.FileLabel, SourceName = s.SourceName, FileId = s.FileId, Status = s.Status,
        StartedAt = s.StartedAt, EndedAt = s.EndedAt, Bytes = s.Bytes, Records = s.Records,
        Entities = s.Entities, Physicalities = s.Physicalities, Attestations = s.Attestations, Error = s.Error,
    };
}

internal sealed class IngestJournalSyncService : BackgroundService
{
    private readonly IngestJournalService _journal;

    public IngestJournalSyncService(IngestJournalService journal) => _journal = journal;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        await _journal.SyncDirtyToAppDbAsync(stoppingToken).ConfigureAwait(false);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await _journal.SyncDirtyToAppDbAsync(stoppingToken).ConfigureAwait(false);
        }
    }
}
