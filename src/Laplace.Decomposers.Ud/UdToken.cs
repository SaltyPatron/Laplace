namespace Laplace.Decomposers.Ud;

using System.Collections.Generic;

/// <summary>
/// One token row from a CoNLL-U file. Universal Dependencies format —
/// 10 tab-separated fields per token. <c>_</c> is the conventional null marker.
/// </summary>
public sealed record UdToken(
    string Id,             // 1, 2, 3 (or 1-2 multi-word, or 1.1 enhanced)
    string Form,           // surface form
    string Lemma,
    string Upos,           // universal POS
    string Xpos,           // language-specific POS
    IReadOnlyDictionary<string, string> Feats,
    string Head,           // head token id (governing)
    string Deprel,         // dependency relation label
    string Deps,           // enhanced dependencies
    string Misc);

public sealed record UdSentence(
    string SentenceId,
    string Text,
    IReadOnlyList<UdToken> Tokens);
