using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.OMW;

public static class OMWEtlRegistration
{
    public static void Register() =>
        EtlWitnessFactory.Register(
            "OMWDecomposer",
            ctx => new OMWGrammarWitness(OMWTabFiles.FileLang(ctx.FilePath)),
            () => OMWDecomposer.LanguageNames.Keys.ToArray());
}
