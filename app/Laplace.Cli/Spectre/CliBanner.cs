using Spectre.Console;

namespace Laplace.Cli.Spectre;

/// <summary>
/// The startup banner: a Figlet "Laplace" whose lines run through a color gradient, over a
/// one-line tagline. Rendered only for bare/help invocations on a terminal, never before a
/// command runs, so piped output stays clean.
/// </summary>
internal static class CliBanner
{
    // Cool blue → violet, the web interface's accent ramp.
    private static readonly Color[] Gradient =
    {
        new(0x38, 0xbd, 0xf8), new(0x60, 0x9c, 0xf5), new(0x81, 0x7c, 0xf0),
        new(0xa2, 0x5c, 0xeb), new(0xc0, 0x3e, 0xe6),
    };

    public static void Render()
    {
        if (Console.IsOutputRedirected) return; // piped/scripted: no decoration

        var fig = new FigletText("Laplace").LeftJustified();
        // FigletText renders in one color: the block takes the ramp's midpoint and the
        // tagline Rule carries its start.
        fig.Color = Gradient[2];
        AnsiConsole.Write(fig);
        AnsiConsole.Write(new Rule("[italic]content-addressable geometric-attestation substrate[/]")
            .RuleStyle(new Style(Gradient[0]))
            .LeftJustified());
        AnsiConsole.WriteLine();
    }
}
