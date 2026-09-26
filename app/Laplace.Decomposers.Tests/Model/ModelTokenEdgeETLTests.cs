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
    [InlineData("ATTENDS", "903e286faebb410c2f107de6aa095f03")]
    [InlineData("COMPLETES_TO", "49ec81960cbe360cdb5ce7de5b049143")]
    [InlineData("OV_RELATES", "2b09e12315238ab81c865effe3d92b10")]
    [InlineData("SIMILAR_TO", "b143f4ae76d9784bfaece173f44acf4a")]
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
    public async Task CircuitPhysicalityIsRecordedWithoutAnyPreexistingGraphClaim()
    {
        string dir = WriteEmbeddingFixture([1, 0, 0, 3, 2, 0]);
        try
        {
            ModelManifest manifest = FixtureManifest();
            IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens = FixtureTokens();
            Hash128 source = SourceEntityIdConventions.ModelContentSourceId(dir)!.Value;
            var etl = new ModelTokenEdgeETL(dir, manifest, tokens, source, "fixture");

            using CollectedChanges changes = await Collect(
                etl.EmitAsync(1, reader: null, DecomposerOptions.Default));

            SubstrateChange circuit = Assert.Single(
                changes.Where(change => change.Physicalities.Length == 1));
            PhysicalityRow physicality = Assert.Single(circuit.Physicalities);
            Assert.Equal(PhysicalityType.Projection, physicality.Type);
            Assert.Equal(
                ModelCoordinates.CircuitId(source, "fixture", "embedding", -1, -1),
                physicality.EntityId);
            Assert.Equal(3, physicality.NConstituents);
            Assert.Equal(1, physicality.SourceDim);
            Assert.NotNull(physicality.TrajectoryXyzm);
            TestimonyWalk.Vertex[] vertices =
                TestimonyWalk.Unpack(physicality.TrajectoryXyzm!);
            Assert.Equal(3, vertices.Length);
            Assert.Equal(tokens[1].EntityId, vertices[0].ObjectId);
            Assert.Equal(tokens[2].EntityId, vertices[1].ObjectId);
            Assert.Equal(tokens[0].EntityId, vertices[2].ObjectId);
            Assert.True(vertices[0].ScoreFp1e9 > vertices[1].ScoreFp1e9);
            Assert.True(vertices[1].ScoreFp1e9 > vertices[2].ScoreFp1e9);
            Assert.Empty(circuit.Attestations);
            Assert.Empty(circuit.EphemeralFoldInputs);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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
    public void SameStructuralAddress_InTwoModels_IsTwoCircuits()
    {
        Hash128 a = SourceWitness.Id("model-a", "1");
        Hash128 b = SourceWitness.Id("model-b", "1");
        Hash128 left = ModelCoordinates.CircuitId(a, "model-a", "attention", 3, 5);
        Hash128 right = ModelCoordinates.CircuitId(b, "model-b", "attention", 3, 5);
        Assert.NotEqual(left, right);
        Assert.Equal(left, ModelCoordinates.CircuitId(a, "model-a", "attention", 3, 5));
        Assert.NotEqual(left, ModelCoordinates.CircuitId(a, "model-a", "attention", 3, 6));
    }

    [Fact]
    public async Task CircuitEvidence_NeedsNoPriorConsensus_AndIsNeverARefutation()
    {
        // Twenty-four entities: 0 and 1 share a direction, 2 opposes 3, the rest
        // are spread. The circuit's own per-subject null decides what is written.
        const int n = 24, dim = 16;
        var values = new float[n * dim];
        ulong state = 0x9e3779b97f4a7c15UL;
        for (int i = 0; i < values.Length; i++)
        {
            state = state * 6364136223846793005UL + 1442695040888963407UL;
            values[i] = (float)((state >> 11) / (double)(1UL << 53) - 0.5);
        }
        for (int k = 0; k < dim; k++)
        {
            values[k] = 4f; values[dim + k] = 4f;
            values[3 * dim + k] = -values[2 * dim + k];
        }
        string dir = WriteEmbeddingFixture(values, n, dim);
        try
        {
            IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens = Enumerable.Range(0, n)
                .Select(i => Token(i, "t" + i, Hash128.OfCanonical("test/token/sig/" + i)))
                .ToArray();
            Hash128 source = SourceEntityIdConventions.ModelContentSourceId(dir)!.Value;
            var etl = new ModelTokenEdgeETL(
                dir, EmbeddingManifest(n, dim), tokens, source, "fixture");

            using CollectedChanges changes = await Collect(
                etl.EmitAsync(1, reader: null, DecomposerOptions.Default));

            AttestationRow[] claims = changes.SelectMany(c => c.Attestations).ToArray();
            EphemeralFoldInput[] grades = changes.SelectMany(c => c.EphemeralFoldInputs).ToArray();
            Assert.NotEmpty(claims);
            Assert.Equal(claims.Length, grades.Length);
            Assert.All(claims, claim =>
            {
                Assert.Equal(ModelDecomposer.SimilarToTypeId, claim.TypeId);
                Assert.Equal(AttestationOutcome.Confirm, claim.Outcome);
                Assert.Equal(source, claim.SourceId);
                Assert.NotEqual(claim.SubjectId, claim.ObjectId);
                Assert.True((claim.QualifierMask & CalculationSources.Qualifier) == CalculationSources.Qualifier);
            });
            Assert.All(grades, g => Assert.True(g.ScoreFp1e9 > 500_000_000));
            bool Pair(AttestationRow c, int a, int b) =>
                (c.SubjectId == tokens[a].EntityId && c.ObjectId == tokens[b].EntityId)
                || (c.SubjectId == tokens[b].EntityId && c.ObjectId == tokens[a].EntityId);
            // SIMILAR_TO is symmetric: the unordered pair is one claim.
            Assert.Single(claims, c => Pair(c, 0, 1));
            Assert.DoesNotContain(claims, c => Pair(c, 2, 3));
            Assert.True(claims.Length < n * (n - 1) / 4, $"{claims.Length} claims is not a significance bound");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TinyCircuit_HasNoSignificantPair_AndWritesNoClaim()
    {
        string dir = WriteEmbeddingFixture([1, 0, 1, 0, -1, 0]);
        try
        {
            var etl = new ModelTokenEdgeETL(dir, FixtureManifest(), FixtureTokens(),
                SourceEntityIdConventions.ModelContentSourceId(dir)!.Value, "fixture");
            using CollectedChanges changes = await Collect(etl.EmitAsync(
                1, reader: null, DecomposerOptions.Default));
            Assert.Contains(changes, c => c.Physicalities.Any(p => p.Type == PhysicalityType.Projection));
            Assert.Empty(changes.SelectMany(c => c.Attestations));
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
            var etl = new ModelTokenEdgeETL(
                dir, FixtureManifest(), tokens, admittedSource, "fixture");

            await Assert.ThrowsAsync<InvalidDataException>(() => Collect(etl.EmitAsync(
                1, reader: null, DecomposerOptions.Default)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RealMiniLmHeader_FeedsEveryCircuitKindByShape()
    {
        if (!File.Exists(Path.Combine(MiniLm, "model.safetensors")))
        {
            _output.WriteLine("MiniLM checkpoint not installed; real-checkpoint recognition skipped.");
            return;
        }
        var config = ModelConfigReader.Read(Path.Combine(MiniLm, "config.json"));
        var tensors = SafetensorsContainerParser.ParseModel(MiniLm);
        int? ids = LlamaTokenizerParser.IdSpace(File.ReadAllBytes(Path.Combine(MiniLm, "tokenizer.json")));
        ModelManifest manifest = ModelManifest.Recognize(tensors, config, ids, "all-MiniLM-L6-v2");
        Assert.True(manifest.TextPlanesRunnable);
        Assert.Equal(6, manifest.LayerCount);
        for (int layer = 0; layer < manifest.LayerCount; layer++)
        {
            Assert.NotNull(manifest.Single(layer, TensorRoleKind.AttnQ)?.Bias);
            Assert.NotNull(manifest.Single(layer, TensorRoleKind.AttnK));
            Assert.NotNull(manifest.Single(layer, TensorRoleKind.AttnV));
            Assert.NotNull(manifest.Single(layer, TensorRoleKind.AttnO));
            Assert.NotNull(manifest.Single(layer, TensorRoleKind.MlpUp)?.Bias);
            Assert.NotNull(manifest.Single(layer, TensorRoleKind.MlpDown));
            Assert.Null(manifest.Single(layer, TensorRoleKind.MlpGate));
        }
        Assert.Equal(1, manifest.FfnActivation(gated: false));
        _output.WriteLine(manifest.Anatomy.Describe());
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

    [Fact]
    public void UntiedOutputChangesOvAndFfnButNotAttention()
    {
        var normal = WriteCircuitFixture(1);
        var reversed = WriteCircuitFixture(1, -1);
        try
        {
            var tokens = FixtureTokens();
            var canonical = tokens.Select((token, index) => (token.EntityId, index))
                .ToDictionary(pair => pair.EntityId, pair => pair.index);
            using var a = SourceEntityIdConventions.OpenModelContentSnapshot(normal.Directory)!;
            using var b = SourceEntityIdConventions.OpenModelContentSnapshot(reversed.Directory)!;
            var left = new ModelCircuitEstate(new SelectedModelAnalysisInput(
                normal.Directory, normal.Manifest, tokens, a.SourceId, a), canonical);
            var right = new ModelCircuitEstate(new SelectedModelAnalysisInput(
                reversed.Directory, reversed.Manifest, tokens, b.SourceId, b), canonical);
            foreach (var type in new[] { ModelDecomposer.AttendsTypeId,
                         ModelDecomposer.OvRelatesTypeId, ModelDecomposer.CompletesToTypeId })
            {
                var first = left.Enumerate(type).Select(c =>
                    (c.Plane, Score: c.Contraction.Score([0], [1]).Scores[0])).ToArray();
                var second = right.Enumerate(type).Select(c =>
                {
                    if (c.Plane is "value-output" or "ffn")
                        Assert.Contains("lm_head.weight", c.TensorNames);
                    return (c.Plane, Score: c.Contraction.Score([0], [1]).Scores[0]);
                }).ToArray();
                Assert.NotEmpty(first);
                Assert.Equal(first.Length, second.Length);
                for (int i = 0; i < first.Length; i++)
                {
                    Assert.Equal(first[i].Plane, second[i].Plane);
                    if (type == ModelDecomposer.AttendsTypeId)
                        Assert.Equal(first[i].Score, second[i].Score);
                    else
                    {
                        Assert.True(first[i].Score > 500_000_000);
                        Assert.True(second[i].Score < 500_000_000);
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(normal.Directory, recursive: true);
            Directory.Delete(reversed.Directory, recursive: true);
        }
    }

    private static string WriteEmbeddingFixture(float[]? tensorValues = null, int rows = 3, int dim = 2)
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace-model-contraction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        int payload = rows * dim * sizeof(float);
        string json = "{\"embeddings.word_embeddings.weight\":{\"dtype\":\"F32\",\"shape\":[" + rows + "," + dim
            + "],\"data_offsets\":[0," + payload + "]}}";
        byte[] header = Encoding.UTF8.GetBytes(json);
        byte[] bytes = new byte[8 + header.Length + payload];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)header.Length);
        header.CopyTo(bytes, 8);
        float[] values = tensorValues ?? [1, 0, 1, 0, -1, 0];
        if (values.Length != rows * dim) throw new ArgumentException("fixture values disagree with its shape", nameof(tensorValues));
        Buffer.BlockCopy(values, 0, bytes, 8 + header.Length, payload);
        File.WriteAllBytes(Path.Combine(dir, "model.safetensors"), bytes);
        return dir;
    }

    private static (string Directory, ModelManifest Manifest) WriteCircuitFixture(float scale, float outputSign = 1)
    {
        string dir = Path.Combine(
            Path.GetTempPath(), "laplace-model-circuits-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tensors = new (string Name, int[] Shape, float[] Values, TensorRoleKind Kind)[]
        {
            ("embeddings.word_embeddings.weight", [3, 2],
                [scale, scale, scale, scale, -scale, -scale], TensorRoleKind.Embedding),
            ("lm_head.weight", [3, 2],
                [outputSign * scale, outputSign * scale, outputSign * scale, outputSign * scale, -outputSign * scale, -outputSign * scale], TensorRoleKind.LmHead),
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

        ModelManifest manifest = ModelManifest.Recognize(
            tensors.Select(t => new HeaderTensor(t.Name, "F32", t.Shape)).ToArray(),
            ConfigResult(BertConfig(3, 2, 1, 2, 1, 3), new()
            {
                ["hidden_size"] = 2, ["num_attention_heads"] = 2, ["head_dim"] = 1,
                ["num_hidden_layers"] = 1, ["intermediate_size"] = 3,
            }),
            3, "circuit-fixture");
        foreach (var tensor in tensors)
            Assert.Equal(tensor.Kind, Assert.Single(manifest.Roles, r => r.Name == tensor.Name).Kind);
        return (dir, manifest);
    }

    private static ModelConfig BertConfig(int vocab, int hidden, int layers, int heads, int headDim, int interm) => new()
    {
        ModelType = "bert", Architecture = "BertModel",
        VocabSize = vocab, HiddenSize = hidden, NumLayers = layers,
        NumHeads = heads, NumKvHeads = heads, HeadDim = headDim,
        IntermediateSize = interm, NumExperts = 0,
        TieWordEmbeddings = false, QkNorm = false,
        RopeTheta = 0, NormEps = 1e-12, HiddenAct = "gelu",
        MlaQLoraRank = 0, MlaKvLoraRank = 0,
        QkRopeHeadDim = 0, QkNopeHeadDim = 0, VHeadDim = 0,
    };

    private static ModelConfigReader.Result ConfigResult(ModelConfig config, Dictionary<string, long> ints) =>
        new(config, Modality.Text, Coverage.Full, ints);

    private static ModelManifest EmbeddingManifest(int rows, int dim) => ModelManifest.Recognize(
        new HeaderTensor[] { new("embeddings.word_embeddings.weight", "F32", [rows, dim]) },
        ConfigResult(BertConfig(rows, dim, 0, 1, dim, 0), new()
        {
            ["hidden_size"] = dim, ["num_attention_heads"] = 1,
        }),
        rows, "fixture");

    private static ModelManifest FixtureManifest() => EmbeddingManifest(3, 2);

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
