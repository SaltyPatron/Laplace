using Laplace.Endpoints.OpenAICompat.Competitions;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class CompetitionFormatsTests
{
    private static Guid[] Entrants(int count) =>
        Enumerable.Range(1, count).Select(seed => new Guid(seed, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)).ToArray();

    private static int Seed(Guid entrant) => BitConverter.ToInt32(entrant.ToByteArray(), 0);

    private static IReadOnlyList<MatchPlan> Plan(string format, int count, string pairing = "seeded") =>
        CompetitionFormats.Find(format)!.Plan(Entrants(count), Pairings.Find(pairing)!);

    public static TheoryData<string, string, int> EveryFormatPairingAndSize()
    {
        var data = new TheoryData<string, string, int>();
        foreach (var format in CompetitionFormats.All)
            foreach (var pairing in Pairings.All.Take(format.Paired ? Pairings.All.Count : 1))
                for (var count = 2; count <= 33; count++) data.Add(format.Id, pairing.Id, count);
        return data;
    }

    /// <summary>What must hold for the storage and the drawing whatever the format: unique ids, sources
    /// that precede the matches they feed, each finishing place consumed once, each entrant entered.</summary>
    [Theory]
    [MemberData(nameof(EveryFormatPairingAndSize))]
    public void EveryPlanIsAWellFormedDag(string format, string pairing, int count)
    {
        var plan = Plan(format, count, pairing);
        var seen = new Dictionary<string, MatchPlan>();
        var consumed = new HashSet<(string, short)>();
        foreach (var match in plan)
        {
            Assert.Equal(2, match.Slots.Count);
            foreach (var slot in match.Slots)
            {
                Assert.True(slot.Entrant is null != slot.FromMatch is null);
                Assert.Equal(slot.FromMatch is null, slot.FromPlace is null);
                if (slot.FromMatch is null) continue;
                Assert.True(seen.TryGetValue(slot.FromMatch, out var source), $"{match.Id} is fed by {slot.FromMatch}, which is not earlier");
                Assert.InRange(slot.FromPlace!.Value, 1, source!.Slots.Count);
                Assert.True(consumed.Add((slot.FromMatch, slot.FromPlace.Value)), $"{slot.FromMatch} place {slot.FromPlace} is used twice");
                if (source.Stage == match.Stage) Assert.True(source.Round < match.Round);
            }
            Assert.True(seen.TryAdd(match.Id, match), $"duplicate match id {match.Id}");
        }
        var entered = plan.SelectMany(match => match.Slots).Where(slot => slot.Entrant is not null).Select(slot => slot.Entrant!.Value).ToArray();
        Assert.Equal(Entrants(count).Order(), entered.Distinct().Order());

        var stages = CompetitionFormats.Find(format)!.Stages(
            plan.GroupBy(match => match.Stage).ToDictionary(stage => stage.Key, stage => stage.Max(match => match.Round)));
        Assert.Equal(plan.Select(match => match.Stage).Distinct().Order(), stages.Select(stage => stage.Id).Order());
    }

    [Theory]
    [InlineData(2), InlineData(3), InlineData(5), InlineData(8), InlineData(13), InlineData(32), InlineData(33)]
    public void SingleEliminationEliminatesAllButOne(int count)
    {
        var plan = Plan("single_elimination", count);
        Assert.Equal(count - 1, plan.Count);
        Assert.Equal("Final", plan[^1].Name);
        Assert.All(plan.SelectMany(match => match.Slots), slot => Assert.NotEqual((short)2, slot.FromPlace));
        Assert.All(plan, match => Assert.All(match.Slots.Where(slot => slot.Entrant is not null),
            slot => Assert.Equal(1, plan.SelectMany(m => m.Slots).Count(s => s.Entrant == slot.Entrant))));
    }

    private static int[][] OpeningPairs(IReadOnlyList<MatchPlan> plan) => plan.Where(match => match.Round == 1 && match.Slots.All(slot => slot.Entrant is not null))
        .Select(match => match.Slots.Select(slot => Seed(slot.Entrant!.Value)).ToArray()).ToArray();

    [Fact]
    public void EachPairingSetsTheOpeningRound()
    {
        Assert.Equal([[1, 8], [4, 5], [2, 7], [3, 6]], OpeningPairs(Plan("single_elimination", 8, "seeded")));
        Assert.Equal([[1, 5], [2, 6], [3, 7], [4, 8]], OpeningPairs(Plan("single_elimination", 8, "split")));
        Assert.Equal([[1, 2], [3, 4], [5, 6], [7, 8]], OpeningPairs(Plan("single_elimination", 8, "list")));
        Assert.Equal([[1, 2], [3, 4], [5, 6], [7, 8]], OpeningPairs(Plan("double_elimination", 8, "list")));
    }

    /// <summary>With byes, nobody skips more than the opening round, whichever pairing is used.</summary>
    [Theory]
    [MemberData(nameof(EveryFormatPairingAndSize))]
    public void AByeSkipsOnlyTheOpeningRound(string format, string pairing, int count)
    {
        if (format != "single_elimination") return;
        var plan = Plan(format, count, pairing);
        Assert.Equal(count - 1, plan.Count);
        Assert.All(plan.Where(match => match.Slots.Any(slot => slot.Entrant is not null)), match => Assert.InRange(match.Round, 1, 2));
    }

    [Fact]
    public void ByesGoToTheTopSeeds()
    {
        var plan = Plan("single_elimination", 6);
        var first = plan.Where(match => match.Round == 1).ToArray();
        Assert.Equal(2, first.Length);
        Assert.Equal([3, 4, 5, 6], first.SelectMany(match => match.Slots).Select(slot => Seed(slot.Entrant!.Value)).Order());
        var second = plan.Where(match => match.Round == 2).ToArray();
        Assert.Equal([1, 2], second.Select(match => Seed(match.Slots.Single(slot => slot.Entrant is not null).Entrant!.Value)).Order());
    }

    [Fact]
    public void SeedOrderKeepsTheTopTwoApartUntilTheFinal()
    {
        Assert.Equal([1, 8, 4, 5, 2, 7, 3, 6], CompetitionFormats.SeedOrder(8));
    }

    [Theory]
    [InlineData(3), InlineData(4), InlineData(6), InlineData(8), InlineData(11), InlineData(16)]
    public void DoubleEliminationTakesTwoLossesToGoOut(int count)
    {
        var plan = Plan("double_elimination", count);
        // Everyone but the champion loses out; the grand final's loser has lost at least once before or there.
        Assert.Equal("gf", plan[^1].Id);
        var losses = plan.Count;
        Assert.InRange(losses, 2 * count - 2, 2 * count - 1);
        // Every winners-bracket loss drops into the losers bracket or the grand final.
        var winners = plan.Where(match => match.Stage == "winners").Select(match => match.Id).ToHashSet();
        var droppedTo = plan.SelectMany(match => match.Slots.Where(slot => slot.FromPlace == 2).Select(slot => (slot.FromMatch!, match.Stage))).ToArray();
        Assert.Equal(winners.Order(), droppedTo.Select(drop => drop.Item1).Order());
        Assert.All(droppedTo, drop => Assert.NotEqual("winners", drop.Stage));
        // A losers-bracket loss is final: nothing takes its second place.
        Assert.DoesNotContain(plan.SelectMany(match => match.Slots),
            slot => slot.FromPlace == 2 && slot.FromMatch!.StartsWith('l'));
        var rounds = plan.Where(match => match.Stage == "losers").Select(match => match.Round).Distinct().Order().ToArray();
        Assert.Equal(Enumerable.Range(1, rounds.Length), rounds);
    }

    [Theory]
    [InlineData(2), InlineData(5), InlineData(6), InlineData(9), InlineData(12)]
    public void RoundRobinPairsEveryoneOnceAndNobodyTwiceInARound(int count)
    {
        var plan = Plan("round_robin", count);
        var pairs = plan.Select(match => match.Slots.Select(slot => Seed(slot.Entrant!.Value)).Order().ToArray()).Select(p => (p[0], p[1])).ToArray();
        Assert.Equal(count * (count - 1) / 2, pairs.Distinct().Count());
        Assert.Equal(pairs.Length, pairs.Distinct().Count());
        foreach (var round in plan.GroupBy(match => match.Round))
        {
            var playing = round.SelectMany(match => match.Slots).Select(slot => slot.Entrant!.Value).ToArray();
            Assert.Equal(playing.Length, playing.Distinct().Count());
            Assert.Equal(count / 2, round.Count());
        }
        Assert.Equal(count % 2 == 0 ? count - 1 : count, plan.Select(match => match.Round).Distinct().Count());
    }

    [Fact]
    public void StepladderClimbsFromTheBottomSeedToTheTop()
    {
        var plan = Plan("stepladder", 5);
        Assert.Equal(4, plan.Count);
        Assert.Equal([5, 4], plan[0].Slots.Select(slot => Seed(slot.Entrant!.Value)));
        for (var step = 1; step < plan.Count; step++)
        {
            Assert.Equal(new SlotPlan(null, plan[step - 1].Id, 1), plan[step].Slots[0]);
            Assert.Equal(4 - step, Seed(plan[step].Slots[1].Entrant!.Value));
        }
        Assert.Equal("Title match", plan[^1].Name);
    }

    [Fact]
    public void OnlyTheLinkRoutesArePublic()
    {
        static bool Public(string method, string path)
        {
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            http.Request.Method = method;
            return CompetitionEndpoints.IsPublic(http.Request, path);
        }
        var id = Guid.NewGuid();
        Assert.True(Public("GET", $"/v1/competitions/{id}"));
        Assert.True(Public("POST", $"/v1/competitions/{id}/entrants"));
        Assert.False(Public("GET", "/v1/competitions"));
        Assert.False(Public("POST", "/v1/competitions"));
        Assert.False(Public("GET", "/v1/competitions/formats"));
        Assert.False(Public("DELETE", $"/v1/competitions/{id}"));
        Assert.False(Public("POST", $"/v1/competitions/{id}/start"));
        Assert.False(Public("DELETE", $"/v1/competitions/{id}/entrants/{Guid.NewGuid()}"));
        Assert.False(Public("PUT", $"/v1/competitions/{id}/matches/gf/result"));
        Assert.False(Public("GET", $"/v1/competitions/{id}/entrants"));
        Assert.False(Public("POST", $"/v1/competitions/{id}/entrants/batch"));
        Assert.False(Public("PUT", $"/v1/competitions/{id}/draw"));
        Assert.False(Public("POST", $"/v1/billing/{id}/entrants"));
    }
}
