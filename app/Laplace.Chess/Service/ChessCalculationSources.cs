using System.Runtime.CompilerServices;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

/// <summary>Registers the chess sources whose testimony is versioned calculation rather than
/// recorded observation (analysis, trajectory, opening match, transition and outcome tallies,
/// engine evaluation), at module load.</summary>
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
