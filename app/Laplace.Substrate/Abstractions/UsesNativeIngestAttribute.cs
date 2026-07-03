namespace Laplace.Decomposers.Abstractions;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class UsesNativeIngestAttribute : Attribute
{
    public bool RequiresEnvOpt { get; init; }
}
