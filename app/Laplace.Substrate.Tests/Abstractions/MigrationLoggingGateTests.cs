using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// DbUp/postgresql may pass a complete exception string as the logger's format argument
/// with no format arguments. PostgreSQL detail text is arbitrary data and can contain
/// braces, so migration diagnostics must not route through DbUp's ConsoleUpgradeLog,
/// which unconditionally calls string.Format(format, args). A logger failure must never
/// replace the database failure that stopped delivery.
/// </summary>
public class MigrationLoggingGateTests
{
    private static string Root => TypeIdLawTests.FindRepoRootPublic();

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([Root, .. parts]));

    [Fact]
    public void MigrationEngine_UsesLiteralSafeDbUpLogger()
    {
        var program = Read("app", "Laplace.Migrations", "Program.cs");
        var logger = Read("app", "Laplace.Migrations", "LiteralSafeUpgradeLog.cs");

        Assert.Contains(".LogTo(new LiteralSafeUpgradeLog())", program);
        Assert.DoesNotContain(".LogToConsole()", program);
        Assert.Contains("if (args.Length == 0)\n            return format;", logger);
        Assert.Contains("catch (FormatException)", logger);
        Assert.Contains("Logging must never replace the operational failure", logger);
    }
}
