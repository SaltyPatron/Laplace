using System.Globalization;
using System.Text;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Npgsql;
using Xunit;

namespace Laplace.Chess.Service.Tests;

/// <summary>
/// Phase A gate (Chess catalog dual): Cat/Act applied to chess paths.
/// Hilbert/centroid = locality prefilter only; Frechet on the realized position-coord
/// polyline (what <c>structural.entity_curve(line)</c> builds once a trajectory is deposited).
/// Never Frechet on packed <c>physicalities.trajectory</c>.
/// </summary>
// Tier=db, not "integration". EVERY test here needs a live PostgreSQL: the Frechet and
// angular-distance helpers take an NpgsqlConnection and the geometry they assert is
// computed server-side. CI's unit filter is `Tier!=perf & Tier!=db`, which "integration"
// passes — so this class ran in the unit lane and failed with 3D000 "database laplace does
// not exist" the moment the box was legitimately empty between a recreate and a seed.
// A test that cannot run without production data is not a unit test and must not gate one.
[Trait("Tier", "db")]
public sealed class ChessLineGeometryGateTests
{
    // Same QGD pair as ChessLineIdentityTests — path identity vs destination collision.
    private static readonly string[] QgdDirectSans = ["d4", "d5", "c4", "e6"];
    private static readonly string[] QgdTransposedSans = ["c4", "e6", "d4", "d5"];

    [Fact]
    public void ComposeTime_Transposition_FrechetSeparates_HilbertIsNotIdentity()
    {
        var direct = Walk(QgdDirectSans);
        var transposed = Walk(QgdTransposedSans);

        Assert.Equal(direct.PositionIds[^1], transposed.PositionIds[^1]);
        Assert.NotEqual(direct.LineId, transposed.LineId);

        // Line centroid Hilbert (locality) — may be close; must not be treated as same opening.
        var cA = Math4d.Centroid(Flatten(direct.Coords));
        var cB = Math4d.Centroid(Flatten(transposed.Coords));
        var hA = Hilbert128.Encode(cA);
        var hB = Hilbert128.Encode(cB);
        bool hilbertEqual = hA.CompareToBytewise(hB) == 0;

        using var conn = OpenLive();
        conn.Open();
        double ang = AngularDistance(conn, cA, cB);
        double frechet = FrechetCurves(conn, direct.Coords, transposed.Coords);
        double packedBogus = FrechetPackedTrajectory(conn, direct.PositionIds, transposed.PositionIds);

        // Gate: shape metric separates the paths. Locality alone must not admit "same line".
        Assert.True(frechet > 1e-6,
            $"expected Frechet separation on realized curves; got {frechet:G17} (ang={ang:G17})");
        Assert.NotEqual(direct.LineId, transposed.LineId);

        Console.WriteLine(
            "ChessLineGeometryGate QGD: "
            + $"frechet={frechet:G17} ang_centroids={ang:G17} "
            + $"hilbert_equal={hilbertEqual} packed_bogus={packedBogus:G17} "
            + $"n={direct.Coords.Count} lineA={ToHex(direct.LineId)} lineB={ToHex(transposed.LineId)} "
            + $"final={ToHex(direct.PositionIds[^1])}");
    }

    [SkippableFact]
    public void LiveDb_PositionCoordsMatchCompose_AndEntityCurveWhenPresent()
    {
        var direct = Walk(QgdDirectSans);
        var transposed = Walk(QgdTransposedSans);

        using var conn = OpenLive();
        conn.Open();

        // Point lookup on physicalities (entity_id), not v_word_points — the view join
        // is the wrong tool for an existence probe and can hang under load.
        var ids = direct.PositionIds.Concat(transposed.PositionIds).Distinct().Select(i => i.ToBytes()).ToArray();
        using (var cmd = new NpgsqlCommand(
                   """
                   SELECT count(DISTINCT entity_id)::int
                   FROM laplace.physicalities
                   WHERE entity_id = ANY(@ids)
                   """, conn))
        {
            cmd.Parameters.AddWithValue("ids", ids);
            cmd.CommandTimeout = 15;
            int found = (int)cmd.ExecuteScalar()!;
            Skip.If(found == 0,
                "no QGD path positions in live DB — seed chess-pgn/openings first; compose-time Frechet still gates");

            double composed = FrechetCurves(conn, direct.Coords, transposed.Coords);
            Assert.True(composed > 1e-6);

            double? curveFrechet = null;
            if (HasTrajectory(conn, direct.LineId) && HasTrajectory(conn, transposed.LineId))
            {
                using var c2 = new NpgsqlCommand(
                    """
                    SELECT public.laplace_frechet_4d(
                        structural.entity_curve(@a), structural.entity_curve(@b))
                    """, conn);
                c2.CommandTimeout = 30;
                c2.Parameters.AddWithValue("a", direct.LineId.ToBytes());
                c2.Parameters.AddWithValue("b", transposed.LineId.ToBytes());
                curveFrechet = (double)c2.ExecuteScalar()!;
                Assert.True(curveFrechet > 1e-6, $"entity_curve Frechet={curveFrechet}");
            }

            Console.WriteLine(
                $"LiveDb geometry: positions_found={found}/{ids.Length} composed_frechet={composed:G17} "
                + $"entity_curve_frechet={(curveFrechet?.ToString("G17", CultureInfo.InvariantCulture) ?? "<lines not deposited>")}");
        }
    }

    private static LineWalk Walk(IReadOnlyList<string> sans)
    {
        var m = new ChessModality();
        var state = m.Initial();
        var ids = new List<Hash128>();
        var coords = new List<double[]>();
        lock (ChessCompose.Gate)
        {
            var start = ChessCompose.Position(m.StateKey(state));
            ids.Add(start.Position.Id);
            coords.Add(start.Position.Coord);
            foreach (var san in sans)
            {
                var mv = San.Resolve(state.Board, m.LegalActions(state), san);
                Assert.NotNull(mv);
                state = m.Apply(state, mv!.Value);
                var composed = ChessCompose.Position(m.StateKey(state));
                ids.Add(composed.Position.Id);
                coords.Add(composed.Position.Coord);
            }
        }
        return new LineWalk(ChessCompose.LineId(ids.ToArray()), ids, coords);
    }

    private static double FrechetCurves(NpgsqlConnection conn, IReadOnlyList<double[]> a, IReadOnlyList<double[]> b)
    {
        using var cmd = new NpgsqlCommand(
            "SELECT public.laplace_frechet_4d(ST_GeomFromText(@a), ST_GeomFromText(@b))", conn);
        cmd.Parameters.AddWithValue("a", ToLineWkt(a));
        cmd.Parameters.AddWithValue("b", ToLineWkt(b));
        return (double)cmd.ExecuteScalar()!;
    }

    private static double AngularDistance(NpgsqlConnection conn, double[] a, double[] b)
    {
        using var cmd = new NpgsqlCommand(
            """
            SELECT public.laplace_angular_distance_4d(
                ST_MakePoint($1,$2,$3,$4), ST_MakePoint($5,$6,$7,$8))
            """, conn);
        cmd.Parameters.AddWithValue(a[0]);
        cmd.Parameters.AddWithValue(a[1]);
        cmd.Parameters.AddWithValue(a[2]);
        cmd.Parameters.AddWithValue(a[3]);
        cmd.Parameters.AddWithValue(b[0]);
        cmd.Parameters.AddWithValue(b[1]);
        cmd.Parameters.AddWithValue(b[2]);
        cmd.Parameters.AddWithValue(b[3]);
        return (double)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Rule #3 anti-pattern: Frechet on packed trajectory verts (id payload as XYZM).
    /// Kept only to show it is a different, non-shape number.
    /// </summary>
    private static double FrechetPackedTrajectory(
        NpgsqlConnection conn, IReadOnlyList<Hash128> a, IReadOnlyList<Hash128> b)
    {
        var ta = Trajectory.Build(a.ToArray());
        var tb = Trajectory.Build(b.ToArray());
        using var cmd = new NpgsqlCommand(
            "SELECT public.laplace_frechet_4d(ST_GeomFromText(@a), ST_GeomFromText(@b))", conn);
        cmd.Parameters.AddWithValue("a", PackedToWkt(ta));
        cmd.Parameters.AddWithValue("b", PackedToWkt(tb));
        return (double)cmd.ExecuteScalar()!;
    }

    private static bool HasTrajectory(NpgsqlConnection conn, Hash128 lineId)
    {
        using var cmd = new NpgsqlCommand(
            """
            SELECT EXISTS (
              SELECT 1 FROM laplace.physicalities
              WHERE entity_id = @id
                AND trajectory IS NOT NULL
                AND n_constituents >= 2)
            """, conn);
        cmd.CommandTimeout = 10;
        cmd.Parameters.AddWithValue("id", lineId.ToBytes());
        return cmd.ExecuteScalar() is true;
    }

    private static string ToLineWkt(IReadOnlyList<double[]> coords)
    {
        var sb = new StringBuilder("LINESTRING ZM (");
        for (int i = 0; i < coords.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var c = coords[i];
            sb.Append(Format(c[0])).Append(' ').Append(Format(c[1])).Append(' ')
              .Append(Format(c[2])).Append(' ').Append(Format(c[3]));
        }
        sb.Append(')');
        return sb.ToString();
    }

    private static string PackedToWkt(double[] xyzm)
    {
        var sb = new StringBuilder("LINESTRING ZM (");
        int n = xyzm.Length / 4;
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(", ");
            int o = i * 4;
            sb.Append(Format(xyzm[o])).Append(' ').Append(Format(xyzm[o + 1])).Append(' ')
              .Append(Format(xyzm[o + 2])).Append(' ').Append(Format(xyzm[o + 3]));
        }
        sb.Append(')');
        return sb.ToString();
    }

    private static string Format(double v) => v.ToString("G17", CultureInfo.InvariantCulture);

    private static double[] Flatten(IReadOnlyList<double[]> coords)
    {
        var flat = new double[coords.Count * 4];
        for (int i = 0; i < coords.Count; i++)
        {
            flat[i * 4] = coords[i][0];
            flat[i * 4 + 1] = coords[i][1];
            flat[i * 4 + 2] = coords[i][2];
            flat[i * 4 + 3] = coords[i][3];
        }
        return flat;
    }

    private static string ToHex(Hash128 id) => Convert.ToHexString(id.ToBytes()).ToLowerInvariant();

    private static NpgsqlConnection OpenLive()
        => new(LaplaceInstall.PostgresConnectionString());

    private sealed record LineWalk(Hash128 LineId, List<Hash128> PositionIds, List<double[]> Coords);
}
