using System.Runtime.CompilerServices;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

/// <summary>The chess lanes that calculate rather than observe: engine analysis and
/// evaluation, and outcome/transition tallies derived from recorded games. Their claims
/// carry derivation/calculation from the moment this module loads.</summary>
internal static class ChessCalculationSources
{
    [ModuleInitializer]
    internal static void Register()
    {
        CalculationSources.Register(ChessVocabulary.AnalysisSourceId);
        CalculationSources.Register(ChessVocabulary.TrajectorySourceId);
        CalculationSources.Register(ChessVocabulary.OpeningMatchSourceId);
        CalculationSources.Register(ChessTransitions.SourceId);
        CalculationSources.Register(ChessPositionOutcomes.SourceId);
        CalculationSources.Register(ChessMoveOutcomes.SourceId);
        CalculationSources.Register(ChessPlayerContextOutcomes.SourceId);
        CalculationSources.Register(ChessTacticOutcomes.SourceId);
        CalculationSources.Register(ChessStockfishEval.SourceId);
    }
}
