using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Model;
using Laplace.Decomposers.Tests;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.Decomposers.Model.Tests;

public sealed class ModelTokenEdgeETLTests
{
    private static readonly string MiniLm =
        "/vault/models/models--sentence-transformers--all-MiniLM-L6-v2/snapshots/" +
        "c9745ed1d9f207416be6d2e6f8de32d1f16199bf";

    private readonly ITestOutputHelper _output;
    public ModelTokenEdgeETLTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void DefaultMode_IsSingleSourcePass_WithNoFixedTestimonyWidth()
    {
        using var environment = new PlanesEnvironment(null);
        Assert.Equal("structure", ModelTokenEdgeETL.ResolvePlanesMode());
        Assert.Equal(0, ModelTokenEdgeETL.TestimonyWidthPerCircuit);
    }

    [Fact]
    public void RawFactorAndRankedModes_AreRejected()
    {
        using var environment = new PlanesEnvironment("factors");
        Assert.Throws<InvalidOperationException>(ModelTokenEdgeETL.ResolvePlanesMode);
    }

    [Theory]
    [InlineData("ATTENDS", "402001019b7e964d3cf0ef7532de16bb")]
    [InlineData("COMPLETES_TO", "c9490ac67209d8d3efd7993a24d88102")]
    [InlineData("OV_RELATES", "5ea99a7eacf84eef2088c932cea7cea9")]
    [InlineData("SIMILAR_TO", "dc766e130b55698b2fdcc15a52e2a718")]
    public void ModelRelationIds_MatchRetainedDatabaseResolution(string relation, string expectedHex)
    {
        Hash128 id = RelationTypeRegistry.RelationTypeId(relation);
        Assert.Equal(expectedHex, Convert.ToHexString(id.ToBytes()).ToLowerInvariant());
    }

    [Fact]
    public void AnalyzerVersion_BindsContextAndOpaqueCalculationReceipt()
    {
        Hash128 source = Hash128.OfCanonical("test/model/source/versioned");
        Hash128 type = ModelDecomposer.SimilarToTypeId;
        Hash128 subject = Hash128.OfCanonical("test/model/subject");
        Hash128 obj = Hash128.OfCanonical("test/model/object");
        int current = ModelTokenEdgeETL.AnalyzerVersion;

        Hash128 context = ModelTokenEdgeETL.CircuitContextForVersion(
            current, source, "embedding", -1, -1, ["embedding.weight"]);
        Hash128 nextContext = ModelTokenEdgeETL.CircuitContextForVersion(
            checked(current + 1), source, "embedding", -1, -1, ["embedding.weight"]);
        Hash128 receipt = ModelTokenEdgeETL.CalculationReceiptForVersion(
            current, source, context, type, subject, obj);
        Hash128 nextReceipt = ModelTokenEdgeETL.CalculationReceiptForVersion(
            checked(current + 1), source, nextContext, type, subject, obj);

        Assert.NotEqual(context, nextContext);
        Assert.NotEqual(receipt, nextReceipt);
    }

    [Fact]
    public void CircuitVotes_AggregateThroughNativeGlickoWithContinuousResult()
    {
        long[][] circuitScores =
        [
            [1_000_000_000, 0],
            [800_000_000, 200_000_000],
            [600_000_000, 400_000_000],
        ];
        long[] ratings = [1_700_000_000_000, 1_550_000_000_000, 1_400_000_000_000];
        long[] rds = [50_000_000_000, 120_000_000_000, 250_000_000_000];

        (long[] scores, short[] outcomes) = NativeBilinearContraction.AggregateCircuitScores(
            circuitScores, ratings, rds);

        Assert.True(scores[0] > 500_000_000);
        Assert.True(scores[1] < 500_000_000);
        Assert.Equal((short)AttestationOutcome.Confirm, outcomes[0]);
        Assert.Equal((short)AttestationOutcome.Refute, outcomes[1]);
    }

    [Fact]
    public async Task ExistingTypedClaims_EmitCategoricalReceiptsAndTransientScoresOnly()
    {
        string dir = WriteEmbeddingFixture();
        try
        {
            ModelManifest manifest = FixtureManifest();
            IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens = FixtureTokens();
            Hash128 typeId = ModelDecomposer.SimilarToTypeId;
            var relations = new[]
            {
                new CircuitRelation(tokens[0].EntityId, tokens[1].EntityId, typeId, 0, 1),
                new CircuitRelation(tokens[0].EntityId, tokens[2].EntityId, typeId, 0, 1),
            };
            var reader = new CandidateReader(typeId, relations);
            var etl = new ModelTokenEdgeETL(dir, manifest, tokens,
                SourceEntityIdConventions.ModelContentSourceId(dir)!.Value);

            using CollectedChanges changes = await Collect(etl.EmitAsync(
                1, reader, DecomposerOptions.Default));

            SubstrateChange change = Assert.Single(changes);
            Assert.Empty(change.Entities);
            Assert.Empty(change.Physicalities);
            Assert.Equal(2, change.Attestations.Length);
            Assert.Equal(2, change.EphemeralFoldInputs.Length);
            Assert.All(change.Attestations, row =>
            {
                Assert.Equal(typeId, row.TypeId);
                Assert.False(row.FoldReplayable);
                Assert.NotNull(row.ContextId);
                long expectedRd = (long)(NativeAttestation.WitnessPhi(
                    RelationTypeRegistry.Resolve("SIMILAR_TO").Rank
                    * SourceTrust.AiModelProbe) * 1_000_000_000.0);
                Assert.Equal(expectedRd, row.OpponentRdFp1e9);
            });
            Assert.Equal(AttestationOutcome.Confirm, change.Attestations[0].Outcome);
            Assert.Equal(AttestationOutcome.Refute, change.Attestations[1].Outcome);
            Assert.True(change.EphemeralFoldInputs[0].ScoreFp1e9 > 500_000_000);
            Assert.True(change.EphemeralFoldInputs[1].ScoreFp1e9 < 500_000_000);
            Assert.Equal(4, reader.RequestedTypes.Count);
            Assert.DoesNotContain(change.Metadata.SourceContentUnitName, "top", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CanonicalWireAliases_AggregateAllEmbeddingRowsAsAnOrderIndependentSet()
    {
        string dir = WriteEmbeddingFixture([1, 0, -1, 0, 0, 2]);
        try
        {
            Hash128 alias = Hash128.OfCanonical("test/token/alias");
            Hash128 other = Hash128.OfCanonical("test/token/other");
            LlamaTokenizerParser.TokenRecord[] tokens =
            [
                Token(1, "wire-b", alias),
                Token(0, "wire-a", alias),
                Token(2, "other", other),
            ];
            var relation = new CircuitRelation(
                alias, other, ModelDecomposer.SimilarToTypeId, 0, 1);
            var etl = new ModelTokenEdgeETL(
                dir, FixtureManifest(), tokens,
                SourceEntityIdConventions.ModelContentSourceId(dir)!.Value);

            using CollectedChanges changes = await Collect(etl.EmitAsync(
                1, new CandidateReader(ModelDecomposer.SimilarToTypeId, [relation]),
                DecomposerOptions.Default));
            SubstrateChange change = Assert.Single(changes);

            Assert.Equal(AttestationOutcome.Draw, Assert.Single(change.Attestations).Outcome);
            Assert.Equal(500_000_000, Assert.Single(change.EphemeralFoldInputs).ScoreFp1e9);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TwoIndependentModels_AdmitOnlySameKindSameDirectionGraphNominations()
    {
        string leftDir = WriteEmbeddingFixture([1, 0, 1, 0, 0, 1]);
        string rightDir = WriteEmbeddingFixture([2, 0, 1, 0, 0, -1]);
        try
        {
            IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens = FixtureTokens();
            using SourceEntityIdConventions.ModelContentSnapshot leftSnapshot =
                SourceEntityIdConventions.OpenModelContentSnapshot(leftDir)!;
            using SourceEntityIdConventions.ModelContentSnapshot rightSnapshot =
                SourceEntityIdConventions.OpenModelContentSnapshot(rightDir)!;
            var left = new SelectedModelAnalysisInput(
                leftDir, FixtureManifest(), tokens, leftSnapshot.SourceId, leftSnapshot);
            var right = new SelectedModelAnalysisInput(
                rightDir, FixtureManifest(), tokens, rightSnapshot.SourceId, rightSnapshot);
            Assert.NotEqual(left.SourceId, right.SourceId);
            Hash128 basis = RelationTypeRegistry.RelationTypeId("IS_SIMILAR_TO");
            var proposal = new CircuitPairProposal(
                tokens[0].EntityId, tokens[1].EntityId, [basis]);
            var etl = new ModelSimilarityCorroborationETL(left, right, pageSize: 8);

            List<ModelCorroborationWorkingSet> sets = await Collect(
                etl.AnalyzeAsync(1, new PairProposalReader([proposal])));

            ModelCorroborationWorkingSet set = Assert.Single(sets);
            Assert.Equal(1, set.ProposedPairs);
            Assert.Equal(1, set.AdmittedPairs);
            Assert.Equal(2, set.Changes.Count);
            Assert.Equal(
                new HashSet<Hash128> { left.SourceId, right.SourceId },
                set.Changes.Select(change => change.Metadata.SourceId).ToHashSet());
            Assert.All(set.Changes, change =>
            {
                Assert.Empty(change.Entities);
                Assert.Empty(change.Physicalities);
                Assert.Equal(AttestationOutcome.Confirm, Assert.Single(change.Attestations).Outcome);
                Assert.Equal(ModelDecomposer.SimilarToTypeId, change.Attestations[0].TypeId);
                Assert.False(change.Attestations[0].FoldReplayable);
                Assert.True(Assert.Single(change.EphemeralFoldInputs).ScoreFp1e9 > 500_000_000);
            });
        }
        finally
        {
            Directory.Delete(leftDir, recursive: true);
            Directory.Delete(rightDir, recursive: true);
        }
    }

    [Fact]
    public async Task CorroborationWorkingSet_RechecksBothHeldSnapshotsBeforeApplyCommit()
    {
        string leftDir = WriteEmbeddingFixture([1, 0, 1, 0, 0, 1]);
        string rightDir = WriteEmbeddingFixture([2, 0, 1, 0, 0, 1]);
        try
        {
            IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens = FixtureTokens();
            using SourceEntityIdConventions.ModelContentSnapshot leftSnapshot =
                SourceEntityIdConventions.OpenModelContentSnapshot(leftDir)!;
            using SourceEntityIdConventions.ModelContentSnapshot rightSnapshot =
                SourceEntityIdConventions.OpenModelContentSnapshot(rightDir)!;
            var etl = new ModelSimilarityCorroborationETL(
                new(leftDir, FixtureManifest(), tokens, leftSnapshot.SourceId, leftSnapshot),
                new(rightDir, FixtureManifest(), tokens, rightSnapshot.SourceId, rightSnapshot),
                pageSize: 8);
            Hash128 basis = RelationTypeRegistry.RelationTypeId("IS_SIMILAR_TO");
            CircuitPairProposal proposal = new(
                tokens[0].EntityId, tokens[1].EntityId, [basis]);
            ModelCorroborationWorkingSet set = Assert.Single(await Collect(
                etl.AnalyzeAsync(1, new PairProposalReader([proposal]))));

            string changedPath = Path.Combine(rightDir, "model.safetensors");
            using (var changed = new FileStream(
                       changedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                changed.Position = changed.Length - 1;
                int previous = changed.ReadByte();
                changed.Position--;
                changed.WriteByte((byte)(previous ^ 0xff));
            }

            await Assert.ThrowsAsync<InvalidDataException>(
                () => set.ApplyAsync(new PrecommitVerifyingWriter()));
        }
        finally
        {
            Directory.Delete(leftDir, recursive: true);
            Directory.Delete(rightDir, recursive: true);
        }
    }

    [Fact]
    public async Task TwoModelsThatDisagree_DoNotMaterializeANovelTargetClaim()
    {
        string leftDir = WriteEmbeddingFixture([1, 0, 1, 0, 0, 1]);
        string rightDir = WriteEmbeddingFixture([1, 0, -1, 0, 0, 1]);
        try
        {
            IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens = FixtureTokens();
            using SourceEntityIdConventions.ModelContentSnapshot leftSnapshot =
                SourceEntityIdConventions.OpenModelContentSnapshot(leftDir)!;
            using SourceEntityIdConventions.ModelContentSnapshot rightSnapshot =
                SourceEntityIdConventions.OpenModelContentSnapshot(rightDir)!;
            var etl = new ModelSimilarityCorroborationETL(
                new(leftDir, FixtureManifest(), tokens, leftSnapshot.SourceId, leftSnapshot),
                new(rightDir, FixtureManifest(), tokens, rightSnapshot.SourceId, rightSnapshot),
                pageSize: 8);
            Hash128 basis = RelationTypeRegistry.RelationTypeId("RELATED_TO");
            var proposal = new CircuitPairProposal(
                tokens[0].EntityId, tokens[1].EntityId, [basis]);

            Assert.Empty(await Collect(
                etl.AnalyzeAsync(1, new PairProposalReader([proposal]))));
        }
        finally
        {
            Directory.Delete(leftDir, recursive: true);
            Directory.Delete(rightDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("SIMILAR_TO", 1)]
    [InlineData("ATTENDS", 2)]
    [InlineData("OV_RELATES", 2)]
    [InlineData("COMPLETES_TO", 2)]
    public async Task JointModelAdmission_ContractsEverySupportedTargetKind(
        string targetName, int expectedCircuits)
    {
        (string leftDir, ModelManifest leftManifest) = WriteCircuitFixture(1);
        (string rightDir, ModelManifest rightManifest) = WriteCircuitFixture(2);
        try
        {
            IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens = FixtureTokens();
            using SourceEntityIdConventions.ModelContentSnapshot leftSnapshot =
                SourceEntityIdConventions.OpenModelContentSnapshot(leftDir)!;
            using SourceEntityIdConventions.ModelContentSnapshot rightSnapshot =
                SourceEntityIdConventions.OpenModelContentSnapshot(rightDir)!;
            var etl = new ModelJointCorroborationETL(
                new(leftDir, leftManifest, tokens, leftSnapshot.SourceId, leftSnapshot),
                new(rightDir, rightManifest, tokens, rightSnapshot.SourceId, rightSnapshot),
                pageSize: 8);
            Hash128 targetType = RelationTypeRegistry.RelationTypeId(targetName);
            CircuitPairProposal proposal = new(
                tokens[0].EntityId, tokens[1].EntityId,
                [RelationTypeRegistry.RelationTypeId("RELATED_TO")]);

            ModelCorroborationWorkingSet set = Assert.Single(await Collect(
                etl.AnalyzeTargetAsync(
                    targetName, 1,
                    new TargetPairProposalReader(targetType, [proposal]))));

            Assert.Equal(2, set.Changes.Count);
            Assert.All(set.Changes, change =>
            {
                AttestationRow receipt = Assert.Single(change.Attestations);
                Assert.Equal(targetType, receipt.TypeId);
                Assert.Equal(AttestationOutcome.Confirm, receipt.Outcome);
                Assert.True(Assert.Single(change.EphemeralFoldInputs).ScoreFp1e9 > 500_000_000);
            });
            Assert.True(etl.PeakNativeResidentBytes > 0);
            Assert.True(etl.PeakTransientScoreBytes >= expectedCircuits * sizeof(long));
        }
        finally
        {
            Directory.Delete(leftDir, recursive: true);
            Directory.Delete(rightDir, recursive: true);
        }
    }

    [Fact]
    public async Task ChangedCheckpointBytes_CannotUsePreviouslyAdmittedSourceIdentity()
    {
        string dir = WriteEmbeddingFixture();
        try
        {
            Hash128 admittedSource = SourceEntityIdConventions.ModelContentSourceId(dir)!.Value;
            string path = Path.Combine(dir, "model.safetensors");
            DateTime timestamp = File.GetLastWriteTimeUtc(path);
            byte[] bytes = File.ReadAllBytes(path);
            bytes[^1] ^= 0x01;
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, timestamp);

            IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens = FixtureTokens();
            var candidate = new CircuitRelation(
                tokens[0].EntityId, tokens[1].EntityId,
                ModelDecomposer.SimilarToTypeId, 0, 1);
            var etl = new ModelTokenEdgeETL(
                dir, FixtureManifest(), tokens, admittedSource);

            await Assert.ThrowsAsync<InvalidDataException>(() => Collect(etl.EmitAsync(
                1, new CandidateReader(ModelDecomposer.SimilarToTypeId, [candidate]),
                DecomposerOptions.Default)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RealMiniLmCheckpoint_ContractsACompleteExistingClaimWithoutTensorPayload()
    {
        if (!File.Exists(Path.Combine(MiniLm, "model.safetensors")))
        {
            _output.WriteLine("MiniLM checkpoint not installed; real-checkpoint proof skipped.");
            return;
        }
        if (!CodepointPerfcache.IsLoaded)
            CodepointPerfcache.Load(TestInstall.ResolvePerfcacheOrThrow());

        var config = ModelConfigReader.Read(Path.Combine(MiniLm, "config.json"));
        var tensors = SafetensorsContainerParser.ParseModel(MiniLm);
        ModelManifest manifest = TensorRoleClassifier.Build(tensors, config, "all-MiniLM-L6-v2");
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens =
            LlamaTokenizerParser.Parse(Path.Combine(MiniLm, "tokenizer.json"));
        LlamaTokenizerParser.TokenRecord first = tokens.First(t => t.TokenId == 100);
        LlamaTokenizerParser.TokenRecord second = tokens.First(t => t.TokenId == 101);
        var candidate = new CircuitRelation(
            first.EntityId, second.EntityId, ModelDecomposer.SimilarToTypeId, 0, 1);
        var reader = new CandidateReader(ModelDecomposer.SimilarToTypeId, [candidate]);
        Hash128 source = ModelDecomposer.SourceForModel(MiniLm).Id;
        var etl = new ModelTokenEdgeETL(MiniLm, manifest, tokens, source);

        var sw = Stopwatch.StartNew();
        using CollectedChanges changes = await Collect(etl.EmitAsync(
            1, reader, DecomposerOptions.Default));
        sw.Stop();

        SubstrateChange change = Assert.Single(changes);
        Assert.Single(change.Attestations);
        Assert.Single(change.EphemeralFoldInputs);
        Assert.Empty(change.Entities);
        Assert.Empty(change.Physicalities);
        Assert.False(change.Attestations[0].FoldReplayable);
        Assert.InRange(change.EphemeralFoldInputs[0].ScoreFp1e9, 0, 1_000_000_000);
        _output.WriteLine(
            $"MiniLM exact embedding arena: vocab={manifest.Config.VocabSize}, d={manifest.Config.HiddenSize}, " +
            $"claims=1, elapsed_ms={sw.ElapsedMilliseconds}, durable_tensor_bytes=0");
    }

    [Fact]
    public async Task RealMiniLmLayer_ContractsAttentionValueOutputAndFfnKinds()
    {
        if (!File.Exists(Path.Combine(MiniLm, "model.safetensors"))) return;
        if (!CodepointPerfcache.IsLoaded)
            CodepointPerfcache.Load(TestInstall.ResolvePerfcacheOrThrow());

        var config = ModelConfigReader.Read(Path.Combine(MiniLm, "config.json"));
        var tensors = SafetensorsContainerParser.ParseModel(MiniLm);
        ModelManifest full = TensorRoleClassifier.Build(tensors, config, "all-MiniLM-L6-v2");
        var manifest = new ModelManifest
        {
            ModelName = full.ModelName, Modality = full.Modality, Coverage = full.Coverage,
            Config = full.Config with { NumLayers = 1 },
            Roles = full.Roles.Where(r => r.LayerIndex <= 0).ToArray(),
        };
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> parsed =
            LlamaTokenizerParser.Parse(Path.Combine(MiniLm, "tokenizer.json"));
        LlamaTokenizerParser.TokenRecord[] endpoints = parsed
            .GroupBy(t => t.EntityId).Select(g => g.First()).Take(4).ToArray();
        var reader = new AllModelKindCandidateReader(endpoints[0].EntityId, endpoints[1].EntityId);
        var etl = new ModelTokenEdgeETL(MiniLm, manifest, parsed,
            ModelDecomposer.SourceForModel(MiniLm).Id);

        var sw = Stopwatch.StartNew();
        using CollectedChanges changes = await Collect(etl.EmitAsync(
            1, reader, DecomposerOptions.Default));
        sw.Stop();
        AttestationRow[] receipts = changes.SelectMany(c => c.Attestations).ToArray();

        Assert.Equal(26, receipts.Length);
        Assert.Equal(1, receipts.Count(r => r.TypeId == ModelDecomposer.SimilarToTypeId));
        Assert.Equal(12, receipts.Count(r => r.TypeId == ModelDecomposer.AttendsTypeId));
        Assert.Equal(12, receipts.Count(r => r.TypeId == ModelDecomposer.OvRelatesTypeId));
        Assert.Equal(1, receipts.Count(r => r.TypeId == ModelDecomposer.CompletesToTypeId));
        Assert.All(changes, c =>
        {
            Assert.Empty(c.Entities);
            Assert.Empty(c.Physicalities);
            Assert.Equal(c.Attestations.Length, c.EphemeralFoldInputs.Length);
        });
        Assert.InRange(etl.PeakNativeResidentBytes, 1, 256L * 1024 * 1024);
        _output.WriteLine(
            $"MiniLM full-vocabulary one-layer contraction: tokenizer_rows={parsed.Count}, " +
            $"canonical_entities={parsed.Select(t => t.EntityId).Distinct().Count()}, " +
            $"peak_native_resident_bytes={etl.PeakNativeResidentBytes}, elapsed_ms={sw.ElapsedMilliseconds}");
    }

    private static async Task<CollectedChanges> Collect(IAsyncEnumerable<SubstrateChange> source)
    {
        var result = new CollectedChanges();
        await foreach (var change in source) result.Add(change);
        return result;
    }

    private sealed class CollectedChanges : List<SubstrateChange>, IDisposable
    {
        public void Dispose()
        {
            foreach (SubstrateChange change in this)
                change.ApplyEnvelope?.Dispose();
        }
    }

    private static async Task<List<ModelCorroborationWorkingSet>> Collect(
        IAsyncEnumerable<ModelCorroborationWorkingSet> source)
    {
        var result = new List<ModelCorroborationWorkingSet>();
        await foreach (ModelCorroborationWorkingSet set in source) result.Add(set);
        return result;
    }

    private static string WriteEmbeddingFixture(float[]? tensorValues = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace-model-contraction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string json = "{\"embeddings.word_embeddings.weight\":{\"dtype\":\"F32\",\"shape\":[3,2],\"data_offsets\":[0,24]}}";
        byte[] header = Encoding.UTF8.GetBytes(json);
        byte[] bytes = new byte[8 + header.Length + 24];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)header.Length);
        header.CopyTo(bytes, 8);
        float[] values = tensorValues ?? [1, 0, 1, 0, -1, 0];
        if (values.Length != 6) throw new ArgumentException("fixture requires exactly six values", nameof(tensorValues));
        Buffer.BlockCopy(values, 0, bytes, 8 + header.Length, 24);
        File.WriteAllBytes(Path.Combine(dir, "model.safetensors"), bytes);
        return dir;
    }

    private static (string Directory, ModelManifest Manifest) WriteCircuitFixture(float scale)
    {
        string dir = Path.Combine(
            Path.GetTempPath(), "laplace-model-circuits-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tensors = new (string Name, int[] Shape, float[] Values, TensorRoleKind Kind)[]
        {
            ("embeddings.word_embeddings.weight", [3, 2],
                [scale, scale, scale, scale, -scale, -scale], TensorRoleKind.Embedding),
            ("lm_head.weight", [3, 2],
                [scale, scale, scale, scale, -scale, -scale], TensorRoleKind.LmHead),
            ("encoder.layer.0.attention.self.query.weight", [2, 2],
                [scale, 0, 0, scale], TensorRoleKind.AttnQ),
            ("encoder.layer.0.attention.self.key.weight", [2, 2],
                [scale, 0, 0, scale], TensorRoleKind.AttnK),
            ("encoder.layer.0.attention.self.value.weight", [2, 2],
                [scale, 0, 0, scale], TensorRoleKind.AttnV),
            ("encoder.layer.0.attention.output.dense.weight", [2, 2],
                [scale, 0, 0, scale], TensorRoleKind.AttnO),
            ("encoder.layer.0.intermediate.dense.weight", [3, 2],
                [scale, 0, 0, scale, scale, scale], TensorRoleKind.MlpUp),
            ("encoder.layer.0.output.dense.weight", [2, 3],
                [scale, 0, scale, 0, scale, scale], TensorRoleKind.MlpDown),
        };
        var json = new StringBuilder("{");
        int dataBytes = 0;
        for (int i = 0; i < tensors.Length; i++)
        {
            if (i > 0) json.Append(',');
            int begin = dataBytes;
            dataBytes = checked(dataBytes + tensors[i].Values.Length * sizeof(float));
            json.Append('"').Append(tensors[i].Name)
                .Append("\":{\"dtype\":\"F32\",\"shape\":[")
                .Append(string.Join(',', tensors[i].Shape))
                .Append("],\"data_offsets\":[")
                .Append(begin).Append(',').Append(dataBytes).Append("]}");
        }
        json.Append('}');
        byte[] header = Encoding.UTF8.GetBytes(json.ToString());
        byte[] bytes = new byte[8 + header.Length + dataBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)header.Length);
        header.CopyTo(bytes, 8);
        int at = 8 + header.Length;
        foreach (var tensor in tensors)
        {
            Buffer.BlockCopy(tensor.Values, 0, bytes, at, tensor.Values.Length * sizeof(float));
            at += tensor.Values.Length * sizeof(float);
        }
        File.WriteAllBytes(Path.Combine(dir, "model.safetensors"), bytes);

        var roles = new List<TensorRole>(tensors.Length);
        foreach (var tensor in tensors)
            roles.Add(new TensorRole(
                tensor.Name, tensor.Shape, "F32",
                tensor.Kind,
                tensor.Kind is TensorRoleKind.Embedding or TensorRoleKind.LmHead ? -1 : 0,
                -1));
        return (dir, new ModelManifest
        {
            ModelName = "circuit-fixture",
            Modality = Modality.Text,
            Coverage = Coverage.Full,
            Config = new ModelConfig
            {
                ModelType = "bert", Architecture = "BertModel",
                VocabSize = 3, HiddenSize = 2, NumLayers = 1,
                NumHeads = 2, NumKvHeads = 2, HeadDim = 1,
                IntermediateSize = 3, NumExperts = 0,
                TieWordEmbeddings = false, QkNorm = false,
                RopeTheta = 0, NormEps = 1e-12, HiddenAct = "gelu",
                MlaQLoraRank = 0, MlaKvLoraRank = 0,
                QkRopeHeadDim = 0, QkNopeHeadDim = 0, VHeadDim = 0,
                RecipeEntityId = default, CanonicalJson = [],
            },
            Roles = roles,
        });
    }

    private static ModelManifest FixtureManifest() => new()
    {
        ModelName = "fixture",
        Modality = Modality.Text,
        Coverage = Coverage.Full,
        Config = new ModelConfig
        {
            ModelType = "bert", Architecture = "BertModel", VocabSize = 3,
            HiddenSize = 2, NumLayers = 0, NumHeads = 1, NumKvHeads = 1,
            HeadDim = 2, IntermediateSize = 0, NumExperts = 0,
            TieWordEmbeddings = true, QkNorm = false, RopeTheta = 0,
            NormEps = 1e-12, HiddenAct = "gelu", MlaQLoraRank = 0,
            MlaKvLoraRank = 0, QkRopeHeadDim = 0, QkNopeHeadDim = 0,
            VHeadDim = 0, RecipeEntityId = default, CanonicalJson = [],
        },
        Roles =
        [
            new TensorRole("embeddings.word_embeddings.weight", [3, 2], "F32",
                TensorRoleKind.Embedding, -1, -1),
        ],
    };

    private static IReadOnlyList<LlamaTokenizerParser.TokenRecord> FixtureTokens() =>
    [
        Token(0, "a", Hash128.OfCanonical("test/token/a")),
        Token(1, "b", Hash128.OfCanonical("test/token/b")),
        Token(2, "c", Hash128.OfCanonical("test/token/c")),
    ];

    private static LlamaTokenizerParser.TokenRecord Token(int id, string raw, Hash128 entity) => new()
    {
        TokenId = id, RawToken = raw, CanonicalBytes = Encoding.UTF8.GetBytes(raw),
        EntityId = entity, Tier = 0, IsByteLevel = false, Role = TokenRole.None,
        ContentX = 0, ContentY = 0, ContentZ = 0, ContentM = 0, HasContentCoord = true,
    };

    private sealed class CandidateReader(Hash128 admittedType, IReadOnlyList<CircuitRelation> relations)
        : ISubstrateReader
    {
        public List<Hash128> RequestedTypes { get; } = [];

        public Task<CircuitCandidatePage> ReadCircuitCandidatesAsync(
            IReadOnlyList<Hash128> vocabulary, Hash128 typeId,
            Hash128? afterSubject, Hash128? afterObject, int pageSize,
            CancellationToken ct = default)
        {
            RequestedTypes.Add(typeId);
            IReadOnlyList<CircuitRelation> rows = typeId == admittedType && afterSubject is null
                ? relations
                : Array.Empty<CircuitRelation>();
            return Task.FromResult(new CircuitCandidatePage(rows, null, null));
        }

        public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default)
            => Task.FromResult(0L);
        public Task<byte[]> EntitiesExistBitmapAsync(
            IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
            => Task.FromResult(new byte[(candidates.Count + 7) / 8]);
    }

    private sealed class PrecommitVerifyingWriter : ISubstrateWriter
    {
        public Task<ApplyResult> ApplyAsync(
            SubstrateChange change, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async Task<ApplyResult> ApplyWorkingSetAsync(
            IReadOnlyList<SubstrateChange> changes,
            Func<CancellationToken, ValueTask> precommitVerifier,
            CancellationToken ct = default)
        {
            await precommitVerifier(ct);
            return new ApplyResult(
                0, 0, 0, 0,
                changes.Sum(static change => change.Attestations.Length), 0,
                0, TimeSpan.Zero, false);
        }
    }

    private sealed class AllModelKindCandidateReader(Hash128 subject, Hash128 obj) : ISubstrateReader
    {
        private static readonly HashSet<Hash128> Types =
        [
            ModelDecomposer.SimilarToTypeId,
            ModelDecomposer.AttendsTypeId,
            ModelDecomposer.OvRelatesTypeId,
            ModelDecomposer.CompletesToTypeId,
        ];

        public Task<CircuitCandidatePage> ReadCircuitCandidatesAsync(
            IReadOnlyList<Hash128> vocabulary, Hash128 typeId,
            Hash128? afterSubject, Hash128? afterObject, int pageSize,
            CancellationToken ct = default)
        {
            IReadOnlyList<CircuitRelation> rows = Types.Contains(typeId) && afterSubject is null
                ? [new CircuitRelation(subject, obj, typeId, 0, 1)]
                : Array.Empty<CircuitRelation>();
            return Task.FromResult(new CircuitCandidatePage(rows, null, null));
        }

        public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default)
            => Task.FromResult(0L);
        public Task<byte[]> EntitiesExistBitmapAsync(
            IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
            => Task.FromResult(new byte[(candidates.Count + 7) / 8]);
    }

    private sealed class PairProposalReader(IReadOnlyList<CircuitPairProposal> proposals) : ISubstrateReader
    {
        public Task<CircuitPairProposalPage> ReadCircuitPairProposalsAsync(
            IReadOnlyList<Hash128> vocabulary, Hash128 targetTypeId, bool targetSymmetric,
            Hash128? afterSubject, Hash128? afterObject, int pageSize,
            CancellationToken ct = default)
        {
            Assert.Equal(ModelDecomposer.SimilarToTypeId, targetTypeId);
            Assert.True(targetSymmetric);
            IReadOnlyList<CircuitPairProposal> rows = afterSubject is null
                ? proposals
                : Array.Empty<CircuitPairProposal>();
            return Task.FromResult(new CircuitPairProposalPage(rows, null, null));
        }

        public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default)
            => Task.FromResult(0L);
        public Task<byte[]> EntitiesExistBitmapAsync(
            IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
            => Task.FromResult(new byte[(candidates.Count + 7) / 8]);
    }

    private sealed class TargetPairProposalReader(
        Hash128 targetType, IReadOnlyList<CircuitPairProposal> proposals) : ISubstrateReader
    {
        public Task<CircuitPairProposalPage> ReadCircuitPairProposalsAsync(
            IReadOnlyList<Hash128> vocabulary, Hash128 requestedType, bool targetSymmetric,
            Hash128? afterSubject, Hash128? afterObject, int pageSize,
            CancellationToken ct = default)
        {
            Assert.Equal(targetType, requestedType);
            IReadOnlyList<CircuitPairProposal> rows = afterSubject is null
                ? proposals
                : Array.Empty<CircuitPairProposal>();
            return Task.FromResult(new CircuitPairProposalPage(rows, null, null));
        }

        public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default)
            => Task.FromResult(0L);
        public Task<byte[]> EntitiesExistBitmapAsync(
            IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
            => Task.FromResult(new byte[(candidates.Count + 7) / 8]);
    }

    private sealed class PlanesEnvironment : IDisposable
    {
        private readonly string? _old = Environment.GetEnvironmentVariable("LAPLACE_MODEL_PLANES");
        public PlanesEnvironment(string? value) => Environment.SetEnvironmentVariable("LAPLACE_MODEL_PLANES", value);
        public void Dispose() => Environment.SetEnvironmentVariable("LAPLACE_MODEL_PLANES", _old);
    }
}
