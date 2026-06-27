using System.Text.Json;
using global::Npgsql;
using NpgsqlTypes;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Model;
using Laplace.Engine.Core;
using static Laplace.Cli.CliRuntime;

namespace Laplace.Cli.Provenance;

// Reads the substrate + recipe and produces a ProvenanceRecord — the canonical source material.
// Renderers (markdown, html, pdf, csv) take the JSON record as input and never touch the substrate.
internal static class ProvenanceExtractor
{
    // Inverted map: type-id → canonical relation name. Built once from RelationTypeRegistry.AllCanonical()
    // so ENCODES object_ids can be labeled without a reverse-lookup SQL function. AllCanonical() reads
    // directly from the native manifest (loaded from relation_types.toml) — no hardcoded list here.
    private static readonly Lazy<Dictionary<Hash128, string>> TypeIdToName = new(() =>
        RelationTypeRegistry.AllCanonical()
            .ToDictionary(r => r.Id, r => r.Canonical));

    private static string RelationName(Hash128 typeId)
        => TypeIdToName.Value.TryGetValue(typeId, out var n) ? n : Hex(typeId);

    public static async Task<ProvenanceRecord> ExtractAsync(
        NpgsqlDataSource ds, string recipePath, CancellationToken ct = default)
    {
        string modelDir = Path.GetDirectoryName(Path.GetFullPath(recipePath)) ?? ".";
        var recipe = LlamaRecipeExtractor.Parse(recipePath);
        var (_, modelName) = ModelDecomposer.SourceForModel(modelDir);

        var identity = BuildIdentity(recipe, modelName);
        var sources  = await BuildSourcesAsync(ds, modelName, ct);
        var tensors  = BuildTensorProvenance(recipe, sources);
        var circuits = await BuildCircuitsAsync(ds, modelName, recipe, ct);

        return new ProvenanceRecord
        {
            SchemaVersion = "1.0",
            GeneratedBy   = "laplace document (Laplace CLI)",
            Identity      = identity,
            Sources       = sources,
            Tensors       = tensors,
            Circuits      = circuits,
        };
    }

    // ── Identity ─────────────────────────────────────────────────────────────

    private static ModelIdentity BuildIdentity(
        LlamaRecipeExtractor.RecipeInfo recipe, string modelName)
        => new()
        {
            RecipeHash   = Hex(recipe.RecipeEntityId),
            Architecture = recipe.Architecture,
            Name         = modelName,
            MoldOrigin   = "recipe-file",
            Config       = new Dictionary<string, string>
            {
                ["vocab_size"]             = recipe.VocabSize.ToString(),
                ["hidden_size"]            = recipe.HiddenSize.ToString(),
                ["num_hidden_layers"]      = recipe.NumLayers.ToString(),
                ["num_attention_heads"]    = recipe.NumHeads.ToString(),
                ["num_key_value_heads"]    = recipe.NumKvHeads.ToString(),
                ["intermediate_size"]      = recipe.IntermediateSize.ToString(),
                ["model_type"]             = recipe.ModelType,
                ["hidden_act"]             = recipe.HiddenAct,
                ["rope_theta"]             = recipe.RopeTheta.ToString("G"),
                ["rms_norm_eps"]           = recipe.RmsNormEps.ToString("G"),
                ["torch_dtype"]            = recipe.TorchDtype,
            },
            GgufMetadata = new Dictionary<string, string>
            {
                ["general.architecture"] = "llama",
                ["general.name"]         = modelName,
            },
        };

    // ── Sources ───────────────────────────────────────────────────────────────

    private static async Task<List<SourceProvenance>> BuildSourcesAsync(
        NpgsqlDataSource ds, string modelName, CancellationToken ct)
    {
        var sources = new List<SourceProvenance>();

        // Ingested models: laplace.model_recipes() → recipe_id + recipe_json.
        try
        {
            await using var cmd = ds.CreateCommand(
                "SELECT recipe_id, recipe_json FROM laplace.model_recipes()");
            cmd.CommandTimeout = 30;
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var rid   = Hash128.FromBytes((byte[])rdr[0]);
                string name = modelName;
                try
                {
                    using var doc = JsonDocument.Parse(rdr.GetString(1));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("model_type", out var mt) && mt.GetString() is { } s)
                        name = s;
                }
                catch { }
                sources.Add(new SourceProvenance
                {
                    SourceId   = Hex(rid),
                    Domain     = "substrate/source/model/v1",
                    Kind       = "ingested-model",
                    Label      = name,
                    Completed  = true,
                    TrustClass = "AIModelProbe",
                });
            }
        }
        catch { }

        // Lexical resources: entity-count probe for each known resource type.
        // A non-zero count confirms that resource has been ingested into the substrate.
        var probes = new (string Label, string Domain, Hash128 TypeId)[]
        {
            ("WordNet",  "substrate/source/wordnet/v1",  EntityTypeRegistry.WordNetSynset),
            ("FrameNet", "substrate/source/framenet/v1", EntityTypeRegistry.FrameNetFrame),
            ("VerbNet",  "substrate/source/verbnet/v1",  EntityTypeRegistry.VerbNetClass),
            ("PropBank", "substrate/source/propbank/v1", EntityTypeRegistry.PropBankRoleset),
        };
        foreach (var (label, domain, typeId) in probes)
        {
            try
            {
                long count = await CountByTypeAsync(ds, typeId, ct);
                if (count > 0)
                    sources.Add(new SourceProvenance
                    {
                        SourceId    = Hex(Hash128.OfCanonical(domain)),
                        Domain      = domain,
                        Kind        = "lexical-resource",
                        Label       = label,
                        Completed   = true,
                        EntityCount = count,
                        TrustClass  = "AcademicCurated",
                    });
            }
            catch { }
        }

        return sources;
    }

    private static async Task<long> CountByTypeAsync(
        NpgsqlDataSource ds, Hash128 typeId, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(
            "SELECT count(*) FROM laplace.entities WHERE type_id = $1");
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bytea, typeId.ToBytes());
        cmd.CommandTimeout = 30;
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is long l ? l : 0L;
    }

    // ── Tensors ───────────────────────────────────────────────────────────────
    // Tensor→plane mapping is DETERMINISTIC from the recipe (same logic as WriteCast in
    // FoundryCommands). No substrate query needed — the source material records what each
    // tensor slot was poured from, purely from the foundry's public algebra.

    private static List<TensorProvenance> BuildTensorProvenance(
        LlamaRecipeExtractor.RecipeInfo recipe, List<SourceProvenance> sources)
    {
        var modelSourceIds = sources.Where(s => s.Kind == "ingested-model")
                                    .Select(s => s.SourceId)
                                    .ToList();
        var result = new List<TensorProvenance>();

        // Embedding + LM-head: equivalence plane (cross-model token identity).
        result.Add(MakeTensor("model.embed_tokens.weight", "embed",  null, null, "equivalence", "equivalence", modelSourceIds));
        result.Add(MakeTensor("lm_head.weight",            "lm_head",null, null, "equivalence", "equivalence", modelSourceIds));

        // Per-layer operators. Rank-band assignment mirrors WriteCast exactly (FoundryCommands.cs:873).
        const int RANK_ASSOC = 0, RANK_TAXO = 1, RANK_COMPLETION = 2;
        string[] rankNames = ["associative", "taxonomic", "causal"];
        int nOps = 3, last = recipe.NumLayers - 1;

        for (int l = 0; l < recipe.NumLayers; l++)
        {
            int aIdx = l == last ? RANK_COMPLETION : l == 0 ? RANK_ASSOC : l % nOps;
            int fIdx = l == last ? RANK_COMPLETION : l == 0 ? RANK_TAXO  : (l + 1) % nOps;
            string attnRank = rankNames[aIdx];
            string ffnRank  = rankNames[fIdx];
            string pfx = $"model.layers.{l}.";

            result.Add(MakeTensor(pfx + "self_attn.q_proj.weight", "q",    l, null, "attention", attnRank, modelSourceIds));
            result.Add(MakeTensor(pfx + "self_attn.k_proj.weight", "k",    l, null, "attention", attnRank, modelSourceIds));
            result.Add(MakeTensor(pfx + "self_attn.v_proj.weight", "v",    l, null, "OV",        attnRank, modelSourceIds));
            result.Add(MakeTensor(pfx + "self_attn.o_proj.weight", "o",    l, null, "OV",        attnRank, modelSourceIds));
            result.Add(MakeTensor(pfx + "mlp.gate_proj.weight",    "gate", l, null, "FFN",       ffnRank,  modelSourceIds));
            result.Add(MakeTensor(pfx + "mlp.up_proj.weight",      "up",   l, null, "FFN",       ffnRank,  modelSourceIds));
            result.Add(MakeTensor(pfx + "mlp.down_proj.weight",    "down", l, null, "FFN",       ffnRank,  modelSourceIds));
        }
        return result;
    }

    private static TensorProvenance MakeTensor(
        string name, string role, int? layer, int? expert, string plane, string rankBand,
        IReadOnlyList<string> sources)
        => new()
        {
            TensorName          = name,
            Role                = role,
            Layer               = layer,
            Expert              = expert,
            Plane               = plane,
            RankBand            = rankBand,
            SvdRank             = null,     // populated post-synthesis; not available from recipe alone
            ContributingSources = sources,
        };

    // ── Circuits ──────────────────────────────────────────────────────────────
    // Circuit entity ids are deterministic (same formula as HeadClassifier.CircuitEntityId).
    // We compute them all, batch-query the ENCODES consensus, then render.

    private static async Task<List<CircuitProvenance>> BuildCircuitsAsync(
        NpgsqlDataSource ds, string modelName,
        LlamaRecipeExtractor.RecipeInfo recipe, CancellationToken ct)
    {
        var encodesTypeId = RelationTypeRegistry.RelationTypeId("ENCODES");

        // Enumerate all circuit canonical names + metadata.
        var descs = new List<(string Canonical, int Layer, int? Head, string Plane)>();

        foreach (var plane in new[] { "SIMILAR_TO", "CONTINUES_TO" })
            descs.Add(($"substrate/entity/{modelName}/circuit/embed.{plane}/v1", -1, null, plane));

        for (int l = 0; l < recipe.NumLayers; l++)
        {
            for (int h = 0; h < recipe.NumHeads; h++)
                descs.Add(($"substrate/entity/{modelName}/circuit/L{l}.H{h}.ATTENDS/v1", l, h, "ATTENDS"));
            descs.Add(($"substrate/entity/{modelName}/circuit/L{l}.OV_RELATES/v1",    l, null, "OV_RELATES"));
            descs.Add(($"substrate/entity/{modelName}/circuit/L{l}.COMPLETES_TO/v1",  l, null, "COMPLETES_TO"));
        }

        var ids    = descs.Select(d => Hash128.OfCanonical(d.Canonical)).ToArray();
        var idBytes = ids.Select(h => h.ToBytes()).ToArray();

        // Batch query: find ENCODES consensus for all circuits in one round-trip.
        // subject_id = ANY(circuit_ids) AND type_id = ENCODES.
        // The document command is a one-shot read; a full table scan here is acceptable.
        var encodesMap = new Dictionary<Hash128, (string Relation, double EffMu, long Witnesses)>();
        try
        {
            await using var cmd = ds.CreateCommand(@"
                SELECT c.subject_id, c.object_id,
                       laplace.eff_mu_display(c.rating, c.rd) AS eff_mu,
                       c.witnesses
                FROM laplace.consensus c
                WHERE c.subject_id = ANY($1::bytea[])
                  AND c.type_id = $2");
            cmd.CommandTimeout = 120;
            var p1 = cmd.Parameters.AddWithValue(idBytes);
            p1.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea;
            var p2 = cmd.Parameters.AddWithValue(encodesTypeId.ToBytes());
            p2.NpgsqlDbType = NpgsqlDbType.Bytea;

            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var subj   = Hash128.FromBytes((byte[])rdr[0]);
                var obj    = Hash128.FromBytes((byte[])rdr[1]);
                double emu = rdr.IsDBNull(2) ? 0.0 : (double)rdr.GetDecimal(2);
                long   wit = rdr.IsDBNull(3) ? 0L  : rdr.GetInt64(3);
                string rel = RelationName(obj);
                if (!encodesMap.TryGetValue(subj, out var ex) || emu > ex.EffMu)
                    encodesMap[subj] = (rel, emu, wit);
            }
        }
        catch { }

        var result = new List<CircuitProvenance>(descs.Count);
        for (int i = 0; i < descs.Count; i++)
        {
            encodesMap.TryGetValue(ids[i], out var enc);
            var (canonical, layer, head, plane) = descs[i];
            result.Add(new CircuitProvenance
            {
                CircuitId       = canonical,
                Layer           = layer,
                Head            = head,
                Plane           = plane,
                EncodesRelation = enc.Relation,
                Confidence      = enc.EffMu > 0 ? enc.EffMu : null,
                Witnesses       = enc.Witnesses > 0 ? enc.Witnesses : null,
                Exemplars       = [],  // future: top token pairs from the circuit's consensus plane
            });
        }
        return result;
    }
}
