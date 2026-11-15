namespace Laplace.Endpoints.OpenAICompat.Competitions;

/// <summary>A slot of a planned match: an entrant directly, or a finishing place in an earlier match.</summary>
internal readonly record struct SlotPlan(Guid? Entrant, string? FromMatch, short? FromPlace);

internal sealed record MatchPlan(string Id, string Stage, int Round, string Name, IReadOnlyList<SlotPlan> Slots);

internal sealed record StagePlan(string Id, string Title, IReadOnlyList<string>? Rounds);

/// <summary>
/// A format supplies only who meets whom. Storage, endpoints and drawing are the same for every
/// format, so a new one is one class added to <see cref="CompetitionFormats.All"/>.
/// </summary>
internal interface ICompetitionFormat
{
    string Id { get; }
    string Title { get; }
    string Summary { get; }

    /// <summary>Whether the opening round is made of pairs the host can choose a pairing for.</summary>
    bool Paired { get; }

    /// <summary>Matches in an order where every source precedes the matches it feeds. Entrants are in seed order.</summary>
    IReadOnlyList<MatchPlan> Plan(IReadOnlyList<Guid> entrants, IPairing pairing);

    /// <summary>The stages in display order, given the highest round played in each.</summary>
    IReadOnlyList<StagePlan> Stages(IReadOnlyDictionary<string, int> rounds);
}

/// <summary>
/// A pairing supplies only which seeds sit side by side in the opening round. Every bracket
/// built from pairs uses whichever one the host picked, so a new one is one class added to
/// <see cref="Pairings.All"/>.
/// </summary>
internal interface IPairing
{
    string Id { get; }
    string Title { get; }
    string Summary { get; }

    /// <summary>
    /// Seed numbers in opening-round slot order: slots 2k and 2k+1 meet. <paramref name="size"/> is
    /// the bracket size, a power of two; a seed above <paramref name="count"/> is an empty slot, and
    /// no pair may be empty on both sides.
    /// </summary>
    int[] Slots(int size, int count);
}

internal static class Pairings
{
    public static readonly IReadOnlyList<IPairing> All = [new SeededPairing(), new SplitPairing(), new ListPairing()];
    public static IPairing Default => All[0];
    public static IPairing? Find(string? id) => All.FirstOrDefault(pairing => pairing.Id == id);
}

/// <summary>1 meets the last seed, and 1 and 2 can meet only in the final.</summary>
internal sealed class SeededPairing : IPairing
{
    public string Id => "seeded";
    public string Title => "Top against bottom";
    public string Summary => "1 plays the last seed, 2 the next to last. The top two can only meet in the final, and top seeds get the byes.";
    public int[] Slots(int size, int count) => CompetitionFormats.SeedOrder(size);
}

/// <summary>The top half meets the bottom half in order.</summary>
internal sealed class SplitPairing : IPairing
{
    public string Id => "split";
    public string Title => "Top half against bottom half";
    public string Summary => "With eight, 1 plays 5, 2 plays 6, and so on down. Top seeds get the byes.";
    public int[] Slots(int size, int count) =>
        Enumerable.Range(1, size / 2).SelectMany(seed => new[] { seed, seed + size / 2 }).ToArray();
}

/// <summary>Neighbours in the list meet, so the host's order is the matchups.</summary>
internal sealed class ListPairing : IPairing
{
    public string Id => "list";
    public string Title => "As listed";
    public string Summary => "1 plays 2, 3 plays 4, down the entrant list, so ordering the list picks every matchup. Any byes go to the end of the list.";
    public int[] Slots(int size, int count)
    {
        // Every pair needs someone, so only the first count - size/2 pairs are played and the rest hold one entrant each.
        var played = count - size / 2;
        var slots = new int[size];
        for (int pair = 0, seed = 1, empty = count + 1; pair < size / 2; pair++)
        {
            slots[pair * 2] = seed++;
            slots[pair * 2 + 1] = pair < played ? seed++ : empty++;
        }
        return slots;
    }
}

internal static class CompetitionFormats
{
    /// <summary>The plan under the default pairing.</summary>
    public static IReadOnlyList<MatchPlan> Plan(this ICompetitionFormat format, IReadOnlyList<Guid> entrants) =>
        format.Plan(entrants, Pairings.Default);

    public static readonly IReadOnlyList<ICompetitionFormat> All =
    [
        new KnockoutFormat(withLosers: false),
        new KnockoutFormat(withLosers: true),
        new LeagueFormat(),
        new LadderFormat(),
    ];

    public static ICompetitionFormat? Find(string? id) => All.FirstOrDefault(format => format.Id == id);

    /// <summary>Seeds in bracket order: 1 meets the last seed, and 1 and 2 can meet only in the final.</summary>
    internal static int[] SeedOrder(int size)
    {
        var order = new[] { 1 };
        while (order.Length < size)
        {
            var span = order.Length * 2 + 1;
            order = order.SelectMany(seed => new[] { seed, span - seed }).ToArray();
        }
        return order;
    }

    internal static string[] KnockoutRounds(int rounds) =>
        Enumerable.Range(1, rounds).Select(round => (rounds - round) switch
        {
            0 => "Final",
            1 => "Semifinals",
            2 => "Quarterfinals",
            var left => $"Round of {1 << (left + 1)}",
        }).ToArray();
}

/// <summary>
/// The one rule every bracket is built from: two feeds meet in a match, and a feed with nobody
/// opposite it passes through unplayed. Byes need no cases of their own.
/// </summary>
internal sealed class MatchBuilder
{
    public List<MatchPlan> Matches { get; } = [];

    public (SlotPlan? Winner, SlotPlan? Loser, bool Played) Meet(SlotPlan? a, SlotPlan? b, string id, string stage, int round, string name)
    {
        if (a is null) return (b, null, false);
        if (b is null) return (a, null, false);
        Matches.Add(new MatchPlan(id, stage, round, name, [a.Value, b.Value]));
        return (new SlotPlan(null, id, 1), new SlotPlan(null, id, 2), true);
    }
}

/// <summary>Lose once and you are out; or, with a losers bracket, lose twice.</summary>
internal sealed class KnockoutFormat(bool withLosers) : ICompetitionFormat
{
    public string Id => withLosers ? "double_elimination" : "single_elimination";
    public string Title => withLosers ? "Double elimination" : "Single elimination";
    public string Summary => withLosers
        ? "Everyone has two lives: a first loss drops you to the losers bracket, a second puts you out."
        : "Lose once and you are out.";
    public bool Paired => true;

    public IReadOnlyList<StagePlan> Stages(IReadOnlyDictionary<string, int> rounds)
    {
        if (!withLosers) return [new("main", "Bracket", CompetitionFormats.KnockoutRounds(rounds.GetValueOrDefault("main")))];
        return new StagePlan[]
        {
            new("winners", "Winners", CompetitionFormats.KnockoutRounds(rounds.GetValueOrDefault("winners"))),
            new("losers", "Losers", null),
            new("final", "Final", ["Grand final"]),
        }.Where(stage => rounds.ContainsKey(stage.Id)).ToArray();
    }

    public IReadOnlyList<MatchPlan> Plan(IReadOnlyList<Guid> entrants, IPairing pairing)
    {
        var size = 2;
        while (size < entrants.Count) size *= 2;
        var rounds = int.Log2(size);
        var titles = CompetitionFormats.KnockoutRounds(rounds);
        var builder = new MatchBuilder();
        var feeds = pairing.Slots(size, entrants.Count)
            .Select(seed => seed <= entrants.Count ? new SlotPlan(entrants[seed - 1], null, null) : (SlotPlan?)null).ToList();
        var dropped = new List<List<SlotPlan?>>();
        var won = 0;
        for (var round = 1; round <= rounds; round++)
        {
            var next = new List<SlotPlan?>();
            var losers = new List<SlotPlan?>();
            for (var i = 0; i < feeds.Count; i += 2)
            {
                var number = i / 2 + 1;
                var (id, name) = withLosers ? ($"w{won + 1}", $"Winners {won + 1}")
                    : ($"r{round}m{number}", feeds.Count == 2 ? "Final" : $"{titles[round - 1].TrimEnd('s')} {number}");
                var (winner, loser, played) = builder.Meet(feeds[i], feeds[i + 1], id, withLosers ? "winners" : "main", round, name);
                if (played) won++;
                next.Add(winner);
                losers.Add(loser);
            }
            feeds = next;
            dropped.Add(losers);
        }
        if (!withLosers) return builder.Matches;

        // Losers bracket: first-round losers meet each other; after that each round's survivors
        // meet the losers dropping from the next winners round, then meet each other.
        var lost = 0;
        var lap = 0;
        List<SlotPlan?> Pair(List<SlotPlan?> a, List<SlotPlan?> b)
        {
            lap++;
            var result = new List<SlotPlan?>();
            for (var i = 0; i < a.Count; i++)
            {
                var (winner, _, played) = builder.Meet(a[i], b[i], $"l{lost + 1}", "losers", lap, $"Losers {lost + 1}");
                if (played) lost++;
                result.Add(winner);
            }
            return result;
        }
        List<SlotPlan?> Halve(List<SlotPlan?> all) => all.Count < 2 ? all
            : Pair(all.Where((_, i) => i % 2 == 0).ToList(), all.Where((_, i) => i % 2 == 1).ToList());

        var survivors = Halve(dropped[0]);
        for (var round = 2; round <= rounds; round++)
        {
            // Alternate the side arriving losers enter on, so early rematches are avoided.
            var arriving = round % 2 == 0 ? Enumerable.Reverse(dropped[round - 1]).ToList() : dropped[round - 1];
            survivors = Halve(Pair(survivors, arriving));
        }
        builder.Meet(feeds[0], survivors[0], "gf", "final", 1, "Grand final");

        // Losers rounds in which nothing was played leave no gap in the round numbers.
        var laps = builder.Matches.Where(m => m.Stage == "losers").Select(m => m.Round).Distinct().Order()
            .Select((from, i) => (from, to: i + 1)).ToDictionary(p => p.from, p => p.to);
        return builder.Matches.Select(m => m.Stage == "losers" ? m with { Round = laps[m.Round] } : m).ToList();
    }
}

/// <summary>Everyone meets everyone once, by the circle method; with an odd count one entrant sits out each round.</summary>
internal sealed class LeagueFormat : ICompetitionFormat
{
    public string Id => "round_robin";
    public string Title => "Round robin";
    public string Summary => "Everyone plays everyone once. With an odd number, one entrant sits out each round.";

    public IReadOnlyList<StagePlan> Stages(IReadOnlyDictionary<string, int> rounds) => [new("league", "League", null)];

    public bool Paired => false;

    public IReadOnlyList<MatchPlan> Plan(IReadOnlyList<Guid> entrants, IPairing pairing)
    {
        var ring = entrants.Select(id => (Guid?)id).ToList();
        if (ring.Count % 2 == 1) ring.Add(null);
        var matches = new List<MatchPlan>();
        for (var round = 1; round < ring.Count; round++)
        {
            var number = 0;
            for (var i = 0; i < ring.Count / 2; i++)
            {
                if (ring[i] is not { } a || ring[^(i + 1)] is not { } b) continue;
                number++;
                matches.Add(new MatchPlan($"r{round}m{number}", "league", round, $"Round {round}, match {number}",
                    [new SlotPlan(a, null, null), new SlotPlan(b, null, null)]));
            }
            ring.Insert(1, ring[^1]);
            ring.RemoveAt(ring.Count - 1);
        }
        return matches;
    }
}

/// <summary>The two lowest seeds meet, and each winner climbs to the next seed up; the top seed waits in the title match.</summary>
internal sealed class LadderFormat : ICompetitionFormat
{
    public string Id => "stepladder";
    public string Title => "Stepladder";
    public string Summary => "The two lowest seeds meet and each winner climbs to face the next seed up. The top seed waits in the title match.";

    public IReadOnlyList<StagePlan> Stages(IReadOnlyDictionary<string, int> rounds)
    {
        var steps = rounds.GetValueOrDefault("ladder");
        return [new("ladder", "Stepladder", Enumerable.Range(1, steps).Select(step => step == steps ? "Title" : $"Step {step}").ToArray())];
    }

    public bool Paired => false;

    public IReadOnlyList<MatchPlan> Plan(IReadOnlyList<Guid> entrants, IPairing pairing)
    {
        var matches = new List<MatchPlan>();
        var steps = entrants.Count - 1;
        var climbing = new SlotPlan(entrants[^1], null, null);
        for (var step = 1; step <= steps; step++)
        {
            var id = $"s{step}";
            matches.Add(new MatchPlan(id, "ladder", step, step == steps ? "Title match" : $"Step {step}",
                [climbing, new SlotPlan(entrants[^(step + 1)], null, null)]));
            climbing = new SlotPlan(null, id, 1);
        }
        return matches;
    }
}
