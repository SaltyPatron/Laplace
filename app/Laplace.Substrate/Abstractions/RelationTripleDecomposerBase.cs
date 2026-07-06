using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Base for every relation-triple source (ATOMIC, ConceptNet, …). A subclass implements
/// ONLY <see cref="Decomposer{TRecord}.ExtractRecordsAsync"/> — pure content → (subject, relation, object)
/// records. All ingestion is the one shared pipeline via <see cref="Decomposer{TRecord}"/>.
/// </summary>
public abstract class RelationTripleDecomposerBase : RelationTripleDecomposer;
