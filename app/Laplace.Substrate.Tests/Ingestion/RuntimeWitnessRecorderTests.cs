using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>
/// The transport of a runtime witness's statements to the Engine (<c>laplace witness</c>): the statement file's
/// text, where it is written, and the request the Engine is given. What the Engine records is its own, and the
/// deployed pipeline checks it against the database.
/// </summary>
public sealed class RuntimeWitnessRecorderTests
{
    [Fact]
    public void AStatementWritesEachPartAsTheTextItIs()
    {
        string text = RuntimeWitnessRecorder.StatementText(
        [
            new WitnessStatement("dog", "is a", "animal", 1),
            new WitnessStatement("def f():\n    return 1\tx \\ y", "compiles", "", 0),
            new WitnessStatement("water", "is", "wet", 0.5),
        ]);
        Assert.Equal("dog\tis a\tanimal\t1\n"
            + "def f():\\\n    return 1\\\tx \\\\ y\tcompiles\t\t0\n"
            + "water\tis\twet\t0.5\n", text);

        Assert.Throws<ArgumentException>(() => RuntimeWitnessRecorder.StatementText([new WitnessStatement("a", "", "b", 1)]));
        Assert.Throws<ArgumentException>(() => RuntimeWitnessRecorder.StatementText([new WitnessStatement("a", "is", "b", 1.5)]));
        Assert.Throws<ArgumentException>(() => RuntimeWitnessRecorder.StatementText([new WitnessStatement("a", "is", "b", double.NaN)]));
    }

    [Fact]
    public async Task TheWitnessStatesAFileNamedByItsBytesAndTheEngineReportsItsTrunks()
    {
        string root = Path.Combine(Path.GetTempPath(), "laplace-witness-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var requests = new List<NativeRecipeRequest>();
            var recorder = new RuntimeWitnessRecorder("Host=h;Port=5432;Database=d;Username=u", root,
                (request, output, error, progress, ct) =>
                {
                    requests.Add(request);
                    progress(new NativeRecipeProgress("file-composed", null, 1, 1, 1, 1, 1, request.Paths[0], 1, 1,
                        "0123456789abcdef0123456789abcdef", null));
                    progress(new NativeRecipeProgress("complete", null, 1, 1, 1, 1, 1, null, 0, 1,
                        null, "fedcba9876543210fedcba9876543210"));
                    return Task.FromResult(0);
                });
            var witness = new NativeRuntimeWitness("carol", "UserPromptContent");
            var first = await recorder.StateAsync(witness, [new WitnessStatement("dog", "is a", "animal", 1)]);
            var again = await recorder.StateAsync(witness, [new WitnessStatement("dog", "is a", "animal", 1)]);

            byte[] bytes = Encoding.UTF8.GetBytes("dog\tis a\tanimal\t1\n");
            string expected = Path.Combine(root, Convert.ToHexStringLower(Hash128.Blake3(bytes).ToBytes()), "witness.statement");
            Assert.Equal(expected, first.Path);
            Assert.Equal(first.Path, again.Path);
            Assert.Equal(bytes, File.ReadAllBytes(first.Path));
            Assert.Equal("fedcba9876543210fedcba9876543210", first.WitnessId);
            Assert.Equal("0123456789abcdef0123456789abcdef", first.FileId);
            var request = requests[0];
            Assert.Equal(NativeRecipeMode.Ingest, request.Mode);
            Assert.Null(request.Source);
            Assert.False(request.ExpandPrerequisites);
            Assert.Equal(witness, request.Witness);
            Assert.Equal(new[] { expected }, request.Paths);

            var failing = new RuntimeWitnessRecorder("Host=h;Port=5432;Database=d;Username=u", root,
                (request, output, error, progress, ct) =>
                {
                    error.Write("statement refused"u8);
                    return Task.FromResult(1);
                });
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                failing.StateAsync(witness, [new WitnessStatement("a", "b", "c", 0)]));
            Assert.Contains("statement refused", refused.Message);
            await Assert.ThrowsAsync<ArgumentException>(() => recorder.RecordAsync(witness, "../note.txt", [1]));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
