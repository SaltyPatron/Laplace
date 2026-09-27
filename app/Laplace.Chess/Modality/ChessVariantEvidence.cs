namespace Laplace.Modality.Chess;

/// <summary>
/// What a game's moves prove about the rules it was played under, and therefore which rule
/// sets it could be. The PGN <c>[Variant "..."]</c> tag is one witness; the moves are read
/// for constraints: material appearing without a capture proves drops, a capture removing
/// bystanders proves atomic, a king ending on a centre square without mate is evidence of
/// King of the Hill.
///
/// The result is a candidate set, not an answer: most games never exercise the rule that
/// distinguishes variants, and an unexercised rule is unknown, not false. When no pre-seeded
/// set survives, <c>Observed</c> gives the rules the game does prove, whose rule
/// surface has its own content id.
/// </summary>
public sealed record ChessVariantEvidence
{
    /// <summary>Board dimensions read off the FEN.</summary>
    public int Files { get; init; } = 8;
    public int Ranks { get; init; } = 8;

    /// <summary>Distinct piece letters seen. Anything outside KQRBNP is a variant piece.</summary>
    public string PiecesSeen { get; init; } = "";

    /// <summary>A castle was actually played, so castling exists in these rules.</summary>
    public bool CastlingObserved { get; init; }

    /// <summary>A castling right was asserted by a FEN, which is weaker than playing one.</summary>
    public bool CastlingRightsAsserted { get; init; }

    /// <summary>Material appeared with no capture to account for it — drops.</summary>
    public bool MaterialAppeared { get; init; }

    /// <summary>A capture removed pieces other than the captured one — atomic.</summary>
    public bool CollateralCapture { get; init; }

    /// <summary>The mover had a capture available and did not play it — captures are optional.</summary>
    public bool DeclinedACapture { get; init; }

    /// <summary>A king reached a centre square and the game ended there without mate.</summary>
    public bool EndedWithKingOnCentre { get; init; }

    /// <summary>Number of checks the winner delivered, when the game ended without mate.</summary>
    public int ChecksDelivered { get; init; }

    /// <summary>The tag the source claimed, if any. A witness, never the verdict.</summary>
    public string? ClaimedVariant { get; init; }

    /// <summary>
    /// The pre-seeded rule sets this evidence does NOT rule out, most specific first.
    ///
    /// Elimination, not scoring: a candidate survives only if nothing observed contradicts
    /// it. Unexercised rules eliminate nothing, so the result is usually a set.
    /// </summary>
    public IReadOnlyList<(string Name, ChessVariantRules Rules)> Candidates()
    {
        var live = new List<(string, ChessVariantRules)>();
        foreach (var (name, rules) in ChessVariants.Conventional)
            if (!Contradicts(rules)) live.Add((name, rules));

        // The claimed tag does not decide, but it ranks first among survivors the moves
        // have not contradicted.
        if (ChessVariants.ByName(ClaimedVariant) is { } claimed)
            live.Sort((a, b) => (b.Item2 == claimed).CompareTo(a.Item2 == claimed));
        return live;
    }

    /// <summary>The single rule set when the evidence admits exactly one, else null.</summary>
    public ChessVariantRules? Resolved()
    {
        var c = Candidates();
        return c.Count == 1 ? c[0].Rules : null;
    }

    /// <summary>
    /// The rules this game proves it was played under, whether or not anyone named them.
    /// Observations become axes directly; unexercised axes keep the standard default.
    /// </summary>
    public ChessVariantRules Observed() => new()
    {
        Files = Files,
        Ranks = Ranks,
        Pieces = PiecesSeen.Length > 0 ? PiecesSeen : ChessVariantRules.Standard.Pieces,
        // Axes are raised only by positive evidence, never lowered by absence. Seeing a castle
        // proves castling exists; not seeing one proves nothing, and no game can prove castling
        // forbidden, so castling stays at the standard default and a game that never castled
        // has the same rule surface as one that did.
        Castling = ChessVariantRules.Standard.Castling,
        Drops = MaterialAppeared,
        CaptureExplodes = CollateralCapture,
        CaptureCompulsory = false,
        WinBySquares = EndedWithKingOnCentre ? "d4,d5,e4,e5" : "",
        WinByCheckCount = ChecksDelivered >= 3 ? 3 : 0,
    };

    private bool Contradicts(ChessVariantRules r)
    {
        if (r.Files != Files || r.Ranks != Ranks) return true;
        if (CastlingObserved && !r.Castling) return true;
        if (MaterialAppeared && !r.Drops) return true;
        if (CollateralCapture && !r.CaptureExplodes) return true;
        if (DeclinedACapture && r.CaptureCompulsory) return true;
        foreach (char c in PiecesSeen)
            if (!r.Pieces.Contains(c, StringComparison.Ordinal)) return true;
        return false;
    }
}
