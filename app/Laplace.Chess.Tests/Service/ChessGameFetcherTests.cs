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
}
