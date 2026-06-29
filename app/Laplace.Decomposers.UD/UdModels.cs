using Laplace.Engine.Core;

namespace Laplace.Decomposers.UD;

public sealed record UdSentence(
    byte[]? TextUtf8,
    IReadOnlyList<UdToken> Tokens,
    IReadOnlyList<UdMwt> Mwts,
    int MaxId);

public readonly record struct UdToken(
    int Id,
    string Ref,
    byte[] FormUtf8,
    byte[] LemmaUtf8,
    bool FormLemmaSame,
    string Upos,
    string Xpos,
    string[] Feats,
    int Head,
    string Deprel,
    string Deps,
    string Misc);

public readonly record struct UdMwt(int Start, int End, byte[] FormUtf8);

public readonly record struct UdIngestRecord(UdSentence Sentence, Hash128 LangId, string LangCode);
