namespace Laplace.Decomposers.Model.Architectures;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using Laplace.Core.Abstractions;

/// <summary>
/// Auto-detected tensor layout of an encoder-only (BERT family) safetensors
/// header: number of transformer layers, number of attention heads,
/// hidden dim, intermediate (FFN) dim, plus tensor-name → SafetensorEntry
/// pointers per layer + role. Detection inspects tensor names matching the
/// canonical BERT convention `{prefix}.encoder.layer.{L}.attention.self.{q|k|v}.weight`
/// + `attention.output.dense.weight` + `intermediate.dense.weight` +
/// `output.dense.weight`. Prefix (`bert.` / `roberta.` / empty) auto-detected.
/// </summary>
public sealed class EncoderOnlyTensorLayout
{
    public int NumLayers { get; init; }
    public int NumHeads  { get; init; }
    public int HiddenDim { get; init; }
    public int IntermediateDim { get; init; }
    public int HeadDim => HiddenDim / NumHeads;

    public required IReadOnlyList<EncoderLayerTensors> Layers { get; init; }
    public SafetensorEntry? PoolerWeight { get; init; }

    /// <summary>
    /// Resolve the layout from a HF package: tensor entries from the
    /// safetensors header + the package's config.json path. Reads
    /// num_attention_heads (and validates num_hidden_layers / hidden_size /
    /// intermediate_size if present) from config.json directly — every
    /// architecture parameter the layout needs is in the package's own
    /// config.json. Returns null if config.json or the BERT-shaped tensors
    /// are missing.
    /// </summary>
    public static EncoderOnlyTensorLayout? TryResolveFromConfigJson(
        IReadOnlyList<SafetensorEntry> entries,
        string                         configJsonPath)
    {
        var bytes = File.ReadAllBytes(configJsonPath);
        using var doc = JsonDocument.Parse(bytes);
        if (!doc.RootElement.TryGetProperty("num_attention_heads", out var numHeadsEl)) { return null; }
        return TryResolve(entries, numHeadsEl.GetInt32());
    }

    /// <summary>
    /// Lower-level resolver — used internally and by callers that already
    /// hold a parsed config (e.g., when the package's config.json has
    /// already been substrate-decomposed via JsonAstDecomposer and the
    /// num_attention_heads concept entity is in scope). Production paths
    /// route through <see cref="TryResolveFromConfigJson"/>.
    /// </summary>
    public static EncoderOnlyTensorLayout? TryResolve(IReadOnlyList<SafetensorEntry> entries, int numHeads)
    {
        var byName = new Dictionary<string, SafetensorEntry>(StringComparer.Ordinal);
        foreach (var e in entries) { byName[e.Name] = e; }

        string? prefix = null;
        foreach (var candidate in new[] { "bert.", "roberta.", "" })
        {
            if (byName.ContainsKey($"{candidate}encoder.layer.0.attention.self.query.weight"))
            {
                prefix = candidate;
                break;
            }
        }
        if (prefix is null) { return null; }

        var layers = new List<EncoderLayerTensors>();
        var layerIndex = 0;
        while (true)
        {
            var qName    = $"{prefix}encoder.layer.{layerIndex}.attention.self.query.weight";
            var kName    = $"{prefix}encoder.layer.{layerIndex}.attention.self.key.weight";
            var vName    = $"{prefix}encoder.layer.{layerIndex}.attention.self.value.weight";
            var oName    = $"{prefix}encoder.layer.{layerIndex}.attention.output.dense.weight";
            var upName   = $"{prefix}encoder.layer.{layerIndex}.intermediate.dense.weight";
            var downName = $"{prefix}encoder.layer.{layerIndex}.output.dense.weight";

            if (!byName.TryGetValue(qName,    out var q))    { break; }
            if (!byName.TryGetValue(kName,    out var k))    { break; }
            if (!byName.TryGetValue(vName,    out var v))    { break; }
            if (!byName.TryGetValue(oName,    out var o))    { break; }
            if (!byName.TryGetValue(upName,   out var up))   { break; }
            if (!byName.TryGetValue(downName, out var down)) { break; }

            layers.Add(new EncoderLayerTensors(layerIndex, q, k, v, o, up, down));
            layerIndex++;
        }
        if (layers.Count == 0) { return null; }

        var first = layers[0];
        if (first.WQ.Shape.Length != 2) { return null; }
        var hiddenDim       = (int)first.WQ.Shape[0];
        var intermediateDim = (int)first.WUp.Shape[0];

        if (numHeads <= 0 || hiddenDim % numHeads != 0) { return null; }

        byName.TryGetValue($"{prefix}pooler.dense.weight", out var pooler);

        return new EncoderOnlyTensorLayout
        {
            NumLayers       = layers.Count,
            NumHeads        = numHeads,
            HiddenDim       = hiddenDim,
            IntermediateDim = intermediateDim,
            Layers          = layers,
            PoolerWeight    = pooler,
        };
    }

}

public sealed record EncoderLayerTensors(
    int LayerIndex,
    SafetensorEntry WQ,
    SafetensorEntry WK,
    SafetensorEntry WV,
    SafetensorEntry WO,
    SafetensorEntry WUp,
    SafetensorEntry WDown);
