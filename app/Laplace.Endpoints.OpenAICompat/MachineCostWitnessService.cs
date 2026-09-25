using System.Text.Json;
using System.Text.Json.Serialization;
using Laplace.Api.Contracts;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Deposits deterministic target-machine analysis as calculated testimony.  The
/// calculation identity is the exact analyzer/toolchain + artifact + target +
/// execution-count manifest; the result is a separately content-addressed witness.
/// </summary>
internal sealed class MachineCostWitnessService(SubstrateClient substrate)
{
    private const string AnalyzerName = "MachineCostAnalyzer";
    private const string CalculationSchema = "laplace.machine-cost-calculation/v1";
    private const string ArtifactSchema = "laplace.machine-artifact-descriptor/v1";
    private const string TargetSchema = "laplace.machine-target/v1";
    private const string WitnessSchema = "laplace.machine-cost-witness/v1";

    private static readonly Hash128 Source = SubstrateCanonicalIds.Source(AnalyzerName);
    private static readonly Hash128 TrustClass = TrustClassRegistry.Id("AppDerived");
    private static readonly double Trust = SourceTrust.AppDerived;

    private static readonly JsonSerializerOptions CanonicalJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    internal sealed record Deposit(string CalculationId, string WitnessId);

    private sealed record ArtifactDescriptor(
        string Schema,
        string Sha256,
        long Bytes,
        string ObjectFormat);

    private sealed record MachineTarget(
        string Schema,
        string TargetTriple,
        string Cpu,
        double ClockHz,
        string ObjdumpVersion,
        string McaVersion);

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

        try
        {
            await using var writer = new ConsensusAccumulatingWriter(
                new NpgsqlSubstrateWriter(substrate.DataSource), substrate.DataSource);

            SubstrateChange bootstrap = new BootstrapIntentBuilder(
                Source, AnalyzerName, TrustClass).Build();
            await writer.ApplyAsync(bootstrap, ct).ConfigureAwait(false);

            var artifact = new ArtifactDescriptor(
                ArtifactSchema,
                receipt.ArtifactSha256,
                receipt.ArtifactBytes,
                receipt.ObjectFormat);

            var target = new MachineTarget(
                TargetSchema,
                receipt.TargetTriple,
                receipt.Cpu,
                receipt.ClockHz,
                receipt.ObjdumpVersion,
                receipt.McaVersion);

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

            byte[] artifactJson = JsonSerializer.SerializeToUtf8Bytes(artifact, CanonicalJson);
            byte[] targetJson = JsonSerializer.SerializeToUtf8Bytes(target, CanonicalJson);
            byte[] calculationJson = JsonSerializer.SerializeToUtf8Bytes(calculation, CanonicalJson);
            byte[] witnessJson = JsonSerializer.SerializeToUtf8Bytes(witness, CanonicalJson);

            using var change = new SubstrateChangeBuilder(
                Source,
                $"machine-cost/{receipt.ArtifactSha256}/{Guid.NewGuid():N}")
                .DeclareSourcePrior(Source, Trust);

            Hash128 artifactRoot = ContentEmitter.Emit(change, artifactJson, Source)
                ?? throw new InvalidOperationException("Machine artifact descriptor did not compose into substrate content.");
            Hash128 targetRoot = ContentEmitter.Emit(change, targetJson, Source)
                ?? throw new InvalidOperationException("Machine target descriptor did not compose into substrate content.");
            Hash128 calculationRoot = ContentEmitter.Emit(change, calculationJson, Source)
                ?? throw new InvalidOperationException("Machine cost calculation manifest did not compose into substrate content.");
            Hash128 witnessRoot = ContentEmitter.Emit(change, witnessJson, Source)
                ?? throw new InvalidOperationException("Machine cost witness did not compose into substrate content.");

            change.AddAttestation(NativeAttestation.Categorical(
                calculationRoot, "HAS_INPUT", artifactRoot,
                Source, (Hash128?)null, Trust, confirm: true));
            change.AddAttestation(NativeAttestation.Categorical(
                calculationRoot, "HAS_INPUT", targetRoot,
                Source, (Hash128?)null, Trust, confirm: true));
            change.AddAttestation(NativeAttestation.Categorical(
                calculationRoot, "HAS_RESULT", witnessRoot,
                Source, (Hash128?)null, Trust, confirm: true));

            await writer.ApplyAsync(change.Build(), ct).ConfigureAwait(false);
            return new Deposit(calculationRoot.ToString(), witnessRoot.ToString());
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException(
                "Machine-cost calculation succeeded, but its calculated testimony could not be persisted.",
                ex);
        }
    }
}
