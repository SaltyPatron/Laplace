namespace Laplace.Modality.Chess;

/// <summary>
/// What a GAME proves about the rules it was played under — and therefore which rule sets
/// it could be, rather than which one a tag claims.
///
/// WHY EVIDENCE AND NOT A TAG. The PGN <c>[Variant "..."]</c> header is one witness, and a
/// weak one: sites spell it differently, omit it, and get it wrong. The MOVES cannot lie.
/// A piece that appears with no capture to explain it proves drops. A capture that removes
/// bystanders proves atomic. A king that ends on a centre square with the opponent not
/// mated is evidence of King of the Hill. So the game is read for constraints and the
/// constraints pick out candidates.
///
/// A SET, NOT AN ANSWER. Most games are consistent with several rule sets, because most
/// games never exercise the rule that distinguishes them — a King-of-the-Hill game decided
/// by ordinary checkmate is indistinguishable from standard, and saying "standard" would be
/// a guess dressed as a fact. So this narrows and stops. One candidate: attest it. Several:
/// attest the observations and not the guess. None: the rules are ones nobody pre-seeded,
/// which is not an error — the rule surface mints its own id and the substrate has learned
/// a variant by being shown one.
///
/// Unattested is not attested-false, applied to rules instead of to relations.
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
    /// it. Rules that were never exercised cannot eliminate anything, which is why the
    /// result is usually a set.
    /// </summary>
    public IReadOnlyList<(string Name, ChessVariantRules Rules)> Candidates()
    {
        var live = new List<(string, ChessVariantRules)>();
        foreach (var (name, rules) in ChessVariants.Conventional)
            if (!Contradicts(rules)) live.Add((name, rules));

        // A claimed tag does not decide, but among survivors it ranks: the source's own
        // statement is evidence, and where the moves have not contradicted it, it is the
        // best evidence available.
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
    /// Observations become axes directly; unexercised axes keep the standard default. This
    /// is what gets an id when no pre-seeded set matches — the variant nobody registered.
    /// </summary>
    public ChessVariantRules Observed() => new()
    {
        Files = Files,
        Ranks = Ranks,
        Pieces = PiecesSeen.Length > 0 ? PiecesSeen : ChessVariantRules.Standard.Pieces,
        // ONLY RAISE FROM POSITIVE EVIDENCE, NEVER LOWER FROM ABSENCE.
        //
        // Castling stays at the standard default whatever was observed. Seeing a castle
        // proves castling exists; NOT seeing one proves nothing — most games never castle.
        // An earlier cut wrote `Castling = CastlingObserved || ...`, so a game that merely
        // never castled minted the rule surface "nocastle:1,atomic:1" instead of "atomic:1"
        // — a DIFFERENT variant from the one it actually was, and two recordings of the same
        // rules stopped colliding. That is unattested read as attested-false, in the one
        // place this design cannot afford it.
        //
        // Nothing a game does can prove castling is forbidden, so nothing here lowers it.
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
