using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessGameFetcherTests
{
    [Fact]
    public void PlayerIdentity_ConvergesAcrossEquivalentUnicodeSpellings()
    {
        const string composed = "Jos\u00e9 Ra\u00fal Capablanca";
        const string decomposed = "Jose\u0301 Rau\u0301l Capablanca";

        Assert.Equal(PlayerAlias.Canonical(composed), PlayerAlias.Canonical(decomposed));
        Assert.Equal(ChessVocabulary.PlayerId(composed), ChessVocabulary.PlayerId(decomposed));
    }

    [Fact]
    public void PgnReader_PreservesUnicodeAndNormalizesTagsToNfc()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                "[Event \"Unicode\"]\n"
                + "[White \"Jose\u0301 Rau\u0301l Capablanca\"]\n"
                + "[Black \"Mikhail Tal\"]\n"
                + "[Result \"1-0\"]\n\n"
                + "1. e4 e5 1-0\n",
                new System.Text.UTF8Encoding(false));

            string game = Assert.Single(PgnGames.StreamGames(path));
            Assert.Equal("Jos\u00e9 Ra\u00fal Capablanca", PgnGames.TagStr(game, "White"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PgnReader_RejectsMalformedUtf8InsteadOfCorruptingIdentity()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path,
            [
                (byte)'[', (byte)'E', (byte)'v', (byte)'e', (byte)'n', (byte)'t', (byte)' ', (byte)'"',
                0xc3, 0x28, (byte)'"', (byte)']', (byte)'\n',
            ]);
            Assert.Throws<System.Text.DecoderFallbackException>(() => PgnGames.StreamGames(path).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FideProfile_UsesTheProfileTitle_NotNavigationText()
    {
        const string html = """
            <html><head><title>Carlsen, Magnus FIDE Profile</title>
            <meta property="og:image" content="https://ratings.fide.com/card/1503014.png"></head><body>
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
        Assert.Equal("https://ratings.fide.com/card/1503014.png", p.AvatarUrl);
        Assert.Contains(p.Aliases, alias => alias.Equals("Magnus Carlsen", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LichessProfile_UsesFirstAndLastNameForRealIdentityAndKeepsLinksAndRatings()
    {
        const string json = """
            {
              "id":"drnykterstein", "username":"DrNykterstein", "title":"GM",
              "perfs":{"rapid":{"rating":2810},"blitz":{"rating":2900}},
              "profile":{
                "firstName":"Magnus", "lastName":"Carlsen", "country":"NO",
                "bio":"World champion", "fideRating":2839, "uscfRating":2900,
                "links":"https://magnus.example\nhttps://team.example"
              },
              "createdAt":1234
            }
            """;

        var p = ChessGameFetcher.ParseLichessProfile(json, "drnykterstein");

        Assert.Equal("Magnus Carlsen", p.RealName);
        Assert.Equal("World champion", p.Biography);
        Assert.Equal(2839, p.Ratings["fide"]);
        Assert.Equal(2900, p.Ratings["uscf"]);
        Assert.Contains("DrNykterstein", p.Aliases);
        Assert.Contains(p.Aliases, alias => alias.Equals("Magnus Carlsen", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("https://lichess.org/@/DrNykterstein", p.Links);
        Assert.Contains("https://magnus.example", p.Links);
    }

    [Fact]
    public void ChessComProfile_KeepsStableIdsAssetsNamesAndStreamingLinks()
    {
        const string profile = """
            {
              "@id":"https://api.chess.com/pub/player/magnuscarlsen",
              "player_id":3889224, "username":"MagnusCarlsen", "name":"Magnus Carlsen",
              "title":"GM", "avatar":"https://images.chesscomfiles.com/magnus.png",
              "url":"https://www.chess.com/member/magnuscarlsen",
              "country":"https://api.chess.com/pub/country/NO",
              "twitch_url":"https://twitch.tv/maskenissen",
              "streaming_platforms":[{"type":"twitch","url":"https://twitch.tv/maskenissen"}]
            }
            """;
        const string stats = """
            {"chess_rapid":{"last":{"rating":2829}},"chess_blitz":{"last":{"rating":3227}}}
            """;

        var p = ChessGameFetcher.ParseChessComProfile(profile, stats, "MagnusCarlsen");

        Assert.Equal("Magnus Carlsen", p.RealName);
        Assert.Equal("https://images.chesscomfiles.com/magnus.png", p.AvatarUrl);
        Assert.Equal("3889224", p.Facts["player_id"]);
        Assert.Equal("https://api.chess.com/pub/player/magnuscarlsen", p.Facts["@id"]);
        Assert.Contains("https://twitch.tv/maskenissen", p.Links);
        Assert.Equal(3227, p.Ratings["blitz"]);
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
    public void FideSearch_ParsesAbsoluteOfficialProfileLinks()
    {
        const string html = """
            <table><tr>
              <td><a href="https://ratings.fide.com/profile/1503014">Carlsen, Magnus</a></td>
              <td data-label="Rtg">2839</td>
            </tr></table>
            """;

        Assert.Equal("1503014", Assert.Single(ChessGameFetcher.ParseFideSearch(html)).FideId);
    }

    [Theory]
    [InlineData("Magnus", "Magnus")]
    [InlineData("Carlsen", "Carlsen")]
    [InlineData("Carlsen, Magnus", "Carlsen, Magnus", "magnus carlsen")]
    [InlineData("Magnus Carlsen", "Magnus Carlsen", "carlsen, magnus")]
    public void FideSearch_TermsCoverNaturalAndOfficialNameOrders(string query, params string[] expected)
    {
        var terms = ChessGameFetcher.FideSearchTerms(query);
        foreach (string term in expected)
            Assert.Contains(terms, actual => actual.Equals(term, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Magnus", "Carlsen, Magnus")]
    [InlineData("Carlsen", "Carlsen, Magnus")]
    [InlineData("Carlsen, Magnus", "Carlsen, Magnus")]
    public void FideSearch_RanksSingleAndReorderedNamesAsMatches(string query, string candidate)
        => Assert.True(ChessGameFetcher.FideCandidateScore(query, candidate) < 10);

    [Fact]
    public void CompleteArchive_HasNoProviderLimit_AndLimitedArchiveFailsClosed()
    {
        Assert.Null(ChessGameFetcher.ResolveArchiveLimit(true, "1000"));
        string all = ChessGameFetcher.LichessGamesUrl("Magnus Carlsen", null);
        string limited = ChessGameFetcher.LichessGamesUrl("Magnus Carlsen", 1000);
        Assert.DoesNotContain("max=", all);
        Assert.Contains("sort=dateAsc", all);
        Assert.Equal(1000, ChessGameFetcher.ResolveArchiveLimit(false, "1000"));
        Assert.Contains("sort=dateAsc", limited);
        Assert.Contains("max=1000", limited);
        Assert.Throws<ArgumentException>(() => ChessGameFetcher.ResolveArchiveLimit(false, ""));
        Assert.Throws<ArgumentException>(() => ChessGameFetcher.ResolveArchiveLimit(false, "0"));
    }

    [Fact]
    public void ChessComArchives_AreOldestFirst()
    {
        var actual = ChessGameFetcher.ChronologicalArchiveUrls(new[]
        {
            "https://api.chess.com/pub/player/x/games/2026/08",
            "https://api.chess.com/pub/player/x/games/1972/11",
            "https://api.chess.com/pub/player/x/games/2001/01",
        });

        Assert.Equal(new[]
        {
            "https://api.chess.com/pub/player/x/games/1972/11",
            "https://api.chess.com/pub/player/x/games/2001/01",
            "https://api.chess.com/pub/player/x/games/2026/08",
        }, actual);
    }

    [Fact]
    public void ChessComMonthlyGames_AreCanonicalizedOldestFirst()
    {
        static string Game(string date, string time, string site) => $"""
            [Event "Chronology"]
            [Site "{site}"]
            [UTCDate "{date}"]
            [UTCTime "{time}"]
            [White "White"]
            [Black "Black"]
            [Result "1-0"]

            1. e4 e5 1-0
            """;

        string late = Game("2024.05.03", "18:00:00", "late");
        string earliest = Game("2024.05.02", "23:59:59", "earliest");
        string sameDayEarly = Game("2024.05.03", "09:00:00", "same-day-early");
        string unknown = Game("????.??.??", "??:??:??", "unknown");

        var expected = new[] { earliest, sameDayEarly, late, unknown };
        Assert.Equal(expected, ChessGameFetcher.ChronologicalGames(new[] { unknown, late, earliest, sameDayEarly }));
        Assert.Equal(expected, ChessGameFetcher.ChronologicalGames(new[] { sameDayEarly, earliest, late, unknown }));
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
