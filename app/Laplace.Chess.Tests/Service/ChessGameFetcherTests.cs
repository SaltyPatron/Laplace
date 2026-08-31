using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessGameFetcherTests
{
    [Fact]
    public void FideProfile_UsesTheProfileTitle_NotNavigationText()
    {
        const string html = """
            <html><head><title>Carlsen, Magnus FIDE Profile</title></head><body>
            <nav>International Chess Federation RATINGS</nav>
            <h1>Carlsen, Magnus</h1><div>2823</div><div>STANDARD</div>
            <div>2803</div><div>RAPID</div><div>2860</div><div>BLITZ</div>
            <h5>FIDE ID</h5><div>1503014</div>
            <h5>Federation</h5><div>Norway</div><h5>B-Year</h5><div>1990</div>
            <h5>Gender</h5><div>Male</div><h5>FIDE title</h5><div>Grandmaster</div>
            <h5>World Rank</h5>
            </body></html>
            """;

        var p = ChessGameFetcher.ParseFideProfile(
            html, "1503014", "https://ratings.fide.com/profile/1503014");

        Assert.Equal("Carlsen, Magnus", p.DisplayName);
        Assert.Equal("Norway", p.Federation);
        Assert.Equal("Grandmaster", p.Title);
        Assert.Equal(2823, p.Ratings["standard"]);
        Assert.Equal(2803, p.Ratings["rapid"]);
        Assert.Equal(2860, p.Ratings["blitz"]);
    }

    [Fact]
    public void FideSearch_ParsesOfficialCandidateRows()
    {
        const string html = """
            <table><tr>
              <td><a href="/profile/1503014">Carlsen, Magnus</a></td>
              <td data-label="title">GM</td><td><img alt="NOR"></td>
              <td data-label="Rtg">2839</td><td data-label="Rtg">2818</td><td data-label="Rtg">2890</td>
              <td data-label="B-Year">1990</td>
            </tr></table>
            """;

        var player = Assert.Single(ChessGameFetcher.ParseFideSearch(html));
        Assert.Equal("1503014", player.FideId);
        Assert.Equal("Carlsen, Magnus", player.Name);
        Assert.Equal("GM", player.Title);
        Assert.Equal("NOR", player.Federation);
        Assert.Equal(2839, player.Standard);
        Assert.Equal(1990, player.BirthYear);
    }

    [Fact]
    public void FideTop_ParsesOfficialCohortRank()
    {
        const string html = """
            <table><tr><td><span class="rank_span">1</span></td>
              <td><a href="/profile/700070">Hou, Yifan</a></td>
              <td class="flag-wrapper"><img src="/images/flags/cn.svg"> CHN</td>
              <td class="rating_column">2617</td><td class="bday_column">1994</td>
            </tr></table>
            """;

        var player = Assert.Single(ChessGameFetcher.ParseFideTop(html, "women"));
        Assert.Equal(1, player.Rank);
        Assert.Equal("700070", player.FideId);
        Assert.Equal("CHN", player.Federation);
        Assert.Equal(2617, player.Standard);
    }
}
