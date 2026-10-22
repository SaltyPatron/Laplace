using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class OpeningClassifierTests : IDisposable
{
    public OpeningClassifierTests() => OpeningClassifier.ResetCache();

    public void Dispose() => OpeningClassifier.ResetCache();

    [Fact]
    public void UciPrefix_NamesTheCatalogLine()
    {
        OpeningClassifier.SetLinesForTest(
        [
            ("D00", "Queen's Pawn Game", ["d4", "d5"]),
            ("D30", "Queen's Gambit Declined", ["d4", "d5", "c4", "e6"]),
        ]);

        var match = OpeningClassifier.Classify(["d4", "d5", "c4", "e6", "Nf3"]);
        Assert.Equal("D30", match.Eco);
        Assert.Equal("Queen's Gambit Declined", match.Name);
        Assert.Equal("Queen's Gambit Declined", match.Family);
        Assert.Null(match.Variation);
    }

    [Fact]
    public void Transposition_UsesTheDeeperPositionRatherThanTheShorterPrefix()
    {
        OpeningClassifier.SetLinesForTest(
        [
            ("D00", "Queen's Pawn Game", ["d4", "d5"]),
            ("D30", "Queen's Gambit Declined: Semi-Slav", ["d4", "d5", "c4", "c6", "Nf3", "Nf6"]),
        ]);

        var match = OpeningClassifier.Classify(["c4", "c6", "d4", "d5", "Nf3", "Nf6"]);
        Assert.Equal("D30", match.Eco);
        Assert.Equal("Queen's Gambit Declined: Semi-Slav", match.Name);
        Assert.Equal("Queen's Gambit Declined", match.Family);
        Assert.Equal("Semi-Slav", match.Variation);
    }

    [Fact]
    public void ExactMoveOrder_WinsWhenItIsAsSpecificAsTheSharedPosition()
    {
        OpeningClassifier.SetLinesForTest(
        [
            ("D30", "Queen's Gambit Declined", ["d4", "d5", "c4", "e6"]),
            ("A13", "English Opening: Agincourt Defense", ["c4", "e6", "d4", "d5"]),
        ]);

        var english = OpeningClassifier.Classify(["c4", "e6", "d4", "d5"]);
        Assert.Equal("A13", english.Eco);
        var queens = OpeningClassifier.Classify(["d4", "d5", "c4", "e6"]);
        Assert.Equal("D30", queens.Eco);
    }

    [Fact]
    public void UnknownLine_IsNotNamedFromAHardcodedList()
    {
        OpeningClassifier.SetLinesForTest(
        [
            ("C60", "Ruy Lopez", ["e4", "e5", "Nf3", "Nc6", "Bb5"]),
        ]);

        var match = OpeningClassifier.Classify(["e4", "e5", "Qh5", "Nc6", "Bc4"]);
        Assert.Null(match.Name);
        Assert.Null(match.Eco);
    }

    [Fact]
    public void CatalogFiles_AreAThroughE()
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace-openings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.tsv"), "eco\tname\tpgn\nA00\tAlpha\t1. a3\n");
            File.WriteAllText(Path.Combine(dir, "e.tsv"), "eco\tname\tpgn\nE00\tEcho\t1. d4 Nf6\n");
            File.WriteAllText(Path.Combine(dir, "z.tsv"), "eco\tname\tpgn\nZ00\tIgnored\t1. h4\n");
            var names = OpeningClassifier.LoadLines(dir).Select(line => line.Name).ToList();
            Assert.Equal(new[] { "Alpha", "Echo" }, names);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Attest_EmitsEcoFamilyVariationAndConcept()
    {
        var match = new OpeningClassifier.OpeningMatch("Queen's Gambit Declined: Semi-Slav", "D30");
        using var builder = new SubstrateChangeBuilder(ChessVocabulary.AnalysisSourceId, "test/opening-identity")
            .DeclareSourcePrior(Laplace.Decomposers.Abstractions.SourceTrust.StructuredCorpus);
        var subject = Hash128.Text("test/opening-line");
        OpeningClassifier.Attest(builder, subject, match, 0.7, ChessVocabulary.AnalysisSourceId);
        var change = builder.Build();
        try
        {
            Assert.Contains(change.Attestations, a =>
                a.TypeId == ChessVocabulary.EcoCodeType && a.SubjectId == subject
                && a.ObjectId == ContentEmitter.RootId("D30"));
            Assert.Contains(change.Attestations, a =>
                a.TypeId == ChessVocabulary.OpeningNameType && a.SubjectId == subject
                && a.ObjectId == ContentEmitter.RootId("Queen's Gambit Declined: Semi-Slav"));
            Assert.Contains(change.Attestations, a =>
                a.TypeId == ChessVocabulary.FeatureType && a.SubjectId == subject
                && a.ObjectId == ContentEmitter.RootId("Queen's Gambit Declined"));
            Assert.Contains(change.Attestations, a =>
                a.TypeId == ChessVocabulary.FeatureType && a.SubjectId == subject
                && a.ObjectId == ContentEmitter.RootId("Semi-Slav"));
            Assert.True(ChessConcepts.TryResolve("gambit", chessDomain: true, out var gambit));
            Assert.Contains(change.Attestations, a =>
                a.TypeId == ChessVocabulary.FeatureType && a.SubjectId == subject
                && a.ObjectId == gambit.Id
                && a.ContextId == gambit.Member(ChessConcepts.Domain.Opening));
            Assert.Contains(change.Entities, e => e.Id == gambit.Id);
        }
        finally
        {
            foreach (var stage in change.IntentStages) stage.Dispose();
        }
    }
}
