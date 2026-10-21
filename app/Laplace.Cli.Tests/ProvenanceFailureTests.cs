using Laplace.Cli.Provenance;
using Laplace.Engine.Core;
using Npgsql;
using Xunit;

namespace Laplace.Cli.Tests;

public sealed class ProvenanceFailureTests
{
    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"model_type\":42}")]
    public void CorruptRecipeCannotBecomeCompletedProvenance(string json)
    {
        var recipe = Hash128.OfCanonical("provenance/recipe-fixture");
        var failure = Assert.Throws<InvalidDataException>(() =>
            ProvenanceExtractor.RecipeModelLabel(json, "fallback", recipe));
        Assert.NotNull(failure.InnerException);
        Assert.Contains("provenance metadata", failure.Message);
    }

    [Theory]
    [InlineData("{}", "fallback")]
    [InlineData("{\"model_type\":\"llama\"}", "llama")]
    public void ValidRecipeMetadataRetainsItsLabelContract(string json, string expected)
        => Assert.Equal(expected, ProvenanceExtractor.RecipeModelLabel(
            json, "fallback", Hash128.OfCanonical("provenance/recipe-fixture")));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceReadFailureCannotBecomeAnEmptyProvenanceList(bool cancelled)
    {
        await using var data = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Username=provenance_test;Database=provenance_test;Timeout=1;Pooling=false;SSL Mode=Disable");
        using var lifetime = new CancellationTokenSource();
        if (cancelled) lifetime.Cancel();
        var failure = await Record.ExceptionAsync(() =>
            ProvenanceExtractor.BuildSourcesAsync(data, "fixture", lifetime.Token));
        Assert.NotNull(failure);
        if (cancelled) Assert.IsAssignableFrom<OperationCanceledException>(failure);
        else Assert.IsAssignableFrom<NpgsqlException>(failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceCountFailureCannotBecomeZeroSources(bool cancelled)
    {
        await using var data = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Username=provenance_test;Database=provenance_test;Timeout=1;Pooling=false;SSL Mode=Disable");
        using var lifetime = new CancellationTokenSource();
        if (cancelled) lifetime.Cancel();
        var failure = await Record.ExceptionAsync(() => ProvenanceExtractor.CountByTypesAsync(
            data, [Hash128.OfCanonical("provenance/count-fixture")], lifetime.Token));
        Assert.NotNull(failure);
        if (cancelled) Assert.IsAssignableFrom<OperationCanceledException>(failure);
        else Assert.IsAssignableFrom<NpgsqlException>(failure);
    }
}
