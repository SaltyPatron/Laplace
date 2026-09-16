using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>
/// One explicit calculated-source version transition, run while existing managed
/// database maintenance owns writer quiescence. Literal playing content remains
/// authoritative; the ordinary source eviction/refold and ingest runner do the work.
/// </summary>
public static class ChessPositionOutcomesMigration
{
    public sealed record Receipt(string Disposition, string Directory, long SelectedPlayings,
        long HydratedPlayings, long InputUnitsDone,
        NpgsqlSubstrateReads.SourceObservationInventory Before,
        NpgsqlSubstrateReads.SourceObservationInventory After);
    private sealed record Pending(string Database, string Archive, string ArchiveSha256);
    private const int PageSize = 256;

    public static async Task<Receipt> RunAsync(NpgsqlDataSource ds, string evidenceRoot,
        long maximumRetainedBytes = 512L * 1024 * 1024, CancellationToken ct = default,
        string invocationLabel = "invocation")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRetainedBytes, 65536);
        evidenceRoot = Path.GetFullPath(evidenceRoot);
        if (string.IsNullOrWhiteSpace(invocationLabel) || invocationLabel is "." or ".."
            || Path.GetFileName(invocationLabel) != invocationLabel)
            throw new ArgumentException("Migration invocation label must be one directory name.", nameof(invocationLabel));
        Directory.CreateDirectory(evidenceRoot);
        SyncDirectory(Path.GetDirectoryName(evidenceRoot)!);
        // Every invocation uses the same source-owned lock/pending receipt, including
        // retries after a bounded-batch source eviction was interrupted.
        using var ownership = new FileStream(Path.Combine(evidenceRoot, "migration.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string directory = Path.Combine(evidenceRoot, invocationLabel + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SyncDirectory(evidenceRoot);
        var reader = new NpgsqlSubstrateReader(ds);
        var before = await Inventory(ds, ct).ConfigureAwait(false);
        await WriteJson(Path.Combine(directory, "inventory-before.json"), before, ct).ConfigureAwait(false);
        string pendingPath = Path.Combine(evidenceRoot, "pending.json");
        Pending? pending = null;
        if (File.Exists(pendingPath))
        {
            if (new FileInfo(pendingPath).Length > 16384) throw new InvalidDataException("Migration pending receipt exceeds its bound.");
            pending = JsonSerializer.Deserialize<Pending>(await File.ReadAllTextAsync(pendingPath, ct).ConfigureAwait(false))
                ?? throw new InvalidDataException("Invalid source migration pending receipt.");
            if (pending.Database != before.Database || !File.Exists(pending.Archive)
                || !Path.GetFullPath(pending.Archive).StartsWith(evidenceRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || new FileInfo(pending.Archive).Length > maximumRetainedBytes
                || await Sha256(pending.Archive, ct).ConfigureAwait(false) != pending.ArchiveSha256)
                throw new InvalidDataException("Pending source migration does not match its retained original evidence.");
        }

        long selected = await ChessWitnessHydrator.CountTransitionEventsAsync(ds, ct).ConfigureAwait(false) ?? 0;
        if (pending is null && before.NullContextOutcomeRows == 0 && before.OutcomeContexts == selected
            && await HasEveryCurrentMarker(ds, reader, selected, ct).ConfigureAwait(false))
        {
            var unchanged = await Inventory(ds, ct).ConfigureAwait(false);
            if (unchanged != before) throw new InvalidDataException("Source changed during current observation verification.");
            var complete = new Receipt("legacy-absent-verified", directory, selected, 0, 0, before, unchanged);
            await WriteJson(Path.Combine(directory, "receipt.json"), complete, ct).ConfigureAwait(false);
            return complete;
        }

        string archive = Path.Combine(directory, "source-before.jsonl");
        long retained;
        using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            retained = await NpgsqlSubstrateReads.RetainSourceMigrationAsync(ds,
                ChessPositionOutcomes.SourceId.ToBytes(), ChessVocabulary.AnalysisMarkerType.ToBytes(),
                output, maximumRetainedBytes, ct).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        if (before.NullContextOutcomeRows > 0 && selected == 0)
            throw new InvalidDataException("Legacy observations have no recorded playing corpus to rebuild; evidence was retained and no eviction started.");
        long hydrated = 0;
        byte[] after = [];
        string inputs = Path.Combine(directory, "hydrated-playings.jsonl");
        using (var output = new FileStream(inputs, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            while (true)
            {
                var ids = await ChessWitnessHydrator.FetchRecordedPlayingIdPageAsync(ds, after, PageSize, true, ct).ConfigureAwait(false);
                if (ids.Count == 0) break;
                after = ids[^1].ToBytes();
                var games = await ChessWitnessHydrator.HydratePositionOutcomeInputsAsync(
                    ds, ids, maximumRetainedBytes - retained, ct).ConfigureAwait(false);
                var byId = games.ToDictionary(game => game.PlayingId);
                if (games.Count != ids.Count || ids.Any(id => !byId.ContainsKey(id)))
                {
                    await WriteJson(Path.Combine(directory, "incomplete-playings.json"),
                        new { selected = ids.Select(id => id.ToString()), hydrated = byId.Keys.Select(id => id.ToString()) }, ct).ConfigureAwait(false);
                    throw new InvalidDataException("Selected playing corpus could not be completely reconstructed; source evidence was retained and no eviction started.");
                }
                foreach (var id in ids)
                {
                    byte[] line = JsonSerializer.SerializeToUtf8Bytes(new { playing_id = id.ToString(), game = byId[id] });
                    if (line.LongLength + 1 > maximumRetainedBytes - retained)
                        throw new InvalidDataException("Hydrated playing retention envelope exhausted before eviction.");
                    await output.WriteAsync(line, ct).ConfigureAwait(false);
                    output.WriteByte((byte)'\n');
                    retained += line.Length + 1;
                    hydrated++;
                }
            }
            output.Flush(flushToDisk: true);
        }
        if (hydrated != selected || selected != await ChessWitnessHydrator.CountTransitionEventsAsync(ds, ct).ConfigureAwait(false)
            || before != await Inventory(ds, ct).ConfigureAwait(false))
            throw new InvalidDataException("Playing corpus or source evidence changed during migration preflight.");
        await WriteJson(Path.Combine(directory, "preflight.json"), new
        {
            source = ChessPositionOutcomes.SourceName, version = ChessPositionOutcomes.Version,
            selected, hydrated, retained_bytes = retained, maximum_retained_bytes = maximumRetainedBytes,
            input_scope = "recorded-source playing/line/setup/result; annotation, clock and evaluation lanes excluded",
            maximum_materialization_allowance_bytes = maximumRetainedBytes,
            materialization_allowance_kind = "conservative encoded-input and expanded-work allowance; not measured RSS",
            archive, archive_sha256 = await Sha256(archive, ct).ConfigureAwait(false),
            inputs, inputs_sha256 = await Sha256(inputs, ct).ConfigureAwait(false),
            previous_pending = pending
        }, ct).ConfigureAwait(false);

        bool evict = before.SourceRows > 0 || pending is not null;
        // Any backfill writes need a durable in-progress owner, including the first
        // repair of missing current markers when no old null-context row remains.
        if (pending is null)
        {
            pending = new(before.Database, archive, await Sha256(archive, ct).ConfigureAwait(false));
            await WriteJson(pendingPath, pending, ct).ConfigureAwait(false);
        }
        if (evict)
        {
            await reader.EvictSourceAsync(ChessPositionOutcomes.SourceId,
                relationIds: null, markerTypeIds: [ChessVocabulary.AnalysisMarkerType], ct: ct).ConfigureAwait(false);
            if (await reader.CountEvidenceBySourceAsync(ChessPositionOutcomes.SourceId, ct).ConfigureAwait(false) != 0)
                throw new InvalidDataException("Source eviction did not remove all obsolete calculated evidence.");
        }

        var inner = new NpgsqlSubstrateWriter(ds, durability: PostgresWriteDurability.Synchronous);
        await using var writer = new ConsensusAccumulatingWriter(inner, ds, persistEvidence: true);
        var runner = new IngestRunner(writer, reader, observability: new NpgsqlIngestObservability(ds));
        var result = await runner.RunAsync(new ChessPositionOutcomesDecomposer(maximumRetainedBytes), IngestRunOptions.Default with
        {
            EcosystemPath = "", SkipLayerOrderingCheck = true, SkipSourceCompletion = true, BatchSize = PageSize,
        }, ct).ConfigureAwait(false);
        if (result.UnitsFailed != 0 || result.Failures.Count != 0)
            throw new InvalidDataException("Position-outcome backfill did not complete; retained source and input receipts remain available.");
        var final = await Inventory(ds, ct).ConfigureAwait(false);
        if (final.NullContextOutcomeRows != 0 || final.OutcomeContexts != selected
            || !await HasEveryCurrentMarker(ds, reader, selected, ct).ConfigureAwait(false))
            throw new InvalidDataException("Position-outcome backfill readback does not cover the exact complete playing corpus.");
        string disposition = before.NullContextOutcomeRows > 0 ? "replaced-legacy-observations"
            : evict ? "rebuilt-incomplete-observations" : "backfilled-missing-observations";
        var receipt = new Receipt(disposition, directory,
            selected, hydrated, result.InputUnitsDone, before, final);
        await WriteJson(Path.Combine(directory, "receipt.json"), receipt, ct).ConfigureAwait(false);
        if (pending is not null)
        {
            File.Move(pendingPath, Path.Combine(directory, "completed-pending.json"));
            SyncDirectory(directory);
            SyncDirectory(evidenceRoot);
        }
        return receipt;
    }

    private static async Task<bool> HasEveryCurrentMarker(NpgsqlDataSource ds,
        NpgsqlSubstrateReader reader, long expected, CancellationToken ct)
    {
        byte[] after = [];
        long verified = 0;
        while (true)
        {
            var ids = await ChessWitnessHydrator.FetchRecordedPlayingIdPageAsync(ds, after, PageSize, true, ct).ConfigureAwait(false);
            if (ids.Count == 0) return verified == expected;
            after = ids[^1].ToBytes();
            var bitmap = await reader.EntitiesExistBitmapAsync(ids.Select(ChessPositionOutcomes.MarkerId).ToArray(), ct).ConfigureAwait(false);
            for (int i = 0; i < ids.Count; i++)
                if (!BitmapBits.IsSet(bitmap, i)) return false;
            var contexts = await NpgsqlSubstrateReads.SourceObservationContextsAsync(ds,
                ChessPositionOutcomes.SourceId.ToBytes(), ChessVocabulary.OutcomeType.ToBytes(),
                ChessVocabulary.OutcomeObject.ToBytes(), ids.Select(id => id.ToBytes()).ToArray(), ct).ConfigureAwait(false);
            var covered = contexts.Select(value => Hash128.FromBytes(value)).ToHashSet();
            if (ids.Any(id => !covered.Contains(id))) return false;
            verified += ids.Count;
        }
    }

    private static Task<NpgsqlSubstrateReads.SourceObservationInventory> Inventory(NpgsqlDataSource ds, CancellationToken ct) =>
        NpgsqlSubstrateReads.SourceObservationInventoryAsync(ds, ChessPositionOutcomes.SourceId.ToBytes(),
            ChessVocabulary.OutcomeType.ToBytes(), ChessVocabulary.OutcomeObject.ToBytes(),
            ChessVocabulary.AnalysisMarkerType.ToBytes(), ct);

    private static async Task<string> Sha256(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private static async Task WriteJson(string path, object value, CancellationToken ct)
    {
        // Publication follows an fsynced complete file; a torn metadata write cannot
        // masquerade as permission to evict without its preserved original archive.
        string temporary = path + ".next-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            await JsonSerializer.SerializeAsync(stream, value, cancellationToken: ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: false);
        SyncDirectory(Path.GetDirectoryName(path)!);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenDirectory(string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Sync(int descriptor);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int descriptor);

    private static void SyncDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        int descriptor = OpenDirectory(path, 65536); // Linux O_RDONLY | O_DIRECTORY.
        if (descriptor < 0) throw new IOException($"Cannot open migration receipt directory for synchronization: {Marshal.GetLastPInvokeError()}.");
        try
        {
            if (Sync(descriptor) != 0) throw new IOException($"Cannot synchronize migration receipt directory: {Marshal.GetLastPInvokeError()}.");
        }
        finally { _ = Close(descriptor); }
    }
}
