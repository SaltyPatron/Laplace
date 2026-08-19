using Laplace.Engine.Core;

namespace Laplace.Decomposers.UD;

public sealed record UdSentence(
    byte[]? TextUtf8,
    IReadOnlyList<UdToken> Tokens,
    IReadOnlyList<UdMwt> Mwts,
    int MaxId,
    string? SourceSentenceId = null,
    long SourceOrdinal = 0);

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
    string Misc,
    bool HeadSpecified = true);

public readonly record struct UdMwt(int Start, int End, byte[] FormUtf8, string Misc = "_");

public readonly record struct UdIngestRecord(UdSentence Sentence, Hash128 LangId, string LangCode);
