namespace Laplace.Decomposers.Tatoeba;

/// <summary>One row from Tatoeba's sentences.csv (id\tlang\ttext).</summary>
public sealed record TatoebaSentenceRecord(long Id, string LanguageIso6393, string Text);

/// <summary>One row from Tatoeba's links.csv (source_id\ttarget_id) — parallel sentence pair.</summary>
public sealed record TatoebaLinkRecord(long SourceId, long TargetId);
