using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Chess.Service;

/// <summary>Cold read-only evidence export through the existing bounded witness read owner.
/// Independent page reads describe an observation interval, never a historical snapshot.</summary>
internal static class ChessStartingSideInventory
{
    internal const string Mode = "inventory-starting-sides";
    internal const string Usage = "ChessCatalogSurfaces inventory-starting-sides --output-dir <new-absolute-directory> "
        + "[--page-size 64] [--maximum-materialized-mib 128] [--maximum-retained-mib 512] [--deadline-seconds 300]";
    internal sealed record Options(string OutputDirectory, int PageSize, long MaterializedBytes,
        long RetainedBytes, int DeadlineSeconds);
    internal sealed record DatabaseIdentity(string Database, string DatabaseOid, string SystemIdentifier);
    internal sealed record SourceBinding(string Name, string SourceId, string LineId);
    internal sealed record PageInputs(IReadOnlyList<ChessWitnessedGame> Games,
        IReadOnlyDictionary<Hash128, IReadOnlyList<SourceBinding>> Sources);

    internal interface IReadSource
    {
        Task<DatabaseIdentity> IdentityAsync(CancellationToken ct);
        Task<long?> CountAsync(CancellationToken ct);
        Task<IReadOnlyList<Hash128>> PageAsync(byte[] afterId, int limit, CancellationToken ct);
        Task<PageInputs> HydrateAsync(IReadOnlyList<Hash128> ids, long maximumBytes, CancellationToken ct);
    }

    internal sealed class Summary
    {
        public string Status { get; set; } = "partial";
        public string Scope => "observed-read-interval";
        public bool Snapshot => false;
        public bool ProvesHistoricalCoverage => false;
        public string[] SelectedSources => ["ChessPgn", "ChessBook", "ChessSelfPlay"];
        public int HydrationReplayMaximumPlies => 1024;
        public string Limitation => "Strict hydration refuses incomplete replay, including lines beyond its 1024-ply window. "
            + "Unverified playings remain unclassified. Independent reads do not establish an MVCC snapshot or prior database contents.";
        public required Options Bounds { get; init; }
        public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedUtc { get; set; }
        public DatabaseIdentity? DatabaseBefore { get; set; }
        public DatabaseIdentity? DatabaseAfter { get; set; }
        public long? SelectedBefore { get; set; }
        public long? SelectedAfter { get; set; }
        public long Enumerated { get; set; }
        public long Retained { get; set; }
        public long White { get; set; }
        public long Black { get; set; }
        public long Unclassified => PendingPlayingIds.Length;
        public string[] PendingPlayingIds { get; set; } = [];
        public string? LastFetchedCursor { get; set; }
        public string? LastCompletedPageCursor { get; set; }
        public string? LastRetainedCursor { get; set; }
        public bool ReachedEnd { get; set; }
        public long RetainedBytes { get; set; }
        public string? InputsSha256 { get; set; }
        public string Stage { get; set; } = "initializing";
        public string? FailureType { get; set; }
        public string? FailureDetail { get; set; }
    }

    internal static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            string key = args[i];
            if (key is not ("--output-dir" or "--page-size" or "--maximum-materialized-mib"
                or "--maximum-retained-mib" or "--deadline-seconds")
                || i + 1 == args.Length || !values.TryAdd(key, args[i + 1]))
                throw new ArgumentException("Unknown, duplicate, or incomplete inventory option.");
        }
        if (!values.TryGetValue("--output-dir", out string? output) || !Path.IsPathFullyQualified(output))
            throw new ArgumentException("--output-dir must name a new absolute directory.");
        long Number(string key, long fallback)
        {
            if (!values.TryGetValue(key, out string? value)) return fallback;
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long number) || number <= 0)
                throw new ArgumentException(key + " must be a positive integer.");
            return number;
        }
        long page = Number("--page-size", 64), seconds = Number("--deadline-seconds", 300);
        if (page > 256 || seconds > int.MaxValue / 1000)
            throw new ArgumentException("Page size must be at most 256 and the deadline must fit milliseconds.");
        return new Options(Path.GetFullPath(output), (int)page,
            checked(Number("--maximum-materialized-mib", 128) * 1024 * 1024),
            checked(Number("--maximum-retained-mib", 512) * 1024 * 1024), (int)seconds);
    }

    internal static string ReadOnlyConnectionString(string basis)
    {
        var builder = new NpgsqlConnectionStringBuilder(basis);
        builder.Options = (builder.Options + " -c default_transaction_read_only=on").Trim();
        builder.ApplicationName = "laplace-chess-starting-side-inventory";
        return builder.ConnectionString;
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is ["--help"]) { Console.WriteLine(Usage); return 0; }
        Options options;
        try { options = Parse(args); }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        { Console.Error.WriteLine(ex.Message + "\n" + Usage); return 2; }
        try
        {
            // Source construction does not connect, bootstrap, repair, or evict.
            await using var ds = LaplaceDataSource.Create(SubstrateAccess.Serving,
                ReadOnlyConnectionString(LaplaceDataSource.ConnectionStringFor(SubstrateAccess.Serving)));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.DeadlineSeconds));
            var summary = await CollectAsync(options, new DatabaseSource(ds), deadline.Token).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(summary));
            return summary.Status == "completed" ? 0 : 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Connection/error text can contain credentials. Retained partial receipts carry
            // the controlled read stage and exception type without raw driver details.
            Console.Error.WriteLine("Inventory could not retain its report: " + ex.GetType().Name);
            return 1;
        }
    }

    internal static string StartingSide(ChessWitnessedGame game)
    {
        var state = ChessAnalyze.InitialState(game.StartFen, new ChessModality())
            ?? throw new InvalidDataException("Hydrated start position is unreadable.");
        if (game.StartPositionId is not { } expected || ChessCompose.PositionId(state.Initial.Board) != expected)
            throw new InvalidDataException("Hydrated start does not match the native line's first constituent.");
        return state.Initial.Board.WhiteToMove ? "white" : "black";
    }

    internal static async Task<Summary> CollectAsync(Options options, IReadSource source, CancellationToken ct)
    {
        if (Directory.Exists(options.OutputDirectory) || File.Exists(options.OutputDirectory))
            throw new IOException("Inventory output directory already exists.");
        Directory.CreateDirectory(options.OutputDirectory);
        string summaryPath = Path.Combine(options.OutputDirectory, "summary.json");
        string inputsPath = Path.Combine(options.OutputDirectory, "hydrated-playings.jsonl");
        var summary = new Summary { Bounds = options };
        // CreateNew also refuses a concurrent owner of the same evidence directory.
        using (var initial = new FileStream(summaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            await JsonSerializer.SerializeAsync(initial, summary, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var output = new FileStream(inputsPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            byte[] after = [];
            try
            {
                summary.Stage = "database-identity-before";
                summary.DatabaseBefore = await source.IdentityAsync(ct).ConfigureAwait(false);
                summary.Stage = "selected-count-before";
                summary.SelectedBefore = await source.CountAsync(ct).ConfigureAwait(false);
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    summary.Stage = "page";
                    var ids = await source.PageAsync(after, options.PageSize, ct).ConfigureAwait(false);
                    if (ids.Count == 0) { summary.ReachedEnd = true; break; }
                    if (ids.Count > options.PageSize)
                        throw new InvalidDataException("Read owner exceeded the admitted page size.");
                    summary.PendingPlayingIds = ids.Select(id => id.ToString()).ToArray();
                    summary.Enumerated += ids.Count;
                    summary.LastFetchedCursor = ids[^1].ToString();
                    Hash128? previous = after.Length == 0 ? null : Hash128.FromBytes(after);
                    foreach (var id in ids)
                    {
                        if (previous is { } before && before.CompareToBytewise(id) >= 0)
                            throw new InvalidDataException("Read owner returned a repeated or unordered playing cursor.");
                        previous = id;
                    }
                    summary.Stage = "source-ownership-and-strict-hydration";
                    var inputs = await source.HydrateAsync(ids, options.MaterializedBytes, ct).ConfigureAwait(false);
                    var byId = inputs.Games.ToDictionary(game => game.PlayingId);
                    if (byId.Count != ids.Count || ids.Any(id => !byId.ContainsKey(id)))
                        throw new InvalidDataException("Selected page was not completely hydrated.");
                    for (int index = 0; index < ids.Count; index++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var game = byId[ids[index]];
                        summary.Stage = "starting-side-and-source-verification";
                        string side = StartingSide(game);
                        if (!inputs.Sources.TryGetValue(game.PlayingId, out var owners) || owners.Count == 0
                            || owners.Any(owner => owner.LineId != game.LineId.ToString()))
                            throw new InvalidDataException("Hydrated line does not match its witnessed source ownership.");
                        // Explicit string identities preserve the exact native input ids in transport JSON.
                        byte[] record = JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            PlayingId = game.PlayingId.ToString(), LineId = game.LineId.ToString(),
                            StartPositionId = game.StartPositionId!.Value.ToString(), StartingSide = side,
                            Sources = owners, game.StartFen, game.Moves,
                            MoveIds = game.MoveIds.Select(id => id.ToString()).ToArray(),
                            Result = game.Result.ResultToken, WhitePlayer = game.WhitePlayer?.ToString(),
                            BlackPlayer = game.BlackPlayer?.ToString(), game.ClockTokens, game.EvalTokens,
                            game.QualityTokens, game.SpentSeconds
                        });
                        summary.Stage = "retain-input";
                        if (record.LongLength + 1 > options.RetainedBytes - summary.RetainedBytes)
                            throw new InvalidDataException("Inventory exceeds its admitted retained-input byte envelope.");
                        await output.WriteAsync(record, ct).ConfigureAwait(false);
                        output.WriteByte((byte)'\n');
                        hash.AppendData(record);
                        hash.AppendData([(byte)'\n']);
                        summary.RetainedBytes += record.LongLength + 1;
                        summary.Retained++;
                        if (side == "white") summary.White++; else summary.Black++;
                        summary.LastRetainedCursor = ids[index].ToString();
                        summary.PendingPlayingIds = ids.Skip(index + 1).Select(id => id.ToString()).ToArray();
                    }
                    after = ids[^1].ToBytes();
                    summary.LastCompletedPageCursor = ids[^1].ToString();
                }
                summary.Stage = "selected-count-after";
                summary.SelectedAfter = await source.CountAsync(ct).ConfigureAwait(false);
                summary.Stage = "database-identity-after";
                summary.DatabaseAfter = await source.IdentityAsync(ct).ConfigureAwait(false);
                if (summary.DatabaseBefore != summary.DatabaseAfter
                    || summary.SelectedBefore != summary.Retained || summary.SelectedAfter != summary.Retained)
                    throw new InvalidDataException("Observed identity or selected counts do not reconcile with retained playings.");
                summary.Status = "completed";
                summary.Stage = "completed";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                summary.Status = "partial";
                summary.FailureType = ex.GetType().Name;
                summary.FailureDetail = ex is OperationCanceledException ? "Inventory deadline or cancellation was reached."
                    : ex is InvalidDataException ? ex.Message[..Math.Min(ex.Message.Length, 512)]
                    : "Read or retention failed; exception text omitted to avoid retaining connection secrets.";
            }
            finally
            {
                // A cancelled write must not leave a truncated JSON record presented as evidence.
                try
                {
                    output.SetLength(summary.RetainedBytes);
                    await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    summary.InputsSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    summary.Status = "partial";
                    summary.Stage = "finalize-retained-input";
                    summary.FailureType = ex.GetType().Name;
                    summary.FailureDetail = "Could not finalize the retained input file; its byte count and digest are unverified.";
                    summary.InputsSha256 = null;
                }
                summary.FinishedUtc = DateTimeOffset.UtcNow;
                await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        return summary;
    }

    private sealed class DatabaseSource(NpgsqlDataSource ds) : IReadSource
    {
        public async Task<DatabaseIdentity> IdentityAsync(CancellationToken ct)
        {
            var rows = await NpgsqlRead.ReadRowsAsync(ds, SqlCatalog.Get("maintenance.database_identity"),
                static row => new DatabaseIdentity(row.GetString(0), row.GetString(1), row.GetString(2)),
                timeoutSeconds: 10, ct: ct).ConfigureAwait(false);
            return rows.Count == 1 ? rows[0] : throw new InvalidDataException("Expected one database identity.");
        }

        public Task<long?> CountAsync(CancellationToken ct)
            => ChessWitnessHydrator.CountTransitionEventsAsync(ds, ct);

        public async Task<IReadOnlyList<Hash128>> PageAsync(byte[] afterId, int limit, CancellationToken ct)
            => await ChessWitnessHydrator.FetchRecordedPlayingIdPageAsync(ds, afterId, limit,
                includeLive: true, ct).ConfigureAwait(false);

        public async Task<PageInputs> HydrateAsync(IReadOnlyList<Hash128> ids, long maximumBytes, CancellationToken ct)
        {
            var budget = new NpgsqlSubstrateReads.ChessWitnessReadBudget(maximumBytes);
            budget.Reserve(checked(65536L + ids.Count * 1024L));
            var selected = ids.ToHashSet();
            byte[][] subjects = ids.Select(id => id.ToBytes()).ToArray();
            var relation = RelationTypeRegistry.RelationTypeId("PLAYS_LINE");
            var bindings = ids.ToDictionary(id => id, _ => new List<SourceBinding>());
            foreach (var (name, source) in new[]
            {
                ("ChessPgn", ChessVocabulary.PgnSourceId),
                ("ChessBook", ChessVocabulary.BookSourceId),
                ("ChessSelfPlay", ChessVocabulary.SourceId)
            })
            {
                // The existing read row has no Source column. Singleton source admission
                // binds every returned row to its exact provenance without a new SQL owner.
                var rows = await NpgsqlSubstrateReads.ChessWitnessInputsAsync(ds, subjects,
                    [relation.ToBytes()], [source.ToBytes()], null, budget, ct).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    var playing = Hash128.FromBytes(row.Subject);
                    if (!selected.Contains(playing) || Hash128.FromBytes(row.Type) != relation
                        || row.Object is null || row.Context is not null)
                        throw new InvalidDataException("Invalid source-bound recorded playing line.");
                    string line = Hash128.FromBytes(row.Object).ToString();
                    if (bindings[playing].Any(owner => owner.LineId != line))
                        throw new InvalidDataException("Recorded playing has competing source-bound lines.");
                    bindings[playing].Add(new SourceBinding(name, source.ToString(), line));
                }
            }
            if (bindings.Values.Any(owners => owners.Count == 0))
                throw new InvalidDataException("Selected playing has no permitted source-bound line.");
            if (budget.Remaining <= 0)
                throw new InvalidDataException("Source ownership exhausted the admitted materialization envelope.");
            var games = await ChessWitnessHydrator.HydratePositionOutcomeInputsAsync(ds, ids,
                budget.Remaining, ct).ConfigureAwait(false);
            return new PageInputs(games,
                bindings.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<SourceBinding>)pair.Value));
        }
    }
}
