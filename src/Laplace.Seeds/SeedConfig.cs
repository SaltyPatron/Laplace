namespace Laplace.Seeds;

using System.Collections.Generic;

/// <summary>
/// Paths to canonical foundational + secondary seed corpora on disk.
/// All paths are absolute. Empty / null paths cause the corresponding
/// decomposer to be skipped (the orchestrator does not synthesize data).
///
/// Phase 3+4 / Track E + Track F4.
/// </summary>
public sealed record SeedConfig(
    string                Iso639Directory,
    string                WordnetDictionaryDirectory,
    string                OmwRoot,
    string                TatoebaDirectory,
    string                UdTreebanksRoot,
    IReadOnlyList<string> AtomicTsvFiles,
    string                WiktionaryJsonlFile);
