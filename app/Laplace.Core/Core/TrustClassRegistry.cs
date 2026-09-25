namespace Laplace.Engine.Core;

/// <summary>
/// Governed witness trust classes (engine/manifest/trust_classes.toml), resolved through
/// the native trust-class law. A class's id is the content id of its label; its prior
/// seeds the standing of every claim a witness of that class makes. An undeclared class
/// is a defect at its call site, never a default prior.
/// </summary>
public static class TrustClassRegistry
{
    public static Hash128 Id(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        unsafe
        {
            Hash128 id;
            if (NativeInterop.TrustClassIdNative(name, &id) != 0)
                throw new ArgumentException(
                    $"trust class '{name}' is not declared in engine/manifest/trust_classes.toml",
                    nameof(name));
            return id;
        }
    }

    public static double Prior(Hash128 trustClassId)
    {
        unsafe
        {
            double prior;
            if (NativeInterop.TrustClassPriorNative(&trustClassId, &prior) != 0)
                throw new InvalidOperationException(
                    $"No governed witness prior is registered for trust class {trustClassId}.");
            return prior;
        }
    }

    public static double Prior(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        unsafe
        {
            double prior;
            if (NativeInterop.TrustClassPriorByNameNative(name, &prior) != 0)
                throw new InvalidOperationException(
                    $"trust class '{name}' is not declared in engine/manifest/trust_classes.toml");
            return prior;
        }
    }
}
