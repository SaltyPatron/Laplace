using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Tests.Structured;

public sealed class UnicodeSourceSemanticsTests
{
    [Theory]
    [InlineData(17, 1)]
    [InlineData(17, 4)]
    [InlineData(127, 1)]
    [InlineData(127, 4)]
    [InlineData(8388608, 1)]
    [InlineData(8388608, 4)]
    public async Task EmojiRecordsRetainProductAndReplaceInheritedHeaders(int window, int workers)
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write("emoji-test.txt", """
            # group: Smileys & Emotion
            # subgroup: face-smiling
            1F600 ; fully-qualified # 😀 E1.0 grinning face
            1F601 ; fully-qualified # 😁 E0.6 beaming face with smiling eyes

            # ordinary packaging comment
            # subgroup: face-affection
            1F642 ; unqualified # 🙂 E1.0 slightly smiling face
            # group: People & Body
            # subgroup: person-role
            1F469 200D 1F4BB ; fully-qualified # 👩‍💻 E4.0 woman technologist
            # group: Objects
            1F4A1 ; fully-qualified # 💡 E0.6 light bulb
            """);
        var baseline = await EmojiClaims(fixture, path, 8388608, 1);
        var actual = await EmojiClaims(fixture, path, window, workers);
        Assert.Equal(baseline.Claims().Order(StringComparer.Ordinal), actual.Claims().Order(StringComparer.Ordinal));
        string[] expected =
        [
            "😀\tSmileys & Emotion", "😀\tface-smiling", "😀\tfully-qualified", "😀\temoji name\tgrinning face", "😀\temoji version\tE1.0",
            "😁\tSmileys & Emotion", "😁\tface-smiling", "😁\tfully-qualified", "😁\temoji name\tbeaming face with smiling eyes", "😁\temoji version\tE0.6",
            "🙂\tSmileys & Emotion", "🙂\tface-affection", "🙂\tunqualified", "🙂\temoji name\tslightly smiling face", "🙂\temoji version\tE1.0",
            "👩‍💻\tPeople & Body", "👩‍💻\tperson-role", "👩‍💻\tfully-qualified", "👩‍💻\temoji name\twoman technologist", "👩‍💻\temoji version\tE4.0",
            "💡\tObjects", "💡\tfully-qualified", "💡\temoji name\tlight bulb", "💡\temoji version\tE0.6"
        ];
        Assert.Equal(expected.Select(claim => "claim\t0\t1\t" + claim).Order(StringComparer.Ordinal), actual.Claims().Order(StringComparer.Ordinal));
        Assert.Equal(5, actual.Lines("record").Length);
        // Same actual product records and Merkle identities, regardless of
        // parser windows and parallel scheduling. Headers never become rows.
        Assert.Equal(baseline.Lines("record").Order(StringComparer.Ordinal), actual.Lines("record").Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("1F600 ; bogus # 😀 E1.0 grinning face")]
    [InlineData("1F600 ; fully-qualified # 😀 not-a-version grinning face")]
    [InlineData("1F600 ; fully-qualified # 😀 E1.0")]
    [InlineData("1F600 ; fully-qualified")]
    public async Task EmojiMalformedProductRowsAreRejected(string row)
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write("emoji-test.txt", "# group: Smileys & Emotion\n# subgroup: face-smiling\n" + row + "\n");
        var result = await EmojiClaims(fixture, path, 17, 4);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not parse completely", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Lines("claim"));
    }

    private static async Task<NativeCorpus.Result> EmojiClaims(NativeCorpus fixture, string path, int window, int workers)
    {
        // Set the child environment, not shared process-wide state. This uses
        // the same request builder and Engine process as the installed caller.
        var start = fixture.Execution.StartInfo(new(NativeRecipeMode.Claims, "unicode", [path],
            "host=127.0.0.1 port=1 dbname=unavailable connect_timeout=1", workers));
        start.Environment["LAPLACE_RECORD_WINDOW_BYTES"] = window.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.StandardOutputEncoding = System.Text.Encoding.UTF8;
        start.StandardErrorEncoding = System.Text.Encoding.UTF8;
        using var process = System.Diagnostics.Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new(process.ExitCode, await output, await error);
    }

    [Theory]
    [InlineData("F900", "kCompatibilityVariant", "U+8C48", "canonical Decomposition_Mapping value for the ideograph", "8C48")]
    [InlineData("3400", "kSemanticVariant", "U+4E18", "semantic variant", "4E18")]
    [InlineData("349A", "kSpecializedSemanticVariant", "U+6587<kFenn", "specialized semantic variant", "6587")]
    [InlineData("346E", "kSimplifiedVariant", "U+2B748", "simplified Chinese variant(s)", "2B748")]
    [InlineData("3437", "kTraditionalVariant", "U+508C", "traditional Chinese variant(s)", "508C")]
    [InlineData("340A", "kSpoofingVariant", "U+340B", "spoofing variants", "340B")]
    [InlineData("3588", "kZVariant", "U+439B", "z-variants", "439B")]
    public async Task UnihanReferencesNameTheirActualScalarTargets(
        string subject, string property, string value, string relation, string target)
    {
        // Bounded rows from the installed Unicode17 Unihan estate. Their XML
        // attribute is packaging; neither U+ notation nor dictionary names
        // become part of the target character's identity.
        using var fixture = new NativeCorpus();
        string path = fixture.Write("ucd.all.flat.xml", $"<ucd><repertoire><char cp=\"{subject}\" "
            + $"{property}=\"{value.Replace("<", "&lt;", StringComparison.Ordinal)}\"/></repertoire></ucd>");
        var result = await fixture.Claims("unicode", path);
        static string Scalar(string hex) => char.ConvertFromUtf32(Convert.ToInt32(hex, 16));
        Assert.Equal(new[] { $"claim\t0\t1\t{Scalar(subject)}\t{relation}\t{Scalar(target)}" }, result.Claims());
        if (value.Contains('<')) Assert.Contains("kFenn", string.Join('\n', result.Lines("record")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DictionaryAndUsageQualifiersStayWithTheirOwnVariantJudgments()
    {
        using var fixture = new NativeCorpus();
        // UAX38 revision39 examples, plus every defined usage flag. The three
        // dictionary observations of the same target share a canonical claim.
        string path = fixture.Write("ucd.all.flat.xml", """
            <ucd><repertoire>
              <char cp="792E" kSemanticVariant="U+70AE&lt;kMeyerWempe U+7832&lt;kLau,kMatthews,kMeyerWempe U+791F&lt;kLau,kMatthews"/>
              <char cp="3A17" kSemanticVariant="U+6377&lt;kHanYu:TZBFJ"/>
            </repertoire></ucd>
            """);
        var parallel = await fixture.Claims("unicode", path, 4);
        var serial = await fixture.Claims("unicode", path, 1);
        Assert.Equal(serial.Lines("record").Order(StringComparer.Ordinal), parallel.Lines("record").Order(StringComparer.Ordinal));
        string[] claims = parallel.Claims();
        Assert.Equal(7, claims.Length);
        Assert.Single(claims, claim => claim == "claim\t0\t1\t礮\tsemantic variant\t炮");
        Assert.Equal(3, claims.Count(claim => claim == "claim\t0\t1\t礮\tsemantic variant\t砲"));
        Assert.Equal(2, claims.Count(claim => claim == "claim\t0\t1\t礮\tsemantic variant\t礟"));
        Assert.Contains("claim\t0\t1\t㨗\tsemantic variant\t捷", claims);
        // The file's outer whitespace may have its own packaging record.
        // These are the two character product records carrying judgments.
        string[] records = parallel.Lines("record").Where(record => record.Contains("semantic variant", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, records.Length);
        Assert.Contains(records, record => record.Contains("kHanYuTZBFJ㨗semantic variant捷", StringComparison.Ordinal));
        Assert.Contains(records, record => record.Contains("kLau礮semantic variant砲kMatthews礮semantic variant砲kMeyerWempe礮semantic variant砲", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("kMatthews")]
    [InlineData("U+4E18<")]
    [InlineData("U+4E18<kMatthews:Q")]
    [InlineData("U+4E18<kMatthews,")]
    public async Task MalformedQualifiedReferencesFailBeforeAnyDatabaseWork(string reference)
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write("ucd.all.flat.xml", "<ucd><repertoire><char cp=\"3400\" kSemanticVariant=\""
            + reference.Replace("<", "&lt;", StringComparison.Ordinal) + "\"/></repertoire></ucd>");
        var result = await fixture.Claims("unicode", path);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not parse completely", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Lines("claim"));
    }

    [Fact]
    public async Task LinkFormattingPreservesPairsAndUrlFragments()
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write("LinkFormattingTest.txt",
            "# comment\nhttps://example.com/#%CE%B1\n\n# between pair\nhttps://example.com/#α\n");
        Assert.Equal(new[] { "claim\t0\t1\thttps://example.com/#%CE%B1\tnormalizes to\thttps://example.com/#α" },
            (await fixture.Claims("unicode", path)).Claims());
    }

    [Fact]
    public async Task LinkFormattingRejectsAnIncompletePair()
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write("LinkFormattingTest.txt", "https://example.com/%CE%B1\n");
        var result = await fixture.Claims("unicode", path);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not parse completely", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinkAnnotationsRetainRepeatedSpansAndUnicodePositions()
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write("LinkDetectionTest.txt", "é ⸠a.com#x⸡ ⸠a.com#x⸡\nno link here\n");
        var result = await fixture.Claims("unicode", path);
        string[] claims=result.Claims();
        Assert.Equal(2,claims.Length);
        string records = string.Join('\n', claims.Concat(result.Lines("held")).Concat(result.Lines("record")));
        Assert.Contains("é a.com#x a.com#x", records, StringComparison.Ordinal);
        Assert.Contains(claims,line => line.Split('\t')[3]=="[é a.com#x a.com#x, 2, 8]");
        Assert.Contains(claims,line => line.Split('\t')[3]=="[é a.com#x a.com#x, 10, 16]");
        Assert.Contains("no link here", records, StringComparison.Ordinal);
        Assert.DoesNotContain("⸠", records, StringComparison.Ordinal);
        Assert.DoesNotContain("⸡", records, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("⸠unterminated")]
    [InlineData("unopened⸡")]
    [InlineData("⸠nested ⸠link⸡⸡")]
    public async Task MalformedLinkAnnotationsAreRejected(string source)
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write("LinkDetectionTest.txt", source + "\n");
        var result = await fixture.Claims("unicode", path);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not parse completely", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LinkBracket.txt", "0029 ; 0028 # closing to opening\n", "claim\t0\t1\t)\tLink_Bracket\t(")]
    [InlineData("LinkEmail.txt", "# @missing: 0000..10FFFF; No\n0041..0042 # range\n", "claim\t0\t1\t[A, B]\tLink_Email")]
    [InlineData("LinkTerm.txt", "# @missing: 0000..10FFFF; Include\n0021 ; Soft\n", "claim\t0\t1\t!\tLink_Term\tSoft")]
    [InlineData("LinkTerm.txt", "0041..0042 ; Soft\n", "claim\t0\t1\t[A, B]\tLink_Term\tSoft")]
    public async Task LinkPropertiesRetainTheirScalarsRangeBoundsAndSourcePropertyNames(
        string name, string content, string expected)
    {
        using var fixture = new NativeCorpus();
        string path = fixture.Write(name, content);
        Assert.Equal(new[] { expected }, (await fixture.Claims("unicode", path)).Claims());
    }
}
