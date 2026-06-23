using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.OMW;

/// <summary>
/// Registers OMW's existing <see cref="OMWGrammarWitness"/> with the generic ETL engine so the one
/// <see cref="EtlDecomposer"/> drives the real OMW Transform (ILI synset anchor + per-file language)
/// through the single <see cref="StructuredGrammarIngest"/> pipeline — the parity oracle. The file
/// language is derived per-file from the path exactly as <see cref="OMWDecomposer"/> does.
/// </summary>
public static class OMWEtlRegistration
{
    public static void Register() =>
        EtlWitnessFactory.Register(
            "OMWDecomposer",
            ctx => new OMWGrammarWitness(OMWTabFiles.FileLang(ctx.FilePath)),
            () => OMWDecomposer.LanguageNames.Keys.ToArray());
}
