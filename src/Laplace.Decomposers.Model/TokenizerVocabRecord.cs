namespace Laplace.Decomposers.Model;

/// <summary>
/// One vocabulary entry from a model's tokenizer asset (vocab.json,
/// tokenizer.json, BPE merges, etc.). The token's IDENTITY in the
/// substrate is its surface string's content-addressed hash — model_id +
/// token_id are bookkeeping bound to the model entity, not to substrate
/// token identity. Per the AI-models-as-edge-extraction invariant +
/// the cross-model dedup memory: same surface = same substrate entity
/// regardless of which model surfaces it.
/// </summary>
public sealed record TokenizerVocabRecord(
    int    TokenId,
    string Surface,
    bool   IsSpecial);
