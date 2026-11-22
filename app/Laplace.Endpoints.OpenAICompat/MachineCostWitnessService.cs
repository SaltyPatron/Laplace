using System.Text.Json;
using System.Text.Json.Serialization;
using Laplace.Api.Contracts;
using Laplace.Engine.Core;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Records a target-machine analysis as what the runtime witness <c>MachineCostAnalyzer</c>
/// measured at <c>AppDerived</c>: one canonical JSON artifact read by the C Engine's
/// <c>laplace witness</c> under its runtime trunk.
/// </summary>
internal sealed class MachineCostWitnessService
{
    private const string AnalyzerName = "MachineCostAnalyzer";
    private const string TrustClass = "AppDerived";
    private const string CalculationSchema = "laplace.machine-cost-calculation/v1";
    private const string WitnessSchema = "laplace.machine-cost-witness/v1";

    private static readonly JsonSerializerOptions CanonicalJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    internal sealed record Deposit(string CalculationId, string WitnessId);

    private sealed record CalculationManifest(
        string Schema,
        string Analyzer,
        string AnalyzerVersion,
        string ArtifactSha256,
        string? Symbol,
        string ObjectFormat,
        string TargetTriple,
        string Cpu,
        double ClockHz,
        int Iterations,
        string Scope,
        bool ControlFlowWeighted,
        string ObjdumpVersion,
        string McaVersion);

    private sealed record CostWitness(
        string Schema,
        string ArtifactSha256,
        string? Symbol,
        string ObjectFormat,
        string TargetTriple,
        string Cpu,
        double ClockHz,
        int Iterations,
        long StaticInstructionCount,
        long ScheduledInstructionInstances,
        long TotalCycles,
        long? TotalUops,
        int? DispatchWidth,
        double? UopsPerCycle,
        double? Ipc,
        double? BlockRThroughputCycles,
        double CalculatedSeconds,
        double CalculatedNanoseconds,
        string Scope,
        bool ControlFlowWeighted,
        IReadOnlyList<MachineCostResourcePressure> ResourcePressure,
        string ObjdumpVersion,
        string McaVersion,
        IReadOnlyList<string> Assumptions);

    public async Task<Deposit> RecordAsync(MachineCostResponse receipt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        var calculation = new CalculationManifest(
            CalculationSchema,
            AnalyzerName,
            "1",
            receipt.ArtifactSha256,
            receipt.Symbol,
            receipt.ObjectFormat,
            receipt.TargetTriple,
            receipt.Cpu,
            receipt.ClockHz,
            receipt.Iterations,
            receipt.Scope,
            receipt.ControlFlowWeighted,
            receipt.ObjdumpVersion,
            receipt.McaVersion);

        var witness = new CostWitness(
            WitnessSchema,
            receipt.ArtifactSha256,
            receipt.Symbol,
            receipt.ObjectFormat,
            receipt.TargetTriple,
            receipt.Cpu,
            receipt.ClockHz,
            receipt.Iterations,
            receipt.StaticInstructionCount,
            receipt.ScheduledInstructionInstances,
            receipt.TotalCycles,
            receipt.TotalUops,
            receipt.DispatchWidth,
            receipt.UopsPerCycle,
            receipt.Ipc,
            receipt.BlockRThroughputCycles,
            receipt.CalculatedSeconds,
            receipt.CalculatedNanoseconds,
            receipt.Scope,
            receipt.ControlFlowWeighted,
            receipt.ResourcePressure,
            receipt.ObjdumpVersion,
            receipt.McaVersion,
            receipt.Assumptions);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { calculation, result = witness }, CanonicalJson);
        string calculationId = Hash128.Blake3(json).ToHex();
        string witnessId = Hash128.Text(AnalyzerName).ToHex();

        var workDir = Path.Combine(Path.GetTempPath(), $"laplace-mc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var artifactPath = Path.Combine(workDir, "machine-cost.json");
            await File.WriteAllBytesAsync(artifactPath, json, ct).ConfigureAwait(false);
            await NativeEngineCli.RunWitnessAsync(
                AnalyzerName, TrustClass, workDir, [artifactPath], AnalyzerName, ct).ConfigureAwait(false);
            return new Deposit(calculationId, witnessId);
        }
        catch (InvalidOperationException ex)
        {
            throw new SubstrateUnavailableException(
                "Machine-cost calculation succeeded, but the Engine did not record it.", ex);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
        }
    }
}
