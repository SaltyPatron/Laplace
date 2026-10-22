using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.ISO;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using System.Reflection;
using Xunit;

namespace Laplace.Decomposers.Tests;

/// <summary>
/// A deposited fact must be able to be false: no attestation whose object is its subject,
/// and no gloss cut into fragments that compete with the whole for one consensus cell.
/// </summary>
public sealed class VacuousFactGateTests
{
    // ---- ISO 639-3: a name is not a definition ------------------------------------------

    /// <summary>
    /// ISO 639-3 publishes codes and names, no glosses, so a language's name is HAS_NAME and
    /// HAS_DEFINITION is neither declared nor emitted. The source files are read as text
    /// because touching <c>ISOSource.Relations</c> runs a static initializer that loads
    /// laplace_core, which is not beside the test binary.
    /// </summary>
    [Fact]
    public void IsoSource_NoLongerDeclares_HasDefinition()
    {
        // Directory.Build.props stamps the source checkout into every assembly.
        // An external build root also has an app/ directory, but not the source.
        var root = typeof(VacuousFactGateTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "LaplaceRepoRoot").Value;
        Assert.NotNull(root);

        var src = File.ReadAllText(Path.Combine(
            root!, "app", "Laplace.Decomposers", "ISO", "ISOSource.cs"));
        var relations = src[(src.IndexOf("Relations", StringComparison.Ordinal))..];
        relations = relations[..relations.IndexOf("];", StringComparison.Ordinal)];

        Assert.DoesNotContain("\"HAS_DEFINITION\"", relations);
        Assert.Contains("\"HAS_NAME\"", relations);

        var dec = File.ReadAllText(Path.Combine(
            root!, "app", "Laplace.Decomposers", "ISO", "ISODecomposer.cs"));
        Assert.DoesNotContain("\"HAS_DEFINITION\"", dec);
    }
}
