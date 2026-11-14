using System.Text.Json;
using System.Text.Json.Serialization;
using Laplace.Api.Contracts;
using Laplace.Decomposers.Abstractions;
using Laplace.Ingestion;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Records a target-machine analysis as what the runtime witness MachineCostAnalyzer measured, at AppDerived (an
/// application's evidence from its own operation: measured cost): one JSON file holding the calculation, its
/// analyzer, artifact, target and execution count, and its result, read by the Engine's json recipe under the
/// witness's trunk (laplace witness). The calculation is that file's trunk; the same calculation of the same
/// artifact on the same target records nothing again. Nothing here composes an identity or writes a row.
/// </summary>
internal sealed class MachineCostWitnessService
{
    private const string AnalyzerName = "MachineCostAnalyzer";
    private const string TrustClass = "AppDerived";
    private const string CalculationSchema = "laplace.machine-cost-calculation/v1";
    private const string WitnessSchema = "laplace.machine-cost-witness/v1";
    private readonly Lazy<RuntimeWitnessRecorder> _recorder = new(RuntimeWitnessRecorder.Installed);
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
        try
        {
            var recorded = await _recorder.Value.RecordAsync(
                new NativeRuntimeWitness(AnalyzerName, TrustClass), "machine-cost.json", json, ct).ConfigureAwait(false);
            if (recorded.WitnessId is null)
                throw new SubstrateUnavailableException(
                    "Machine-cost calculation succeeded, but the Engine reported no witness trunk for its record.",
                    new InvalidOperationException(recorded.Path));
            return new Deposit(recorded.FileId ?? recorded.WitnessId, recorded.WitnessId);
        }
        catch (InvalidOperationException ex)
        {
            throw new SubstrateUnavailableException(
                "Machine-cost calculation succeeded, but the Engine did not record it.", ex);
        }
    }
}
