using System.Text.Json;
using Xunit;
using Laplace.Decomposers.Model;

namespace Laplace.Decomposers.Model.Tests;

/// <summary>
/// Plan Phase 3 gate (2026-07-08): the foundry now ships the tokenizer.json as
/// type "BPE" with trained merges instead of "WordLevel". The token→entity-hash
/// contract must be invariant under that switch — a vocab entry's EntityId
/// derives from its canonical form only, never from the model type — and the
/// merges must round-trip through ParseMerges.
/// </summary>
public class BpeTokenizerRoundTripTests
{
    // Token entity ids resolve through the codepoint perfcache; loading here (the
    // assembly's per-class idiom) removes the order dependence on another test
    // class having loaded the process-global blob first.
    static BpeTokenizerRoundTripTests() =>
        Laplace.Engine.Core.CodepointPerfcache.Load(
            Laplace.Decomposers.Tests.TestInstall.ResolvePerfcacheOrThrow());

    private static string WriteTokenizer(string dir, string modelType, bool withMerges)
    {
        var vocab = new Dictionary<string, int>
        {
            ["<unk>"] = 0, ["<s>"] = 1, ["</s>"] = 2,
            ["<0x41>"] = 3,
            ["▁the"] = 4, ["the"] = 5,
            ["▁dog"] = 6, ["dog"] = 7,
            ["▁do"] = 8,
        };
        var model = new Dictionary<string, object?>
        {
            ["type"] = modelType,
            ["unk_token"] = "<unk>",
            ["vocab"] = vocab,
        };
        if (withMerges)
        {
            model["ignore_merges"] = true;
            // byte-encoded alphabet ("Ġ" = space), rank order — chains rebuild "Ġdog"
            model["merges"] = new[] { "Ġ d", "Ġd o", "Ġdo g", "t h", "th e", "Ġ the" };
        }
        var doc = new Dictionary<string, object?>
        {
            ["version"] = "1.0",
            ["added_tokens"] = new[]
            {
                new Dictionary<string, object?> { ["id"] = 0, ["content"] = "<unk>", ["special"] = true },
                new Dictionary<string, object?> { ["id"] = 1, ["content"] = "<s>", ["special"] = true },
                new Dictionary<string, object?> { ["id"] = 2, ["content"] = "</s>", ["special"] = true },
            },
            ["model"] = model,
        };
        string path = Path.Combine(dir, "tokenizer.json");
        File.WriteAllText(path, JsonSerializer.Serialize(doc));
        return path;
    }

    [Fact]
    public void EntityIds_Invariant_Between_WordLevel_And_Bpe_Emission()
    {
        string dirA = Directory.CreateTempSubdirectory("lap-tok-wl-").FullName;
        string dirB = Directory.CreateTempSubdirectory("lap-tok-bpe-").FullName;
        try
        {
            var wordLevel = LlamaTokenizerParser.Parse(WriteTokenizer(dirA, "WordLevel", withMerges: false));
            var bpe = LlamaTokenizerParser.Parse(WriteTokenizer(dirB, "BPE", withMerges: true));

            var idsWl = wordLevel.OrderBy(t => t.TokenId).Select(t => (t.TokenId, t.EntityId)).ToArray();
            var idsBpe = bpe.OrderBy(t => t.TokenId).Select(t => (t.TokenId, t.EntityId)).ToArray();

            Assert.Equal(idsWl.Length, idsBpe.Length);
            Assert.Equal(idsWl, idsBpe);
        }
        finally
        {
            Directory.Delete(dirA, recursive: true);
            Directory.Delete(dirB, recursive: true);
        }
    }

    [Fact]
    public void TrainedMerges_RoundTrip_Through_ParseMerges()
    {
        string dir = Directory.CreateTempSubdirectory("lap-tok-mrg-").FullName;
        try
        {
            string path = WriteTokenizer(dir, "BPE", withMerges: true);
            var merges = LlamaTokenizerParser.ParseMerges(path);
            Assert.Equal(6, merges.Count);
            // "Ġ the" canonicalizes: left = space (byte-decoded), right = "the"
            var (left, right) = merges[^1];
            Assert.Equal(" "u8.ToArray(), left);
            Assert.Equal("the"u8.ToArray(), right);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
