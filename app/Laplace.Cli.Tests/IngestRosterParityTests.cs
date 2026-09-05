using System.Reflection;
using System.Text.Json;
using Laplace.Cli;
using Xunit;

namespace Laplace.Cli.Tests;

public sealed class IngestRosterParityTests
{
    /// <summary>
    /// Runtime-only maintenance and alias routes that are deliberately absent from
    /// the seed-cadence manifest. This allowlist may only shrink as the roster is
    /// generated; a new route must be classified explicitly.
    /// </summary>
    private static readonly HashSet<string> OperationalOnlyRoutes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "agents",
            "chess",
            "chess-analyze",
            "chess-books",
            "chess-eval",
            "chess-move-outcomes",
            "chess-opening-match",
            "chess-position-outcomes",
            "chess-syzygy",
            "chess-transitions",
            "chess-trajectory",
            "code",
            "frame-video",
            "model",
            "model-corroborate",
            "omw-probe",
            "openings",
            "parquet",
            "recipe",
            "rgba-image",
            "safetensor",
            "tabular",
            "track-audio",
        };

    // 14 -> 15 for chess-opening-match, taken visibly as this shrink-only ceiling
    // requires. It is an operational lane of exactly the shape already listed here:
    // substrate-sourced, marker-gated, no manifest entry because the witness manifest
    // describes the foundation/knowledge ladder and this is a chess-modality pass, like
    // chess-syzygy and chess-trajectory beside it.
    // 15 -> 18 for the media-ladder lanes (frame-video / rgba-image / track-audio):
    // generic media format lanes, deliberately OUTSIDE the seed-cadence manifest —
    // no corpus seed is ordered for them (modality-ladder campaign law: identity
    // locks land first, seeds only on operator order), so operational-only is the
    // truthful classification until a media corpus enters the ladder.
    // 19 -> 20 for agents: AI-agent session logs (Claude Code, Codex, Gemini,
    // Antigravity, Copilot, Cursor, ...). Witness-unit lane whose boundary is an
    // operator-supplied path -- or, with no path, this user's own provider roots.
    // No witness-manifest entry because the seed cadence orders corpora under
    // DATA_ROOT and a personal session-log tree is neither a corpus nor orderable
    // by the ladder; the same classification code/repo/tabular already carry.
    // 18 -> 19 for chess-move-outcomes: substrate-sourced, marker-gated chess-modality
    // pass (the move-outcome fold onto the bounded MOVE vocabulary), the same shape and
    // the same reasoning as chess-opening-match/chess-eval/chess-trajectory above it.
    // 20 -> 21 for chess-transitions: the dedicated substrate-sourced backfill
    // for deterministic position transitions. Keeping it separate from analysis
    // prevents a transition upgrade from replaying unrelated testimony.
    // 22 -> 23 for model-corroborate: an operator-supplied analysis across two
    // already-deposited model snapshots. It consumes substrate/model state and is not
    // a seed-cadence source, so operational-only is the explicit classification.
    private const int OperationalOnlyRouteCeiling = 23;

    [Fact]
    public void RuntimeRoutes_MatchManifestPlusExplicitOperationalRoutes()
    {
        var manifestRoutes = ReadManifestRoutes();
        var runtimeRoutes = IngestDispatchTable.RegisteredKeys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = manifestRoutes.Except(runtimeRoutes, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var unclassified = runtimeRoutes
            .Except(manifestRoutes, StringComparer.OrdinalIgnoreCase)
            .Except(OperationalOnlyRoutes, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var stale = OperationalOnlyRoutes.Except(runtimeRoutes, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(missing.Count == 0,
            "Manifest sources missing from C# dispatch:\n  " + string.Join("\n  ", missing));
        Assert.True(unclassified.Count == 0,
            "Runtime routes absent from the manifest and operational allowlist:\n  "
            + string.Join("\n  ", unclassified));
        Assert.True(stale.Count == 0,
            "Operational-only routes no longer dispatch; remove stale entries:\n  "
            + string.Join("\n  ", stale));
        Assert.True(OperationalOnlyRoutes.Count <= OperationalOnlyRouteCeiling,
            $"{nameof(OperationalOnlyRoutes)} has {OperationalOnlyRoutes.Count} entries; "
            + $"shrink-only ceiling is {OperationalOnlyRouteCeiling}.");
    }

    private static HashSet<string> ReadManifestRoutes()
    {
        var path = Path.Combine(
            FindRepoRoot(), "scripts", "win", "witness-manifest.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in document.RootElement.GetProperty("cadence").EnumerateArray())
        {
            foreach (var source in stage.GetProperty("sources").EnumerateArray())
            {
                routes.Add(source.GetProperty("cli").GetString()
                    ?? throw new InvalidDataException("manifest source has null cli"));
            }
        }
        return routes;
    }

    private static string FindRepoRoot()
    {
        var stamped = typeof(IngestRosterParityTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "LaplaceRepoRoot")?.Value;
        if (stamped is not null && File.Exists(
                Path.Combine(stamped, "scripts", "win", "witness-manifest.json")))
            return stamped;
        throw new InvalidOperationException("Repository root metadata is missing");
    }
}
