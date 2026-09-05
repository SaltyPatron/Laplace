using System.Text;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class FideRatingListTests
{
    private const string Xml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <playerslist>
          <player>
            <fideid>2016192</fideid><name>Nakamura, Hikaru</name><country>USA</country><sex>M</sex><title>GM</title>
            <rating>2792</rating><rapid_rating>2745</rapid_rating><blitz_rating>2810</blitz_rating><birthday>19871209</birthday><flag />
          </player>
          <player>
            <fideid>1503014</fideid><name>Carlsen, Magnus</name><country>NOR</country><sex>M</sex><title>GM</title>
            <rating>2840</rating><rapid_rating>2820</rapid_rating><blitz_rating>2890</blitz_rating><birthday>19901130</birthday><flag />
          </player>
          <player>
            <fideid>7000001</fideid><name>Example, Alice</name><country>ENG</country><sex>F</sex><title>WG</title>
            <rating>2500</rating><rapid_rating>2600</rapid_rating><blitz_rating>2450</blitz_rating><birthday>20080503</birthday><flag />
          </player>
          <player>
            <fideid>7000002</fideid><name>Example, Junior</name><country>FRA</country><sex>M</sex><title>M</title>
            <rating>2550</rating><rapid_rating>2400</rapid_rating><blitz_rating>2500</blitz_rating><birthday>20070101</birthday><flag />
          </player>
          <player>
            <fideid>7000003</fideid><name>Inactive, Strong</name><country>GER</country><sex>M</sex><title>G</title>
            <rating>2999</rating><rapid_rating>2999</rapid_rating><blitz_rating>2999</blitz_rating><birthday>19990101</birthday><flag>i</flag>
          </player>
        </playerslist>
        """;

    [Fact]
    public void Search_UsesPublishedIdentityAndAllRatingPlanes()
    {
        using var xml = Stream();
        var player = Assert.Single(FideRatingList.SearchXml(xml, "Hikaru", 25));

        Assert.Equal("2016192", player.FideId);
        Assert.Equal("Nakamura, Hikaru", player.Name);
        Assert.Equal("USA", player.Federation);
        Assert.Equal("GM", player.Title);
        Assert.Equal(2792, player.Standard);
        Assert.Equal(2745, player.Rapid);
        Assert.Equal(2810, player.Blitz);
        Assert.Equal(1987, player.BirthYear);
    }

    [Fact]
    public void Top_UsesRequestedRatingPlaneAndExcludesInactivePlayers()
    {
        using var standardXml = Stream();
        var standard = FideRatingList.TopXml(standardXml, "open", 3, 2026);
        Assert.Equal("1503014", standard[0].FideId);
        Assert.DoesNotContain(standard, p => p.FideId == "7000003");
        Assert.Equal(new int?[] { 1, 2, 3 }, standard.Select(p => p.Rank).ToArray());

        using var rapidXml = Stream();
        var rapid = FideRatingList.TopXml(rapidXml, "men_rapid", 3, 2026);
        Assert.Equal("1503014", rapid[0].FideId);
        Assert.Equal(2820, rapid[0].Rapid);
    }

    [Fact]
    public void Cohorts_FilterSexAndJuniorBirthYearWithoutChangingIdentity()
    {
        using var womenXml = Stream();
        var women = FideRatingList.TopXml(womenXml, "women", 10, 2026);
        var woman = Assert.Single(women);
        Assert.Equal("7000001", woman.FideId);
        Assert.Equal("WGM", woman.Title);

        using var juniorsXml = Stream();
        var juniors = FideRatingList.TopXml(juniorsXml, "juniors", 10, 2026);
        Assert.Equal(new[] { "7000002", "7000001" }, juniors.Select(p => p.FideId).ToArray());

        using var girlsXml = Stream();
        var girl = Assert.Single(FideRatingList.TopXml(girlsXml, "girls", 10, 2026));
        Assert.Equal("7000001", girl.FideId);
    }

    [Fact]
    public void Search_PreservesRecordsAcrossBoundedGrammarBatches()
    {
        const int count = 4105; // deliberately crosses the 4096-record grammar batch boundary
        byte[] estate = BuildEstate(count, needleAt: count - 1);

        using var xml = new MemoryStream(estate);
        var player = Assert.Single(FideRatingList.SearchXml(xml, "Needle", 5));
        Assert.Equal((8000000 + count - 1).ToString(), player.FideId);
        Assert.Equal("Needle, Player", player.Name);
        Assert.Equal("IM", player.Title);
    }

    [Fact]
    public void Projection_SingleAndParallelWorkersAreFieldAndOrderEquivalent()
    {
        const int count = 8209; // three physical batches; semantic output must not care
        byte[] estate = BuildEstate(count, needleAt: count - 3);

        using var serialXml = new MemoryStream(estate, writable: false);
        var serial = FideRatingList.ProjectXml(serialXml, parseWorkers: 1);

        using var parallelXml = new MemoryStream(estate, writable: false);
        var parallel = FideRatingList.ProjectXml(parallelXml, parseWorkers: 4);

        Assert.Equal(count, serial.Length);
        Assert.Equal(serial, parallel);
        Assert.Equal((8000000).ToString(), parallel[0].FideId);
        Assert.Equal((8000000 + count - 1).ToString(), parallel[^1].FideId);
        Assert.Equal("Needle, Player", parallel[^3].Name);
    }

    [Fact]
    public void Projection_CancelledBeforeWorkDoesNotParseEstate()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var xml = Stream();
        Assert.Throws<OperationCanceledException>(() =>
            FideRatingList.ProjectXml(xml, parseWorkers: 4, cts.Token));
    }

    private static byte[] BuildEstate(int count, int needleAt)
    {
        var source = new StringBuilder("<?xml version=\"1.0\"?><playerslist>");
        for (int i = 0; i < count; i++)
        {
            string id = (8000000 + i).ToString();
            string name = i == needleAt ? "Needle, Player" : $"Fixture, Player {i}";
            source.Append("<player><fideid>").Append(id)
                .Append("</fideid><name>").Append(name)
                .Append("</name><country>USA</country><sex>M</sex><title>M</title>")
                .Append("<rating>2000</rating><rapid_rating>1900</rapid_rating>")
                .Append("<blitz_rating>1800</blitz_rating><birthday>20000101</birthday><flag />")
                .Append("</player>");
        }
        source.Append("</playerslist>");
        return Encoding.UTF8.GetBytes(source.ToString());
    }

    private static MemoryStream Stream()
        => new(Encoding.UTF8.GetBytes(Xml));
}
