using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Laplace.Api.Contracts;
using Laplace.Endpoints.OpenAICompat;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Endpoints.OpenAICompat.Tests;





internal sealed class UnreachableSubstrateClient : ISubstrateClient
{
    public Task<IReadOnlyList<ConverseRow>> ConverseAsync(
        string prompt, byte[]? session, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<IReadOnlyList<ConverseRow>> ConverseTenantScopedAsync(
        string prompt, byte[]? session, byte[][] scopeSources, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<IReadOnlyList<ConverseRow>> ConverseTurnsAsync(
        IReadOnlyList<string> userTurns, byte[]? session, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public IAsyncEnumerable<GenerateToken> WalkTextStreamAsync(
        string prompt, int steps = 32, int maxOrder = 5, double temperature = 0.7, int topK = 10,
        CancellationToken ct = default) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<IReadOnlyList<CompletionRow>> CompletionsAsync(string prompt, int limit, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<SubstrateAuditReport> AuditReportAsync(
        bool includeConsensus, bool includeConvergence, int topRelationLimit, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<SubstrateVisualizationGraph> VisualizationGraphAsync(
        int limit, bool includeGeometry, bool includeEvidence, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<IReadOnlyList<ExplainTraceStep>> ExplainTraceAsync(
        string prompt, int depth, int beam, bool includeEvidence, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<EntityEvidence?> EvidenceAsync(string target, int limit, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<EmbeddingResult> EmbeddingAsync(
        string input, bool includeMeaning, int meaningLimit, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ReadinessResponse> ReadinessAsync(CancellationToken ct) =>
        Task.FromResult(new ReadinessResponse(
            Ready: false,
            SubstrateReachable: false,
            Entities: 0,
            ConsensusRelations: 0,
            PerfcacheReady: false));

    public Task<ExploreCatalogResponse> ExploreCatalogAsync(CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ExploreResolveResponse?> ExploreResolveAsync(string reference, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ExploreEntityPreviewResponse?> ExploreEntityPreviewAsync(string idHex, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ExploreEntityResponse?> ExploreEntityAsync(
        string idHex, int consensusLimit, int evidenceLimit, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<IReadOnlyList<ExploreAnchorNeighborRow>> ExploreAnchorNeighborsAsync(
        ExploreAnchor anchor, int geodesicK, int frechetK, double frechetMax, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<IReadOnlyList<WitnessedWord>> WitnessedWordsAsync(
        IReadOnlyList<string> surfaces, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ExploreTrainingExportResponse?> ExploreTrainingExportAsync(
        string idHex, int consensusLimit, int evidenceLimit, bool includeMembers, bool includePeers, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ExploreNeighborsResponse?> ExploreNeighborsAsync(string idHex, int k, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ExploreMembersResponse?> ExploreMembersAsync(string idHex, int limit, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ExplorePeersResponse?> ExplorePeersAsync(string idHex, int limit, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ExploreContainersResponse?> ExploreContainersAsync(
        string idHex, int maxHops, int limit, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ExploreGraphResponse?> ExploreConsensusGraphAsync(
        string idHex, int hops, int fanout, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<InstalledOpInvoker.OpResult> InvokeOpAsync(
        string name, IReadOnlyDictionary<string, JsonNode?>? args, int maxRows,
        int timeoutSeconds, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<IReadOnlyList<QueryShape>> QueryShapesAsync(CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<IReadOnlyList<RelationBand>> RelationBandsAsync(CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<(byte[] Id, string Label)?> ResolveTopicAsync(string reference, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<IReadOnlyList<QueryRow>> QueryAsync(
        string shape, byte[] topic, byte[]? topic2, string? relationType, string? lang,
        byte[][]? contextIds, int[]? bands, QueryDials dials, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<PulseResponse> PulseAsync(long nowUnix, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<MeshResponse?> MeshAsync(string idHex, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<TaxonomyResponse?> TaxonomyAsync(string idHex, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ModalitiesResponse> ModalitiesAsync(CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<IReadOnlyList<SourceRosterRow>> SourceRosterAsync(byte[] sourceId, int limit, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<IReadOnlyList<BandLeaders>> LeadersAsync(int[] bands, int perBand, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<EntityRecordResponse?> EntityRecordAsync(string idHex, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<MatchupResponse?> MatchupAsync(string xRef, string yRef, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<MatchupVerdictResponse?> MatchupVerdictAsync(string xRef, string yRef, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<IReadOnlyList<ChessPlayerRow>> ChessRosterAsync(int limit, int offset, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ChessPlayersResponse> ChessPlayersAsync(int limit, int offset, string? search, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ChessPlayersResponse> ChessPlayersAsync(int limit, int offset, string? search, string? initial, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ChessPlayerResponse?> ChessPlayerAsync(string idHex, int opponentLimit, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ChessGamesResponse?> ChessPlayerGamesAsync(string idHex, int limit, int offset, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ChessGameResponse?> ChessGameAsync(string idHex, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());

    public Task<ChessGamePliesResponse?> ChessGamePliesAsync(string idHex, CancellationToken ct) =>
        throw new SubstrateUnavailableException("substrate unreachable", new InvalidOperationException());
}

internal sealed class FakeSubstrateClient : ISubstrateClient
{
    private const string WhaleIdHex = "00112233445566778899aabbccddeeff";
    private const string CetaceanIdHex = "ffeeddccbbaa99887766554433221100";
    private const string IsAIdHex = "0123456789abcdef0123456789abcdef";
    private const string WordNetIdHex = "fedcba9876543210fedcba9876543210";

    public Task<IReadOnlyList<ConverseRow>> ConverseAsync(
        string prompt, byte[]? session, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ConverseRow>>(
            prompt.Contains("unknown-topic", StringComparison.OrdinalIgnoreCase)
                ? []
                :
                [
                    new ConverseRow("A whale is a marine mammal.", 0.91m, 42),
                    new ConverseRow("whale IS_A cetacean.", 0.84m, 17)
                ]);

    public Task<IReadOnlyList<ConverseRow>> ConverseAsync(
        string prompt, byte[]? session, ConverseOptions options, CancellationToken ct) =>
        options.Shape is null && options.Bands is null && !options.Elaborate
            && !string.Equals(options.LanguageSource, "request", StringComparison.Ordinal)
            ? ConverseAsync(prompt, session, ct)
            : Task.FromResult<IReadOnlyList<ConverseRow>>(
            [
                new ConverseRow(
                    options.Shape is null && options.Bands is null && !options.Elaborate
                        ? $"language={options.LanguageCode}"
                        : $"shape={options.Shape ?? "-"};bands={string.Join(',', options.Bands ?? [])};elaborate={options.Elaborate}",
                    0.91m, 42)
            ]);

    public Task<IReadOnlyList<ConverseRow>> ConverseTenantScopedAsync(
        string prompt, byte[]? session, byte[][] scopeSources, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ConverseRow>>(
        [
            new ConverseRow("tenant-scoped: my own witnessed answer.", 0.42m, 3)
        ]);

    public Task<IReadOnlyList<ConverseRow>> ConverseTurnsAsync(
        IReadOnlyList<string> userTurns, byte[]? session, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ConverseRow>>(
        [
            new ConverseRow("A whale is a marine mammal.", 0.91m, 42)
        ]);

    public async IAsyncEnumerable<GenerateToken> WalkTextStreamAsync(
        string prompt,
        int steps = 32,
        int maxOrder = 5,
        double temperature = 0.7,
        int topK = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield return new GenerateToken(1, " the", 5);

        if (prompt.Contains("trigger-stream-error", StringComparison.OrdinalIgnoreCase))
            throw new SubstrateUnavailableException("substrate went away mid-walk.", new InvalidOperationException());
        yield return new GenerateToken(2, " whale", 4);
        yield return new GenerateToken(3, " sings", 3);
    }

    public Task<IReadOnlyList<CompletionRow>> CompletionsAsync(string prompt, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CompletionRow>>(
        [
            new CompletionRow(CetaceanIdHex, IsAIdHex, 0.88m, 23, "cetacean")
        ]);

    public Task<SubstrateAuditReport> AuditReportAsync(
        bool includeConsensus, bool includeConvergence, int topRelationLimit, CancellationToken ct) =>
        Task.FromResult(new SubstrateAuditReport(
            Counts:
            [
                new SubstrateCount("entities", 1_000_000),
                new SubstrateCount("attestations", 5_000_000)
            ],
            Consensus: includeConsensus
                ? new ConsensusHealth(5_000_000, 1_500_000, 3.33m, 4.2m, 9001)
                : null,
            MultiSourceEntityCount: includeConvergence ? 250_000 : null,
            TopRelations: [TopEdge()]));

    public Task<SubstrateVisualizationGraph> VisualizationGraphAsync(
        int limit, bool includeGeometry, bool includeEvidence, CancellationToken ct) =>
        Task.FromResult(new SubstrateVisualizationGraph(
            Nodes:
            [
                new VisualizationNode(
                    WhaleIdHex, "whale",
                    X: includeGeometry ? 0.5 : null,
                    Y: includeGeometry ? -0.25 : null,
                    Z: includeGeometry ? 0.125 : null,
                    M: includeGeometry ? 0.8125 : null,
                    Radius: includeGeometry ? 1.0 : null,
                    Constituents: includeGeometry ? 5 : null,
                    EvidenceRows: includeEvidence ? 42 : null),
                new VisualizationNode(
                    CetaceanIdHex, "cetacean",
                    X: includeGeometry ? -0.5 : null,
                    Y: includeGeometry ? 0.25 : null,
                    Z: includeGeometry ? -0.125 : null,
                    M: includeGeometry ? 0.8125 : null,
                    Radius: includeGeometry ? 1.0 : null,
                    Constituents: includeGeometry ? 8 : null,
                    EvidenceRows: includeEvidence ? 17 : null)
            ],
            Edges: [TopEdge()]));

    public Task<IReadOnlyList<ExplainTraceStep>> ExplainTraceAsync(
        string prompt, int depth, int beam, bool includeEvidence, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ExplainTraceStep>>(
        [
            new ExplainTraceStep(
                Depth: 1,
                PathHex: [WhaleIdHex],
                TypePathHex: [IsAIdHex],
                EntityIdHex: WhaleIdHex,
                EntityLabel: "whale",
                EffectiveMu: 0.91m,
                PathMu: 0.91m,
                Witnesses: 42,
                Evidence: includeEvidence ? [Sample()] : Array.Empty<EvidenceSample>()),
            new ExplainTraceStep(
                Depth: 2,
                PathHex: [WhaleIdHex, CetaceanIdHex],
                TypePathHex: [IsAIdHex, IsAIdHex],
                EntityIdHex: CetaceanIdHex,
                EntityLabel: "cetacean",
                EffectiveMu: 0.84m,
                PathMu: 0.7644m,
                Witnesses: 17,
                Evidence: includeEvidence ? [Sample()] : Array.Empty<EvidenceSample>())
        ]);

    public Task<EntityEvidence?> EvidenceAsync(string target, int limit, CancellationToken ct)
    {
        if (target is "unknown-word" or "00000000000000000000000000000000")
            return Task.FromResult<EntityEvidence?>(null);

        return Task.FromResult<EntityEvidence?>(new EntityEvidence(
            WhaleIdHex,
            "whale",
            [
                new LabeledEvidenceItem(
                    TypeId: IsAIdHex,
                    TypeLabel: "is a",
                    ObjectId: CetaceanIdHex,
                    ObjectLabel: "cetacean",
                    SourceId: "",
                    SourceLabel: "WordNetDecomposer",
                    ContextId: null,
                    Outcome: 2,
                    ObservationCount: 42,
                    EffMu: 1534.7m)
            ]));
    }

    public Task<EmbeddingResult> EmbeddingAsync(string input, bool includeMeaning, int meaningLimit, CancellationToken ct)
    {
        if (input is "unknown-word")
            return Task.FromResult(new EmbeddingResult(null, null, Array.Empty<MeaningNeighbor>()));

        return Task.FromResult(new EmbeddingResult(
            WhaleIdHex,
            new EmbeddingForm(0.5, -0.25, 0.125, 0.8125, 1.0, 5),
            includeMeaning
                ?
                [
                    new MeaningNeighbor("IS_A", "cetacean", 0.91m, 42),
                    new MeaningNeighbor("HAS_DEFINITION", "a large marine mammal", 0.88m, 30)
                ]
                : Array.Empty<MeaningNeighbor>()));
    }

    public Task<ReadinessResponse> ReadinessAsync(CancellationToken ct) =>
        Task.FromResult(new ReadinessResponse(
            Ready: true,
            SubstrateReachable: true,
            Entities: 4_440_000,
            ConsensusRelations: 6_100_000,
            PerfcacheReady: true));

    public Task<ExploreCatalogResponse> ExploreCatalogAsync(CancellationToken ct) =>
        Task.FromResult(new ExploreCatalogResponse(
            Counts: [new SubstrateCount("entities", 1_000_000), new SubstrateCount("attestations", 5_000_000)],
            Consensus: new ConsensusHealth(5_000_000, 1_500_000, 3.33m, 4.2m, 9001),
            MultiSourceEntityCount: 250_000,
            TopRelations: [TopEdge()],
            Sources: [new ExploreSourceRow("WordNet", 1_000_000, 500_000, "knowledge", "L2", "synsets")],
            Stages: [new ExploreStageRow("knowledge", 2, "WordNet hub", [new ExploreStageSourceRow("wordnet", "L2", "synsets", null)])],
            FeaturedRefs: ["dog", "whale"]));

    public Task<ExploreResolveResponse?> ExploreResolveAsync(string reference, CancellationToken ct)
    {
        if (reference is "unknown-word") return Task.FromResult<ExploreResolveResponse?>(null);
        return Task.FromResult<ExploreResolveResponse?>(new ExploreResolveResponse(
            WhaleIdHex, "whale", "word", true,
            [new SalientFactRow("IS_A", "cetacean", 0.91m, 42)]));
    }

    public Task<ExploreEntityPreviewResponse?> ExploreEntityPreviewAsync(string idHex, CancellationToken ct) =>
        Task.FromResult<ExploreEntityPreviewResponse?>(new ExploreEntityPreviewResponse(
            idHex, "whale", 2, "Word", true, 42,
            [new SalientFactRow("IS_A", "cetacean", 0.91m, 42)]));

    public Task<ExploreEntityResponse?> ExploreEntityAsync(
        string idHex, int consensusLimit, int evidenceLimit, CancellationToken ct) =>
        Task.FromResult<ExploreEntityResponse?>(SampleEntity(idHex));

    public Task<IReadOnlyList<ExploreAnchorNeighborRow>> ExploreAnchorNeighborsAsync(
        ExploreAnchor anchor, int geodesicK, int frechetK, double frechetMax, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ExploreAnchorNeighborRow>>(
        [
            new ExploreAnchorNeighborRow("geodesic", CetaceanIdHex, "cetacean", 2, 0.12, null),
            new ExploreAnchorNeighborRow("shape", WhaleIdHex, "whale", 2, null, 0.03),
        ]);

    public Task<IReadOnlyList<WitnessedWord>> WitnessedWordsAsync(
        IReadOnlyList<string> surfaces, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WitnessedWord>>(
            surfaces.Contains("whale")
                ? [new WitnessedWord("whale", WhaleIdHex, 42)]
                : []);

    public Task<ExploreTrainingExportResponse?> ExploreTrainingExportAsync(
        string idHex, int consensusLimit, int evidenceLimit, bool includeMembers, bool includePeers, CancellationToken ct) =>
        Task.FromResult<ExploreTrainingExportResponse?>(new ExploreTrainingExportResponse(
            idHex, "whale", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 42, 2,
            SampleEntity(idHex)!,
            includeMembers ? [new ExploreMemberRow(CetaceanIdHex, "cetacean", "synonym", 0.88m, 17)] : [],
            includePeers ? [new ExplorePeerRow("dolphin", "frame", 0.75)] : []));

    public Task<ExploreNeighborsResponse?> ExploreNeighborsAsync(string idHex, int k, CancellationToken ct) =>
        Task.FromResult<ExploreNeighborsResponse?>(new ExploreNeighborsResponse(
            idHex,
            [new ExploreNeighborRow("cetacean", 0.12, 0.34, "structural",
                NeighborIdHex: CetaceanIdHex, X: 0.1, Y: 0.2, Z: 0.3, M: 0.4, Radius: 0.5)],
            [new SalientFactRow("IS_A", "cetacean", 0.91m, 42)]));

    public Task<ExploreMembersResponse?> ExploreMembersAsync(string idHex, int limit, CancellationToken ct) =>
        Task.FromResult<ExploreMembersResponse?>(new ExploreMembersResponse(
            idHex, [new ExploreMemberRow(CetaceanIdHex, "cetacean", "synonym", 0.88m, 17)]));

    public Task<ExplorePeersResponse?> ExplorePeersAsync(string idHex, int limit, CancellationToken ct) =>
        Task.FromResult<ExplorePeersResponse?>(new ExplorePeersResponse(
            idHex, [new ExplorePeerRow("dolphin", "frame", 0.75)]));

    public Task<ExploreContainersResponse?> ExploreContainersAsync(
        string idHex, int maxHops, int limit, CancellationToken ct) =>
        Task.FromResult<ExploreContainersResponse?>(new ExploreContainersResponse(
            idHex, [new ExploreContainerRow(WhaleIdHex, "whale document", 4, "Document", 1)]));

    public Task<ExploreGraphResponse?> ExploreConsensusGraphAsync(
        string idHex, int hops, int fanout, CancellationToken ct) =>
        Task.FromResult<ExploreGraphResponse?>(new ExploreGraphResponse(
            IdHex: idHex,
            Label: "whale",
            Hops: hops,
            Fanout: fanout,
            Nodes:
            [
                new ExploreGraphNode(idHex, "whale", 0, 2),
                new ExploreGraphNode(CetaceanIdHex, "cetacean", 1, 2),
            ],
            Edges:
            [
                new ExploreGraphEdge(idHex, CetaceanIdHex, "IS_A", 0.91m, 42, 1),
            ],
            Truncated: false,
            MaxNodes: 160));

    public Task<InstalledOpInvoker.OpResult> InvokeOpAsync(
        string name, IReadOnlyDictionary<string, JsonNode?>? args, int maxRows,
        int timeoutSeconds, CancellationToken ct)
    {
        if (name == "source_status")
        {
            var row = new Dictionary<string, object?>
            {
                ["source"] = "WordNetDecomposer",
                ["known"] = true,
                ["ingested"] = true,
                ["timeout_seconds"] = timeoutSeconds,
            };
            return Task.FromResult(new InstalledOpInvoker.OpResult([row], null, null));
        }
        return Task.FromResult(new InstalledOpInvoker.OpResult(
            [], null, $"no installed operation named '{name}'"));
    }

    public Task<IReadOnlyList<QueryShape>> QueryShapesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<QueryShape>>(
        [
            new QueryShape("define", "witnessed glosses", false, false, false),
            new QueryShape("related", "outgoing edges of one relation type", false, true, false),
            new QueryShape("is_a", "witnessed IS_A chain between two topics", true, false, false),
        ]);

    public Task<IReadOnlyList<RelationBand>> RelationBandsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RelationBand>>(
        [
            new RelationBand(2, "taxonomic", 0.90, 11, 89_414),
            new RelationBand(4, "partitive", 0.73, 26, 9_097),
        ]);

    public Task<(byte[] Id, string Label)?> ResolveTopicAsync(string reference, CancellationToken ct)
    {
        if (reference is "unknown-word")
            return Task.FromResult<(byte[], string)?>(null);
        return Task.FromResult<(byte[], string)?>((Convert.FromHexString(WhaleIdHex), "whale"));
    }

    public Task<IReadOnlyList<QueryRow>> QueryAsync(
        string shape, byte[] topic, byte[]? topic2, string? relationType, string? lang,
        byte[][]? contextIds, int[]? bands, QueryDials dials, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<QueryRow>>(
        [
            new QueryRow("whale IS_A cetacean.", 0.91m, 42),
            new QueryRow("A whale is a marine mammal.", 0.84m, 17),
        ]);

    public Task<PulseResponse> PulseAsync(long nowUnix, CancellationToken ct) =>
        Task.FromResult(new PulseResponse("pulse", nowUnix, 4_440_000, 6_300_000, 5_700_000,
            4_337_000, nowUnix - 3, 120, true));

    public Task<ModalitiesResponse> ModalitiesAsync(CancellationToken ct) =>
        Task.FromResult(new ModalitiesResponse("modalities", 6_280_000, 781, 0, 0, 22_104));

    public Task<TaxonomyResponse?> TaxonomyAsync(string idHex, CancellationToken ct)
    {
        if (idHex.Length != 32 || !idHex.All(Uri.IsHexDigit))
            return Task.FromResult<TaxonomyResponse?>(null);
        return Task.FromResult<TaxonomyResponse?>(new TaxonomyResponse("taxonomy",
            WhaleIdHex, "whale",
            [ new TaxonomyNode(CetaceanIdHex, "cetacean", 1325.09m), new TaxonomyNode(IsAIdHex, "mammal", 1319.14m) ],
            [ new TaxonomyNode(WordNetIdHex, "sperm whale", 1325.09m) ]));
    }

    public Task<IReadOnlyList<SourceRosterRow>> SourceRosterAsync(byte[] sourceId, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SourceRosterRow>>(
        [
            new SourceRosterRow(WhaleIdHex, "whale", "IS_A", CetaceanIdHex, "cetacean", 42),
        ]);

    public Task<MeshResponse?> MeshAsync(string idHex, CancellationToken ct)
    {
        if (idHex.Length != 32 || !idHex.All(Uri.IsHexDigit))
            return Task.FromResult<MeshResponse?>(null);
        return Task.FromResult<MeshResponse?>(new MeshResponse("mesh", idHex.ToLowerInvariant(), "whale",
            "WordNet_Synset",
            [ new MeshLink(CetaceanIdHex, "cetacean", "is a", "WordNet_Synset", 0.91m, 42) ],
            [ new MeshLink(WhaleIdHex, "whale", "sense", null, 1.0m, 12),
              new MeshLink(CetaceanIdHex, "orca", "sense", null, 0.8m, 5) ]));
    }

    public Task<IReadOnlyList<BandLeaders>> LeadersAsync(int[] bands, int perBand, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BandLeaders>>(
            bands.Select(b => new BandLeaders(b, b == 2 ? "taxonomic" : $"band {b}",
            [
                new LeaderRow(WhaleIdHex, "whale", "IS_A", CetaceanIdHex, "cetacean", 1325.09m, 42),
            ])).ToList());

    public Task<EntityRecordResponse?> EntityRecordAsync(string idHex, CancellationToken ct)
    {
        // Honor the real contract: a non-32-hex id resolves to null (→ 404).
        if (idHex.Length != 32 || !idHex.All(Uri.IsHexDigit))
            return Task.FromResult<EntityRecordResponse?>(null);
        return Task.FromResult<EntityRecordResponse?>(
            new EntityRecordResponse("entity.record", idHex.ToLowerInvariant(), 34, 2, 1, 12));
    }

    public Task<MatchupResponse?> MatchupAsync(string xRef, string yRef, CancellationToken ct)
    {
        if (xRef is "unknown-word" || yRef is "unknown-word")
            return Task.FromResult<MatchupResponse?>(null);
        var record = new EntityRecordResponse("entity.record", WhaleIdHex, 34, 2, 1, 12);
        var facts = new[] { new SalientFactRow("is a", "cetacean", 1325.09m, 42) };
        return Task.FromResult<MatchupResponse?>(new MatchupResponse("matchup",
            new MatchupSide(WhaleIdHex, "whale", record, facts),
            new MatchupSide(CetaceanIdHex, "cetacean", record, facts),
            [
                new TapeRow("both", "is a", "aquatic mammal", 1325.09m),
                new TapeRow("x-only", "is a", "baleen whale", 1201.44m),
            ]));
    }

    public Task<MatchupVerdictResponse?> MatchupVerdictAsync(string xRef, string yRef, CancellationToken ct) =>
        Task.FromResult<MatchupVerdictResponse?>(new MatchupVerdictResponse(
            "matchup.verdict", "whale —is a→ cetacean", "taxonomy", 1325.09m, 42, 0.12,
            "related via taxonomy; strong shared usage (42)"));

    private static ExploreEntityResponse SampleEntity(string idHex) => new(
        idHex, "whale", 2, "Word", true, 42,
        [new ExplorePhysicalityRow(1, 0.5, -0.25, 0.125, 0.8125, 1.0, 5)],
        [new SalientFactRow("IS_A", "cetacean", 0.91m, 42)],
        [new ExploreConsensusRow("out", "IS_A", CetaceanIdHex, "cetacean", 0.91m, 42)],
        [],
        [],
        [],
        [],
        [],
        [
            new LabeledEvidenceItem(
                IsAIdHex, "is a", CetaceanIdHex, "cetacean", "", "WordNetDecomposer", null, 2, 42, 1534.7m)
        ]);

    private static VisualizationEdge TopEdge() => new(
        SubjectIdHex: WhaleIdHex,
        Subject: "whale",
        TypeIdHex: IsAIdHex,
        Type: "IS_A",
        ObjectIdHex: CetaceanIdHex,
        Object: "cetacean",
        EffectiveMu: 0.91m,
        Witnesses: 42);

    private static EvidenceSample Sample() => new(
        TypeIdHex: IsAIdHex,
        ObjectIdHex: CetaceanIdHex,
        SourceIdHex: WordNetIdHex,
        ContextIdHex: null,
        Outcome: 2,
        ObservationCount: 12);

    // --- chess read surface -------------------------------------------------
    // Two players and the one game between them, wired so the drill the UI walks
    // is walkable end to end: roster -> player -> his games -> that game -> the
    // opponent's page. TalIdHex is the live content address of "Tal, Mikhail"
    // (realize.canonical_id('chess/player/mikhail tal')), so a fixture id and a real id
    // are the same kind of thing here, as they are in the substrate.
    private const string TalIdHex = "b422a7d40dec7948426e7c8ae40810d5";
    private const string BotvinnikIdHex = "aa11bb22cc33dd44ee55ff6677889900";
    private const string GameIdHex = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";

    // eff_mu = rating - 2*rd, the conservative estimate everything ranks by.
    private static readonly ChessPlayerRow TalRow = new(
        1, TalIdHex, "Tal, Mikhail", 1341, 1900.0, 50.0, 1800.0);

    private static readonly ChessPlayerRow BotvinnikRow = new(
        2, BotvinnikIdHex, "Botvinnik, Mikhail", 28, 1600.0, 50.0, 1500.0);

    public Task<IReadOnlyList<ChessPlayerRow>> ChessRosterAsync(int limit, int offset, CancellationToken ct)
    {
        IReadOnlyList<ChessPlayerRow> all = [TalRow, BotvinnikRow];
        return Task.FromResult<IReadOnlyList<ChessPlayerRow>>(
            [.. all.Skip(Math.Max(0, offset)).Take(Math.Max(0, limit))]);
    }

    public Task<ChessPlayersResponse> ChessPlayersAsync(
        int limit, int offset, string? search, CancellationToken ct)
        => ChessPlayersAsync(limit, offset, search, null, ct);

    public async Task<ChessPlayersResponse> ChessPlayersAsync(
        int limit, int offset, string? search, string? initial, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(initial))
        {
            // First-codepoint bucketing: reaches every player, not a warm window.
            var all = await ChessRosterAsync(int.MaxValue, 0, ct);
            var hits = all
                .Where(p => p.Name.StartsWith(initial.Trim(), StringComparison.OrdinalIgnoreCase))
                .Skip(Math.Max(0, offset)).Take(Math.Max(0, limit))
                .Select((p, i) => p with { Rank = offset + i + 1 })
                .ToList();
            return new ChessPlayersResponse("chess.players", hits.Count, Math.Max(0, offset), hits);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Content-address lookup: an exactly-spelled name or nothing.
            var hit = search.Trim().Equals("Tal, Mikhail", StringComparison.OrdinalIgnoreCase)
                   || search.Trim().Equals("mikhail tal", StringComparison.OrdinalIgnoreCase);
            return new ChessPlayersResponse("chess.players", hit ? 1 : 0, 0, hit ? [TalRow] : []);
        }
        var page = await ChessRosterAsync(limit, offset, ct);
        return new ChessPlayersResponse("chess.players", page.Count, offset, page);
    }

    public Task<ChessPlayerResponse?> ChessPlayerAsync(string idHex, int opponentLimit, CancellationToken ct) =>
        Task.FromResult<ChessPlayerResponse?>(
            !string.Equals(idHex, TalIdHex, StringComparison.OrdinalIgnoreCase)
                ? null
                : new ChessPlayerResponse("chess.player", TalIdHex, "Tal, Mikhail",
                    Overall: new ChessRecord(1341, 669, 489, 183, 0, 0.6811),
                    AsWhite: new ChessRecord(726, 420, 223, 83, 0, 0.7327),
                    AsBlack: new ChessRecord(615, 249, 266, 100, 0, 0.6203),
                    PeakRating: 2705,
                    Ratings: [new ChessRatingRow(2705, 12), new ChessRatingRow(2645, 30)],
                    Opponents:
                    [
                        new ChessOpponentRow(BotvinnikIdHex, "Botvinnik, Mikhail",
                            28, 1700.0, 60.0, 1580.0),
                    ]));

    public Task<ChessGamesResponse?> ChessPlayerGamesAsync(
        string idHex, int limit, int offset, CancellationToken ct) =>
        Task.FromResult<ChessGamesResponse?>(
            idHex.Length != 32
                ? null
                : new ChessGamesResponse("chess.games", idHex, offset,
                    offset > 0 || limit <= 0
                        ? []
                        :
                        [
                            new ChessGameRow(GameIdHex, "1960.03.15", "World Championship", "B44",
                                AsWhite: true, BotvinnikIdHex, "Botvinnik, Mikhail", "1-0", 2),
                        ]));

    public Task<ChessGameResponse?> ChessGameAsync(string idHex, CancellationToken ct) =>
        Task.FromResult<ChessGameResponse?>(
            !string.Equals(idHex, GameIdHex, StringComparison.OrdinalIgnoreCase)
                ? null
                : new ChessGameResponse("chess.game", GameIdHex,
                    TalIdHex, "Tal, Mikhail", BotvinnikIdHex, "Botvinnik, Mikhail",
                    Result: "1-0", PlayedOn: "1960.03.15", Event: "World Championship",
                    Eco: "B44", Termination: "Normal", TimeControl: "40/9000",
                    TcClass: "classical",
                    Movetext: "1. e4 c5 2. Nf3 Nc6 3. d4 cxd4 4. Nxd4 e6 1-0"));

    public Task<ChessGamePliesResponse?> ChessGamePliesAsync(string idHex, CancellationToken ct) =>
        Task.FromResult<ChessGamePliesResponse?>(
            !string.Equals(idHex, GameIdHex, StringComparison.OrdinalIgnoreCase)
                ? null
                : new ChessGamePliesResponse("chess.game.plies", GameIdHex,
                    StartFen: "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
                    HasClocks: true, Truncated: null,
                    Plies:
                    [
                        new ChessPlyRow(1, "e4", "e2e4",
                            "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq - 0 1",
                            true, 179.0, "b8152a19a309000000000000000000aa"),
                        new ChessPlyRow(2, "c5", "c7c5",
                            "rnbqkbnr/pp1ppppp/8/2p5/4P3/8/PPPP1PPP/RNBQKBNR w KQkq - 0 2",
                            false, 178.5, "b8152a19a309000000000000000000bb"),
                    ]));
}
