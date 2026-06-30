namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Marks a decomposer class as eligible for the native ETL path (tree-sitter row framing +
/// grammar compose in <c>laplace_core</c>). <see cref="NativeGrammarIngest.CanUseNative"/>
/// reflects on this attribute instead of matching hardcoded class name strings.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class UsesNativeIngestAttribute : Attribute
{
    /// <summary>When true, native ingest is opt-in via <c>LAPLACE_INGEST_NATIVE=1</c>.</summary>
    public bool RequiresEnvOpt { get; init; }
}
