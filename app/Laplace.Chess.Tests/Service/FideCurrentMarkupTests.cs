using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class FideCurrentMarkupTests
{
    [Fact]
    public void Profile_AcceptsBomBeforeName()
    {
        const string html = """
            <html><head><title>﻿ Nakamura, Hikaru FIDE Profile</title></head><body>
            <div>2792 STANDARD</div><div>2738 RAPID</div><div>2800 BLITZ</div>
            <div>FIDE ID 2016192 Federation United States of America B-Year 1987 Gender Male FIDE title Grandmaster World Rank 2</div>
            </body></html>
            """;

        var profile = ChessGameFetcher.ParseFideProfile(
            html, "2016192", "https://ratings.fide.com/profile/2016192");

        Assert.Equal("Nakamura, Hikaru", profile.DisplayName);
        Assert.Equal("2016192", profile.ProviderId);
        Assert.Equal(2792, profile.Ratings["standard"]);
    }

    [Fact]
    public void Profile_RejectsMismatchedProviderId()
    {
        const string html = """
            <html><head><title>Nakamura, Hikaru FIDE Profile</title></head><body>
            <div>FIDE ID 9999999 Federation USA B-Year 1987</div>
            </body></html>
            """;

        Assert.Throws<InvalidDataException>(() => ChessGameFetcher.ParseFideProfile(
            html, "2016192", "https://ratings.fide.com/profile/2016192"));
    }

    [Fact]
    public void Search_AcceptsRelativeProfileHrefWithoutLeadingSlash()
    {
        const string html = """
            <table><tr>
              <td><a href="profile/2016192">Nakamura, Hikaru</a></td>
              <td data-label="title">GM</td><td><img alt="USA"></td>
              <td data-label="Rtg">2792</td><td data-label="Rtg">2738</td><td data-label="Rtg">2800</td>
              <td data-label="B-Year">1987</td>
            </tr></table>
            """;

        var player = Assert.Single(ChessGameFetcher.ParseFideSearch(html));
        Assert.Equal("2016192", player.FideId);
        Assert.Equal("Nakamura, Hikaru", player.Name);
    }

    [Fact]
    public void Top_AcceptsLabeledColumnsWithoutLegacyCssClasses()
    {
        const string html = """
            <table><tr>
              <td>2</td><td><a href="profile/2016192">Nakamura, Hikaru</a></td>
              <td><img alt="USA"></td><td data-label="Rating">2792</td>
              <td data-label="B-Year">1987</td>
            </tr></table>
            """;

        var player = Assert.Single(ChessGameFetcher.ParseFideTop(html, "open"));
        Assert.Equal(2, player.Rank);
        Assert.Equal("2016192", player.FideId);
        Assert.Equal(2792, player.Standard);
        Assert.Equal(1987, player.BirthYear);
        Assert.Equal("USA", player.Federation);
    }

    [Fact]
    [Trait("Tier", "live")]
    public async Task OfficialFideSearchProfileAndRosterAreUsable()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var exact = await ChessGameFetcher.SearchFideAsync("2016192", 25, timeout.Token);
        var exactPlayer = Assert.Single(exact);
        Assert.Equal("2016192", exactPlayer.FideId);
        Assert.Contains("Nakamura", exactPlayer.Name, StringComparison.OrdinalIgnoreCase);

        var byName = await ChessGameFetcher.SearchFideAsync("Hikaru", 25, timeout.Token);
        Assert.Contains(byName, p => p.FideId == "2016192");

        var top = await ChessGameFetcher.FetchFideTopAsync("open", 10, timeout.Token);
        Assert.NotEmpty(top);
        Assert.All(top, p => Assert.Matches("^[0-9]{4,12}$", p.FideId));
    }
}
