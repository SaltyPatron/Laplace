using Laplace.Decomposers.Abstractions;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Ingestion;

/// <summary>
/// Explicit feedback: a participant confirms or refutes a claim, [subject, relation, object] (or [subject,
/// relation] with no object), each part the text it is and the relation the participant's own word for it. The
/// participant is a runtime witness at UserPromptContent (Wiki Sequence 22.6, Conflicts C1): the verdict is its
/// statement, 1 for confirm and 0 for refute, recorded by the Engine (<see cref="RuntimeWitnessRecorder"/>), its
/// standing folded in the database. The same verdict stated again by the same participant is the same statement
/// and moves nothing. HTTP /v1/feedback, MCP feedback and the CLI attest are this one lane.
/// </summary>
public static class FeedbackStatements
{
    /// <summary>The trust class a participant's explicit feedback states at (Laplace-Native trust_classes.toml).</summary>
    public const string TrustClass = "UserPromptContent";

    public sealed record Standing(double Rating, double Deviation, long Matches, double Confidence);

    public sealed record Result(string? WitnessId, string? ClaimId, Standing? Before, Standing? After);

    public static async Task<Result> StateAsync(RuntimeWitnessRecorder recorder, NpgsqlDataSource reads,
        string participant, string subject, string relation, string obj, bool confirm, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(participant);
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ArgumentException.ThrowIfNullOrEmpty(relation);
        obj ??= "";
        var before = await NpgsqlClaimStanding.ReadAsync(reads, subject, relation, obj, ct).ConfigureAwait(false);
        var receipt = await recorder.StateAsync(new NativeRuntimeWitness(participant, TrustClass),
            [new WitnessStatement(subject, relation, obj, confirm ? 1.0 : 0.0)], ct).ConfigureAwait(false);
        var after = await NpgsqlClaimStanding.ReadAsync(reads, subject, relation, obj, ct).ConfigureAwait(false);
        return new Result(receipt.WitnessId, (after ?? before)?.Claim.ToHex(), Of(before), Of(after));
    }

    private static Standing? Of(NpgsqlClaimStanding.Row? row)
        => row is { } r ? new Standing(r.Rating, r.Deviation, r.Matches, r.Confidence) : null;
}
