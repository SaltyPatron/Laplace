namespace Laplace.Decomposers.Model.Architectures;

using System;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Decomposers.Model.Extractors;
using Laplace.Decomposers.Model.Mechanistic;

/// <summary>
/// First of #37's eleven architecture-family decomposers — encoder-only
/// transformer (BERT / RoBERTa / MiniLM / DistilBERT / ALBERT-style models
/// that emit hidden-state representations for downstream tasks). Drives the
/// three per-tensor extractors (Attention / FFN / LmHead) over the BERT-
/// convention tensor names auto-detected by EncoderOnlyTensorLayout:
///
///   - Per layer L, per head H: emit W_Q^H / W_K^H / W_V^H (row slices) and
///     W_O^H (column slice) as LINESTRING4D operator shapes via
///     AttentionEdgeExtractor.
///   - Per layer L, per FFN neuron N: emit W_up_row[N] and W_down_col[N]
///     as POINT4D operator shapes via FfnKeyValueExtractor.
///
/// LmHeadExtractor is NOT invoked from the encoder path — encoder-only
/// models typically don't ship an LM head; classification / sentence-
/// embedding heads are handled by their own extractors when the
/// architecture-family decomposer for those models lands.
///
/// Discrete-edge emission (the activation-driven half of each extractor)
/// requires real-corpus activation observations and lands when
/// IRealCorpusActivationObserver wires up; this decomposer handles the
/// geometric operator-shape half end-to-end against the safetensors
/// header + tensor data.
///
/// Phase 4 / Track F5 / G5.
/// </summary>
public sealed class EncoderOnlyDecomposer
{
    private readonly MechanisticHeadEntityResolver _heads;
    private readonly AttentionEdgeExtractor        _attention;
    private readonly FfnKeyValueExtractor          _ffn;

    public EncoderOnlyDecomposer(
        MechanisticHeadEntityResolver heads,
        AttentionEdgeExtractor        attention,
        FfnKeyValueExtractor          ffn)
    {
        _heads     = heads     ?? throw new ArgumentNullException(nameof(heads));
        _attention = attention ?? throw new ArgumentNullException(nameof(attention));
        _ffn       = ffn       ?? throw new ArgumentNullException(nameof(ffn));
    }

    /// <summary>
    /// Stream all transformer layer tensors through the per-tensor
    /// extractors. Returns counts useful for verification (per-model golden
    /// delta records intersect these counts).
    /// </summary>
    public async Task<DecomposeResult> DecomposeAsync(
        AtomId                    modelEntity,
        string                    modelSourceCanonicalName,
        ISafetensorsHandle         handle,
        EncoderOnlyTensorLayout   layout,
        CancellationToken         cancellationToken)
    {
        var result        = new DecomposeResult();
        var hiddenDim     = layout.HiddenDim;
        var headDim       = layout.HeadDim;
        var numHeads      = layout.NumHeads;
        var intermediate  = layout.IntermediateDim;

        foreach (var layer in layout.Layers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var wq = ReadFloat64(handle, layer.WQ, (long)hiddenDim * hiddenDim);
            var wk = ReadFloat64(handle, layer.WK, (long)hiddenDim * hiddenDim);
            var wv = ReadFloat64(handle, layer.WV, (long)hiddenDim * hiddenDim);
            var wo = ReadFloat64(handle, layer.WO, (long)hiddenDim * hiddenDim);

            for (var h = 0; h < numHeads; h++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var headEntity = await _heads.ResolveHeadAsync(
                    modelEntity, layer.LayerIndex, h, cancellationToken).ConfigureAwait(false);

                // Q/K/V: per-head = row slice [h*head_dim .. (h+1)*head_dim] → shape [head_dim, hidden_dim]
                var wqH = SliceRows(wq, hiddenDim, hiddenDim, h * headDim, headDim);
                var wkH = SliceRows(wk, hiddenDim, hiddenDim, h * headDim, headDim);
                var wvH = SliceRows(wv, hiddenDim, hiddenDim, h * headDim, headDim);

                // W_O: per-head = COL slice [h*head_dim .. (h+1)*head_dim] → shape [hidden_dim, head_dim]
                var woH = SliceCols(wo, hiddenDim, hiddenDim, h * headDim, headDim);

                await _attention.EmitOperatorShapeAsync(
                    modelEntity, modelSourceCanonicalName,
                    AttentionMatrixKind.Query, headEntity,
                    wqH.AsMemory(), headDim, hiddenDim, cancellationToken).ConfigureAwait(false);

                await _attention.EmitOperatorShapeAsync(
                    modelEntity, modelSourceCanonicalName,
                    AttentionMatrixKind.Key, headEntity,
                    wkH.AsMemory(), headDim, hiddenDim, cancellationToken).ConfigureAwait(false);

                await _attention.EmitOperatorShapeAsync(
                    modelEntity, modelSourceCanonicalName,
                    AttentionMatrixKind.Value, headEntity,
                    wvH.AsMemory(), headDim, hiddenDim, cancellationToken).ConfigureAwait(false);

                await _attention.EmitOperatorShapeAsync(
                    modelEntity, modelSourceCanonicalName,
                    AttentionMatrixKind.Output, headEntity,
                    woH.AsMemory(), hiddenDim, headDim, cancellationToken).ConfigureAwait(false);

                result.AttentionOperatorShapesEmitted += 4;
            }

            // FFN W_up [intermediate × hidden] — per-neuron row vector of hidden_dim
            var wUp = ReadFloat64(handle, layer.WUp, (long)intermediate * hiddenDim);
            for (var n = 0; n < intermediate; n++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var neuronEntity = await _heads.ResolveNeuronAsync(
                    modelEntity, layer.LayerIndex, n, cancellationToken).ConfigureAwait(false);
                var rowVec = ExtractRow(wUp, intermediate, hiddenDim, n);
                await _ffn.EmitNeuronOperatorShapeAsync(
                    modelEntity, modelSourceCanonicalName,
                    FfnNeuronRoleKind.UpKey, neuronEntity,
                    rowVec.AsMemory(), cancellationToken).ConfigureAwait(false);
                result.FfnNeuronOperatorShapesEmitted++;
            }

            // FFN W_down [hidden × intermediate] — per-neuron col vector of hidden_dim
            var wDown = ReadFloat64(handle, layer.WDown, (long)hiddenDim * intermediate);
            for (var n = 0; n < intermediate; n++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var neuronEntity = await _heads.ResolveNeuronAsync(
                    modelEntity, layer.LayerIndex, n, cancellationToken).ConfigureAwait(false);
                var colVec = ExtractCol(wDown, hiddenDim, intermediate, n);
                await _ffn.EmitNeuronOperatorShapeAsync(
                    modelEntity, modelSourceCanonicalName,
                    FfnNeuronRoleKind.DownValue, neuronEntity,
                    colVec.AsMemory(), cancellationToken).ConfigureAwait(false);
                result.FfnNeuronOperatorShapesEmitted++;
            }

            result.LayersProcessed++;
        }

        return result;
    }

    private static double[] ReadFloat64(ISafetensorsHandle handle, SafetensorEntry entry, long elementCount)
    {
        var buf = new double[elementCount];
        handle.ReadFloat64(entry, buf);
        return buf;
    }

    /// <summary>
    /// Slice rows [startRow, startRow+rowCount) of a row-major
    /// [totalRows × cols] matrix. Returns a freshly allocated [rowCount × cols].
    /// </summary>
    private static double[] SliceRows(double[] matrix, int totalRows, int cols, int startRow, int rowCount)
    {
        _ = totalRows;
        var result = new double[(long)rowCount * cols];
        Array.Copy(matrix, (long)startRow * cols, result, 0L, (long)rowCount * cols);
        return result;
    }

    /// <summary>
    /// Slice columns [startCol, startCol+colCount) of a row-major
    /// [rows × totalCols] matrix. Returns a freshly allocated [rows × colCount].
    /// </summary>
    private static double[] SliceCols(double[] matrix, int rows, int totalCols, int startCol, int colCount)
    {
        var result = new double[(long)rows * colCount];
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < colCount; c++)
            {
                result[(long)r * colCount + c] = matrix[(long)r * totalCols + startCol + c];
            }
        }
        return result;
    }

    private static double[] ExtractRow(double[] matrix, int totalRows, int cols, int rowIdx)
    {
        _ = totalRows;
        var result = new double[cols];
        Array.Copy(matrix, (long)rowIdx * cols, result, 0L, cols);
        return result;
    }

    private static double[] ExtractCol(double[] matrix, int rows, int totalCols, int colIdx)
    {
        var result = new double[rows];
        for (var r = 0; r < rows; r++)
        {
            result[r] = matrix[(long)r * totalCols + colIdx];
        }
        return result;
    }

    public sealed class DecomposeResult
    {
        public int LayersProcessed                  { get; set; }
        public int AttentionOperatorShapesEmitted   { get; set; }
        public int FfnNeuronOperatorShapesEmitted   { get; set; }
    }
}
