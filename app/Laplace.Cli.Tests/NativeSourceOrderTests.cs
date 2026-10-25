using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Cli.Tests;

public sealed class NativeSourceOrderTests
{
    [Fact]
    public void ReadsCommentsCrLfAndDeclaredDependenciesWithoutAnotherOrderList()
    {
        using var estate = new Estate();
        estate.Source("base", "name base\r\n");
        estate.Source("dependent", "name dependent\r\nafter base # required first\r\n");
        estate.Order("# canonical order\r\nbase\r\n\r\ndependent # last\r\n");
        Assert.Equal(new[] { "base", "dependent" }, EtlManifest.ReadSourceOrder(estate.Root));
    }

    [Theory]
    [InlineData("base\ndependent\n", "after absent\n")]
    [InlineData("base\ndependent\n", "after dependent\n")]
    [InlineData("dependent\nbase\n", "after base\n")]
    [InlineData("base\nbase\n", "")]
    [InlineData("# no sources\n", "")]
    public void RejectsMissingForwardSelfOrDuplicateDependencies(string order, string dependency)
    {
        using var estate = new Estate();
        estate.Source("base", "name base\n");
        estate.Source("dependent", "name dependent\n" + dependency);
        estate.Order(order);
        Assert.Throws<InvalidDataException>(() => EtlManifest.ReadSourceOrder(estate.Root));
    }

    [Fact]
    public void CatalogIncludesUnscheduledSourcesAndResolvesDeclaredAliases()
    {
        using var estate = new Estate();
        estate.Source("base", "name base\naliases b\nroot $LAPLACE_DATA/Base\n");
        estate.Source("dependent", "name dependent\nafter base\n");
        estate.Order("base\n");
        var catalog = NativeIngestCatalog.Read(estate.Root);
        Assert.Equal(new[] { "base", "dependent" }, catalog.Sources.Select(source => source.Name));
        Assert.Equal("base", catalog.Resolve("B"));
        Assert.Equal("$LAPLACE_DATA/Base", Assert.Single(catalog.Sources[0].Roots));
        estate.Source("dependent", "name dependent\naliases b\nafter base\n");
        Assert.Throws<InvalidDataException>(() => NativeIngestCatalog.Read(estate.Root));
    }

    private sealed class Estate : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("laplace-source-order-").FullName;
        public void Source(string name, string declaration)
        {
            string directory = Path.Combine(Root, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "source"), declaration);
        }
        public void Order(string order) => File.WriteAllText(Path.Combine(Root, "order"), order);
        public void Dispose()
        {
            string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Directory.GetParent(Root)!.FullName, expectedParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Source-order test directory is outside its temporary parent.");
            Directory.Delete(Root, recursive: true);
        }
    }
}
