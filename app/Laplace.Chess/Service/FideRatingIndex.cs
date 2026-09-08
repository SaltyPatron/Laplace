namespace Laplace.Chess.Service;

/// <summary>
/// Immutable read indexes for one projected FIDE publication generation.
/// Sorting/canonical-name work is paid once when a snapshot is published, not on
/// every browser/search request.
/// </summary>
internal sealed class FideRatingIndex
{
    internal readonly record struct NameEntry(
        FideRatingList.Player Player,
        string CanonicalName,
        string CompactCanonicalName,
        string[] Tokens);

    private readonly Dictionary<string, FideRatingList.Player> _byId;
    private readonly FideRatingList.Player[] _standard;
    private readonly FideRatingList.Player[] _rapid;
    private readonly FideRatingList.Player[] _blitz;

    internal FideRatingIndex(FideRatingList.Player[] players)
    {
        _byId = new Dictionary<string, FideRatingList.Player>(players.Length, StringComparer.Ordinal);
        foreach (var player in players)
        {
            if (!_byId.TryAdd(player.FideId, player) && _byId[player.FideId] != player)
                throw new InvalidDataException(
                    $"FIDE publication contains conflicting projected records for {player.FideId}.");
        }
        // Repeated provider records may project to the same player. Index the
        // identity once in every view; never select an arbitrary conflicting
        // rating or manufacture a combined record. The source artifact remains
        // unchanged, including fields outside this display projection.
        players = _byId.Values.ToArray();
        Names = players.Select(static p =>
        {
            string canonical = PlayerAlias.Canonical(p.Name);
            return new NameEntry(
                p,
                canonical,
                canonical.Replace(" ", "", StringComparison.Ordinal),
                canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }).ToArray();

        _standard = Ranked(players, static p => p.Standard);
        _rapid = Ranked(players, static p => p.Rapid);
        _blitz = Ranked(players, static p => p.Blitz);
    }

    internal NameEntry[] Names { get; }

    internal bool TryFindById(string fideId, out FideRatingList.Player player)
        => _byId.TryGetValue(fideId, out player!);

    internal IReadOnlyList<FideRatingList.Player> Ranked(string mode)
        => mode switch
        {
            "rapid" => _rapid,
            "blitz" => _blitz,
            _ => _standard,
        };

    private static FideRatingList.Player[] Ranked(
        IEnumerable<FideRatingList.Player> players,
        Func<FideRatingList.Player, int> rating)
        => players
            .Where(p => rating(p) > 0)
            .OrderByDescending(rating)
            .ThenBy(static p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static p => p.FideId, StringComparer.Ordinal)
            .ToArray();
}
