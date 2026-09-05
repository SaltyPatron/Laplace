using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Owns the complete two-artifact selection for one explicit analysis run.
/// Config, tokenizer, headers and tensors are all consumed through the two
/// held snapshots; disposal happens only after every atomic working set applied.
/// </summary>
public sealed class SelectedModelAnalysisEstate : IDisposable
{
    private SelectedModelAnalysisEstate(
        SelectedModelAnalysisInput left, SelectedModelAnalysisInput right)
    {
        Left = left;
        Right = right;
    }

    public SelectedModelAnalysisInput Left { get; }
    public SelectedModelAnalysisInput Right { get; }

    public static SelectedModelAnalysisEstate Open(
        string leftDirectory, string rightDirectory)
    {
        string left = SafetensorSnapshotWitness.ResolveCompleteDir(leftDirectory)
                      ?? leftDirectory;
        string right = SafetensorSnapshotWitness.ResolveCompleteDir(rightDirectory)
                       ?? rightDirectory;
        SourceEntityIdConventions.ModelContentSnapshot? leftSnapshot = null;
        SourceEntityIdConventions.ModelContentSnapshot? rightSnapshot = null;
        try
        {
            leftSnapshot = SourceEntityIdConventions.OpenModelContentSnapshot(left)
                ?? throw new InvalidDataException($"selected model has no checkpoint weights: {left}");
            rightSnapshot = SourceEntityIdConventions.OpenModelContentSnapshot(right)
                ?? throw new InvalidDataException($"selected model has no checkpoint weights: {right}");
            SelectedModelAnalysisInput leftInput = OpenOne(left, leftSnapshot);
            SelectedModelAnalysisInput rightInput = OpenOne(right, rightSnapshot);
            if (leftInput.SourceId == rightInput.SourceId)
                throw new InvalidDataException(
                    "selected model estate contains the same content identity twice");
            return new(leftInput, rightInput);
        }
        catch
        {
            leftSnapshot?.Dispose();
            rightSnapshot?.Dispose();
            throw;
        }
    }

    public async Task<ModelJointCorroborationResult> AnalyzeAndApplyAsync(
        int commitEpoch,
        ISubstrateReader reader,
        ISubstrateWriter writer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        var etl = new ModelJointCorroborationETL(Left, Right);
        int workingSets = 0;
        long proposed = 0;
        long admitted = 0;
        long attestationsInserted = 0;
        bool anyReplay = false;
        await foreach (ModelCorroborationWorkingSet set in etl
                           .AnalyzeAsync(commitEpoch, reader, ct)
                           .ConfigureAwait(false))
        {
            ApplyResult result = await set.ApplyAsync(writer, ct).ConfigureAwait(false);
            workingSets++;
            proposed += set.ProposedPairs;
            admitted += set.AdmittedPairs;
            attestationsInserted += result.AttestationsInserted;
            anyReplay |= result.JournalReplayHit;
        }
        return new(
            Left.SourceId, Right.SourceId,
            workingSets, proposed, admitted, attestationsInserted,
            anyReplay, etl.PeakNativeResidentBytes, etl.PeakTransientScoreBytes);
    }

    public void Dispose()
    {
        Left.Snapshot.Dispose();
        Right.Snapshot.Dispose();
    }

    private static SelectedModelAnalysisInput OpenOne(
        string directory,
        SourceEntityIdConventions.ModelContentSnapshot snapshot)
    {
        string configPath = Path.Combine(directory, "config.json");
        string tokenizerPath = Path.Combine(
            directory, SafetensorSnapshotWitness.TokenizerFile);
        byte[] configBytes = snapshot.Read(configPath, ReadAllBytes);
        byte[] tokenizerBytes = snapshot.Read(tokenizerPath, ReadAllBytes);
        ModelConfigReader.Result config = ModelConfigReader.Read(configBytes);
        IReadOnlyList<SafetensorsContainerParser.TensorReference> tensors =
            SafetensorsContainerParser.ParseModel(snapshot);
        string modelName = Path.GetFileName(
            directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        ModelManifest manifest = TensorRoleClassifier.Build(tensors, config, modelName);
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens =
            LlamaTokenizerParser.Parse(tokenizerBytes);
        snapshot.VerifySourceId();
        return new(directory, manifest, tokens, snapshot.SourceId, snapshot);
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream.Length > int.MaxValue)
            throw new InvalidDataException("model metadata sidecar exceeds managed parser capacity");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}

public readonly record struct ModelJointCorroborationResult(
    Hash128 LeftSource,
    Hash128 RightSource,
    int WorkingSets,
    long ProposedPairs,
    long AdmittedPairs,
    long AttestationsInserted,
    bool AnyJournalReplay,
    long PeakNativeResidentBytes,
    long PeakTransientScoreBytes);
