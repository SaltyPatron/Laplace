using System.Text.Json;
using Xunit;
using Laplace.Decomposers.Model;

namespace Laplace.Decomposers.Model.Tests;

/// <summary>
/// Proves the GENERIC, shape-driven structure detection works across architectures with NO
/// per-family code: the same <see cref="ModelGeometry"/> detects the right circuits for
/// TinyLlama (Llama, GQA: q=[2048,2048], k/v=[256,2048], o_proj, up/gate/down) AND Phi-2
/// (MHA, sharded: o is "dense", MLP is "fc1"/"fc2", no gate). Reads the models' real
/// safetensors headers (single-file or sharded) + the generic HF config dims — no SHAs,
/// no family branches.
/// </summary>
public class ModelGeometryTests
{
    private static string? Snapshot(string repo)
    {
        var snaps = Path.Combine(repo, "snapshots");
        if (Directory.Exists(snaps))
            return Directory.GetDirectories(snaps).OrderBy(d => d).FirstOrDefault();
        return Directory.Exists(repo) ? repo : null;
    }

    private static IEnumerable<(string name, int[] shape)> Headers(string dir)
    {
        foreach (var st in Directory.GetFiles(dir, "*.safetensors").OrderBy(x => x))
            foreach (var r in SafetensorsContainerParser.ParseHeader(st))
                yield return (r.Name, r.Shape);
    }

    private static (int vocab, int d, int L, int nH, int nKv, int hd, int interm) Dims(string dir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "config.json")));
        var r = doc.RootElement;
        int Get(string k, int def = 0) =>
            r.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : def;
        int vocab = Get("vocab_size"), d = Get("hidden_size"), L = Get("num_hidden_layers");
        int nH = Get("num_attention_heads"); int nKv = Get("num_key_value_heads", nH);
        int interm = Get("intermediate_size");
        int hd = r.TryGetProperty("head_dim", out var hp) && hp.ValueKind == JsonValueKind.Number
            ? hp.GetInt32() : (nH > 0 ? d / nH : 0);
        return (vocab, d, L, nH, nKv, hd, interm);
    }

    [Theory]
    // repo, OV decode name, FFN encode name, FFN decode name — the architecture-specific
    // tensor names the GENERIC detector must resolve purely from shape + universal role tokens.
    [InlineData("/vault/models/models--TinyLlama--TinyLlama-1.1B-Chat-v1.0", "o_proj", "up_proj", "down_proj")]
    [InlineData("/vault/models/models--microsoft--phi-2",                    "dense",  "fc1",     "fc2")]
    public void Detect_FindsAllCircuits_GenericAcrossArchitectures(
        string repo, string ovDecodeToken, string ffnEncToken, string ffnDecToken)
    {
        var dir = Snapshot(repo);
        Assert.True(dir is not null, $"model not present (cross-model proof needs it): {repo}");

        var (vocab, d, L, nH, nKv, hd, interm) = Dims(dir!);
        var geo = ModelGeometry.Detect(vocab, d, nH, nKv, hd, interm, L, Headers(dir!));

        // Embedding detected by shape (the [vocab, ·] tensor), no name assumption.
        Assert.NotNull(geo.EmbeddingName);
        Assert.Contains("embed", geo.EmbeddingName!);

        // Exactly QK + OV + FFN per layer — detected for every layer of either architecture.
        Assert.Equal(L, geo.Circuits.Count(c => c.Kind == ModelGeometry.CircuitKind.QK));
        Assert.Equal(L, geo.Circuits.Count(c => c.Kind == ModelGeometry.CircuitKind.OV));
        Assert.Equal(L, geo.Circuits.Count(c => c.Kind == ModelGeometry.CircuitKind.FFN));

        // Layer 0 circuits resolved to the architecture's actual tensor names — from shape,
        // not a family table. QK pairs q with k; OV pairs v with the output (o_proj | dense);
        // FFN pairs the up/encode with the down/decode (up_proj/down_proj | fc1/fc2).
        var l0 = geo.Circuits.Where(c => c.Layer == 0).ToList();
        var qk  = l0.Single(c => c.Kind == ModelGeometry.CircuitKind.QK);
        var ov  = l0.Single(c => c.Kind == ModelGeometry.CircuitKind.OV);
        var ffn = l0.Single(c => c.Kind == ModelGeometry.CircuitKind.FFN);

        Assert.Contains("q_proj", qk.Encode);
        Assert.Contains("k_proj", qk.Decode);
        Assert.Contains("v_proj", ov.Encode);
        Assert.Contains(ovDecodeToken, ov.Decode);
        Assert.Contains(ffnEncToken, ffn.Encode);
        Assert.Contains(ffnDecToken, ffn.Decode);
    }
}
