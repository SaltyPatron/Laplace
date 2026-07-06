using System.Text.Json.Serialization;

namespace Laplace.Api.Contracts;

public sealed record ExploreCatalogResponse(
    [property: JsonPropertyName("counts")] IReadOnlyList<SubstrateCount> Counts,
    [property: JsonPropertyName("consensus")] ConsensusHealth? Consensus,
    [property: JsonPropertyName("multi_source_entity_count")] long? MultiSourceEntityCount,
    [property: JsonPropertyName("top_relations")] IReadOnlyList<VisualizationEdge> TopRelations,
    [property: JsonPropertyName("sources")] IReadOnlyList<ExploreSourceRow> Sources,
    [property: JsonPropertyName("stages")] IReadOnlyList<ExploreStageRow> Stages,
    [property: JsonPropertyName("featured_refs")] IReadOnlyList<string> FeaturedRefs);

public sealed record ExploreSourceRow(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("evidence")] long Evidence,
    [property: JsonPropertyName("content")] long Content,
    [property: JsonPropertyName("stage")] string? Stage,
    [property: JsonPropertyName("layer")] string? Layer,
    [property: JsonPropertyName("role")] string? Role);

public sealed record ExploreStageRow(
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("order")] int Order,
    [property: JsonPropertyName("law")] string? Law,
    [property: JsonPropertyName("sources")] IReadOnlyList<ExploreStageSourceRow> Sources);

public sealed record ExploreStageSourceRow(
    [property: JsonPropertyName("cli")] string Cli,
    [property: JsonPropertyName("layer")] string? Layer,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("links")] string? Links);

public sealed record ExploreResolveResponse(
    [property: JsonPropertyName("id_hex")] string IdHex,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("ref_kind")] string RefKind,
    [property: JsonPropertyName("exists")] bool Exists,
    [property: JsonPropertyName("preview_facts")] IReadOnlyList<SalientFactRow> PreviewFacts);

public sealed record ExploreEntityPreviewResponse(
    [property: JsonPropertyName("id_hex")] string IdHex,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("tier")] short? Tier,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("exists")] bool Exists,
    [property: JsonPropertyName("evidence_count")] long EvidenceCount,
    [property: JsonPropertyName("preview_facts")] IReadOnlyList<SalientFactRow> PreviewFacts);

public sealed record SalientFactRow(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("fact")] string Fact,
    [property: JsonPropertyName("eff_mu")] decimal EffMu,
    [property: JsonPropertyName("witnesses")] long Witnesses);

public sealed record ExplorePhysicalityRow(
    [property: JsonPropertyName("type")] short Type,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("m")] double M,
    [property: JsonPropertyName("radius")] double Radius,
    [property: JsonPropertyName("n_constituents")] int Constituents);

public sealed record ExploreConsensusRow(
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("entity_id_hex")] string EntityIdHex,
    [property: JsonPropertyName("entity_label")] string EntityLabel,
    [property: JsonPropertyName("eff_mu")] decimal EffMu,
    [property: JsonPropertyName("witnesses")] long Witnesses);

public sealed record ExploreSenseRow(
    [property: JsonPropertyName("sense_id_hex")] string SenseIdHex,
    [property: JsonPropertyName("synset_id_hex")] string SynsetIdHex,
    [property: JsonPropertyName("synset_label")] string SynsetLabel,
    [property: JsonPropertyName("eff_mu")] decimal EffMu,
    [property: JsonPropertyName("witnesses")] long Witnesses);

public sealed record ExploreConstituentRow(
    [property: JsonPropertyName("ordinal")] int Ordinal,
    [property: JsonPropertyName("child_id_hex")] string ChildIdHex,
    [property: JsonPropertyName("child_label")] string ChildLabel,
    [property: JsonPropertyName("run_length")] int RunLength,
    [property: JsonPropertyName("flags")] long Flags);

public sealed record ExploreMemberRow(
    [property: JsonPropertyName("member_id_hex")] string MemberIdHex,
    [property: JsonPropertyName("member_label")] string MemberLabel,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("eff_mu")] decimal EffMu,
    [property: JsonPropertyName("witnesses")] long Witnesses);

public sealed record ExploreEntityResponse(
    [property: JsonPropertyName("id_hex")] string IdHex,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("tier")] short? Tier,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("exists")] bool Exists,
    [property: JsonPropertyName("evidence_count")] long EvidenceCount,
    [property: JsonPropertyName("physicalities")] IReadOnlyList<ExplorePhysicalityRow> Physicalities,
    [property: JsonPropertyName("salient_facts")] IReadOnlyList<SalientFactRow> SalientFacts,
    [property: JsonPropertyName("consensus_out")] IReadOnlyList<ExploreConsensusRow> ConsensusOut,
    [property: JsonPropertyName("consensus_in")] IReadOnlyList<ExploreConsensusRow> ConsensusIn,
    [property: JsonPropertyName("senses")] IReadOnlyList<ExploreSenseRow> Senses,
    [property: JsonPropertyName("constituents")] IReadOnlyList<ExploreConstituentRow> Constituents,
    [property: JsonPropertyName("evidence")] IReadOnlyList<LabeledEvidenceItem> Evidence);

public sealed record ExploreEntityDetailResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("entity")] ExploreEntityResponse Entity,
    [property: JsonPropertyName("billing")] BillingReceipt? Billing);

public sealed record ExploreNeighborRow(
    [property: JsonPropertyName("neighbor")] string Neighbor,
    [property: JsonPropertyName("geodesic")] double Geodesic,
    [property: JsonPropertyName("frechet")] double? Frechet,
    [property: JsonPropertyName("axis")] string Axis);

public sealed record ExploreNeighborsResponse(
    [property: JsonPropertyName("id_hex")] string IdHex,
    [property: JsonPropertyName("structural")] IReadOnlyList<ExploreNeighborRow> Structural,
    [property: JsonPropertyName("semantic")] IReadOnlyList<SalientFactRow> Semantic);

public sealed record ExploreNeighborsDetailResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("neighbors")] ExploreNeighborsResponse Neighbors,
    [property: JsonPropertyName("billing")] BillingReceipt? Billing);

public sealed record ExploreMembersResponse(
    [property: JsonPropertyName("id_hex")] string IdHex,
    [property: JsonPropertyName("members")] IReadOnlyList<ExploreMemberRow> Members);

public sealed record ExploreMembersDetailResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("members")] ExploreMembersResponse Members,
    [property: JsonPropertyName("billing")] BillingReceipt? Billing);

public sealed record ExplorePeersResponse(
    [property: JsonPropertyName("id_hex")] string IdHex,
    [property: JsonPropertyName("peers")] IReadOnlyList<ExplorePeerRow> Peers);

public sealed record ExplorePeerRow(
    [property: JsonPropertyName("peer")] string Peer,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("strength")] double Strength);

public sealed record ExplorePeersDetailResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("peers")] ExplorePeersResponse Peers,
    [property: JsonPropertyName("billing")] BillingReceipt? Billing);

public sealed record ExploreContainerRow(
    [property: JsonPropertyName("entity_id_hex")] string EntityIdHex,
    [property: JsonPropertyName("entity_label")] string EntityLabel,
    [property: JsonPropertyName("tier")] short Tier,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("hops")] int Hops);

public sealed record ExploreContainersResponse(
    [property: JsonPropertyName("id_hex")] string IdHex,
    [property: JsonPropertyName("containers")] IReadOnlyList<ExploreContainerRow> Containers);

public sealed record ExploreContainersDetailResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("containers")] ExploreContainersResponse Containers,
    [property: JsonPropertyName("billing")] BillingReceipt? Billing);

public sealed record DecomposeNodeRow(
    [property: JsonPropertyName("ordinal")] uint Ordinal,
    [property: JsonPropertyName("id_hex")] string IdHex,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("tier")] byte Tier,
    [property: JsonPropertyName("text_offset")] int TextOffset,
    [property: JsonPropertyName("text_length")] int TextLength);

public sealed record DecomposeResponse(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("root_id_hex")] string RootIdHex,
    [property: JsonPropertyName("natural_unit_ordinal")] uint NaturalUnitOrdinal,
    [property: JsonPropertyName("nodes")] IReadOnlyList<DecomposeNodeRow> Nodes);

public sealed record DecomposeRequest(
    [property: JsonPropertyName("text")] string? Text);

public sealed record ExploreTrainingExportResponse(
    [property: JsonPropertyName("id_hex")] string IdHex,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("generated_at")] long GeneratedAt,
    [property: JsonPropertyName("witness_rows")] long WitnessRows,
    [property: JsonPropertyName("consensus_rows")] long ConsensusRows,
    [property: JsonPropertyName("entity")] ExploreEntityResponse Entity,
    [property: JsonPropertyName("members")] IReadOnlyList<ExploreMemberRow> Members,
    [property: JsonPropertyName("peers")] IReadOnlyList<ExplorePeerRow> Peers);

public sealed record ExploreTrainingExportDetailResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("export")] ExploreTrainingExportResponse Export,
    [property: JsonPropertyName("billing")] BillingReceipt? Billing);

public sealed record ExploreTrainingExportRequest(
    [property: JsonPropertyName("consensus_limit")] int? ConsensusLimit,
    [property: JsonPropertyName("evidence_limit")] int? EvidenceLimit,
    [property: JsonPropertyName("include_members")] bool IncludeMembers,
    [property: JsonPropertyName("include_peers")] bool IncludePeers);
