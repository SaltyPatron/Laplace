using System.Text;
using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>
/// The conversation surfaces' transport to the one forward program: the Engine command a turn runs and
/// the receipt it reads back (Laplace-Engine <c>laplace turn|said --receipt</c>). The turn itself, its
/// answer and its witnessing, is the Engine's, and the deployed pipeline checks it against a database.
/// </summary>
public sealed class NativeTurnReceiptTests
{
    private static NativeRecipeExecution.TurnReceiptReader Read(Action<NativeTurnStep>? step, params string[] lines)
    {
        var reader = new NativeRecipeExecution.TurnReceiptReader("carol", "geography", step);
        foreach (var line in lines) reader.Read(Encoding.UTF8.GetBytes(line));
        return reader;
    }

    private const string Resolve = """{"event":"resolve","prompt":"81656e6181e88c29c678b45e04cdb94b","user":"carol","session":"geography","ordinal":2,"turns_before":1}""";

    [Fact]
    public void AWitnessedTurnReadsBackItsStepsResponseAndPlace()
    {
        var steps = new List<NativeTurnStep>();
        var receipt = Read(steps.Add, Resolve,
            """{"event":"step","step":1,"kind":"answers","id":"24378f1b1b7ddfb2c6c05d7eed02bd7c","text":"a \"pet\"\n","grounds":2,"continuity":0,"confidence":0.0849425}""",
            """{"event":"step","step":2,"kind":"follows","id":"00000000000000000000000000000002","text":" dog","grounds":1,"continuity":1,"confidence":0.5}""",
            """{"event":"realize","text":"a \"pet\"\n dog","disposition":"complete: every obligation is grounded"}""",
            """{"event":"witness","witnessed":true,"turn":"0123456789abcdef0123456789abcdef","response":"fedcba9876543210fedcba9876543210","ordinal":2,"entities":7}""")
            .Receipt("RESOLVE    ...\n");
        Assert.Equal(2, steps.Count);
        Assert.Equal("a \"pet\"\n", steps[0].Text);
        Assert.Equal("follows", steps[1].Kind);
        Assert.Equal("a \"pet\"\n dog", receipt.Response);
        Assert.True(receipt.Witnessed);
        Assert.Equal("0123456789abcdef0123456789abcdef", receipt.TurnId);
        Assert.Equal("fedcba9876543210fedcba9876543210", receipt.ResponseId);
        Assert.Equal(2, receipt.Ordinal);
        Assert.Equal(1, receipt.TurnsBefore);
        Assert.Equal("carol", receipt.Participant);
        Assert.Equal("RESOLVE    ...\n", receipt.Trace);
    }

    [Fact]
    public void ATurnHeldOrNotWitnessedSaysWhy()
    {
        var held = Read(null,
            """{"event":"resolve","prompt":"81656e6181e88c29c678b45e04cdb94b","response":"fedcba9876543210fedcba9876543210","user":"carol","session":"geography","ordinal":1,"turns_before":1}""",
            """{"event":"witness","witnessed":false,"held":1,"turn":"0123456789abcdef0123456789abcdef","reason":"the turn is held by the session: nothing is witnessed again"}""")
            .Receipt("");
        Assert.False(held.Witnessed);
        Assert.Equal(1, held.Held);
        Assert.Equal("fedcba9876543210fedcba9876543210", held.ResponseId);
        Assert.Contains("held by the session", held.Reason);

        var read = Read(null, Resolve,
            """{"event":"realize","text":"","disposition":"unresolved: what follows grounds nothing that is owed"}""",
            """{"event":"witness","witnessed":false,"held":null,"turn":null,"reason":"a read: nothing is witnessed"}""")
            .Receipt("");
        Assert.False(read.Witnessed);
        Assert.Null(read.Held);
        Assert.Null(read.TurnId);
        Assert.Empty(read.Steps);
    }

    [Fact]
    public void AReceiptCutOffOrNamingAnUndeclaredEventIsRefused()
    {
        // An Engine that exits before it says whether the turn was witnessed has not recorded a turn.
        Assert.Throws<InvalidDataException>(() => Read(null, Resolve).Receipt(""));
        Assert.Throws<InvalidDataException>(() => Read(null,
            """{"event":"witness","witnessed":true,"turn":"0123456789abcdef0123456789abcdef","response":"fedcba9876543210fedcba9876543210","ordinal":1,"entities":1}""").Receipt(""));
        Assert.Throws<InvalidDataException>(() => Read(null, Resolve, """{"event":"recall"}"""));
    }

    [Fact]
    public void ATurnRunsTheEnginesTurnAndASaidTurnItsSaid()
    {
        var execution = NativeRecipeExecution.Configured();
        var turn = execution.TurnStartInfo("host=x dbname=y", "carol", "geography", null, read: false,
            "prompt.txt", null, "receipt.jsonl").ArgumentList.ToArray();
        Assert.Equal("turn", turn[0]);
        Assert.Equal(new[] { "--firmware", execution.Firmware, "--as", "carol", "--session", "geography",
            "--files", "--receipt", "receipt.jsonl", "prompt.txt" }, turn[1..]);

        var read = execution.TurnStartInfo("host=x dbname=y", "carol", "geography", null, read: true,
            "prompt.txt", null, "receipt.jsonl").ArgumentList.ToArray();
        Assert.Contains("--read", read);

        var said = execution.TurnStartInfo("host=x dbname=y", "carol", "geography", "conventional model",
            read: false, "prompt.txt", "response.txt", "receipt.jsonl");
        var arguments = said.ArgumentList.ToArray();
        Assert.Equal("said", arguments[0]);
        Assert.Equal(new[] { "--by", "conventional model" }, arguments[7..9]);
        Assert.Equal(new[] { "prompt.txt", "response.txt" }, arguments[^2..]);
        Assert.Equal("host=x dbname=y", said.Environment["LAPLACE_CONNINFO"]);

        // A said turn has both its responder and its response, and is witnessed.
        Assert.Throws<ArgumentException>(() => execution.TurnStartInfo("host=x dbname=y", "carol", "geography",
            "model", read: false, "prompt.txt", null, "receipt.jsonl"));
        Assert.Throws<ArgumentException>(() => execution.TurnStartInfo("host=x dbname=y", "carol", "geography",
            "model", read: true, "prompt.txt", "response.txt", "receipt.jsonl"));
    }
}
