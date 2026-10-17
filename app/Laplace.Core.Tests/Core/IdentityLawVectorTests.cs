using System.Text;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

/// <summary>
/// The identity law (Laplace-Wiki Storage/Identity.md) against fixed IDs that Laplace-Native's
/// lp_id_codepoint/lp_id_compose produce and the Engine records: a codepoint is BLAKE3-128 of its
/// UTF-8 bytes, and a composition is BLAKE3-128 of its children's IDs in order and nothing else,
/// with no domain byte. 'A' and 'Holmes' are Laplace-Native's prototype goldens (tests/test_identity.c);
/// the rest were read from the Engine's database. A monorepo ID that differs from these means the
/// monorepo and the Engine name the same content differently, and no source can be compared or merged
/// across them (Laplace#1885).
/// </summary>
public sealed class IdentityLawVectorTests
{
    private static Hash128 Codepoint(string c) => Hash128.Blake3(Encoding.UTF8.GetBytes(c));

    private static unsafe Hash128 Text(string s)
    {
        var kids = new Hash128[s.Length];
        for (int i = 0; i < s.Length; i++) kids[i] = Codepoint(s[i].ToString());
        if (kids.Length == 1) return kids[0];
        Hash128 id;
        fixed (Hash128* p = kids) NativeInterop.Hash128Merkle(2, p, (nuint)kids.Length, &id);
        return id;
    }

    [Theory]
    [InlineData("A", "32684bfa28c0c84d6f210511aace0efc")]
    [InlineData("Aa", "a86759f63fc23d64b52593706fc2b3af")]
    [InlineData("eng", "29e09d41298b25c17362361f470282a9")]
    [InlineData("fra", "1839744b45311de68153a4ce6e36dece")]
    [InlineData("English", "6c181714855eac49e04ac85ef40e6bf1")]
    [InlineData("French", "e0ee66aaca83a6a9bf3f6e978abce38b")]
    [InlineData("Holmes", "5b7e40e9a23ae55eaccd45646934977d")]
    public void TheMonorepoNamesContentAsLaplaceNativeDoes(string text, string expected)
        => Assert.Equal(expected, Text(text).ToHex());
}
