using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Base for every relation-triple source (ATOMIC, ConceptNet, …). A subclass implements
/// ONLY <see cref="Decomposer{TRecord}.ExtractRecordsAsync"/> — pure content → (subject, relation, object)
/// records. Each record composes two tier trees (subject + object) before the edge is
/// emitted; batch/probe/commit sizing uses <see cref="IngestSourceProfile.RelationTriple"/>.
/// </summary>
public abstract class RelationTripleDecomposerBase : RelationTripleDecomposer;
