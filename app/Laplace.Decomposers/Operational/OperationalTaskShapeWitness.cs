using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Operational;

/// <summary>
/// A declared structural applicability contract. All external references are
/// exact substrate IDs; source field names never become prompt word triggers.
/// The native task-shape reader owns matching and actual predicate execution.
/// </summary>
internal sealed class OperationalTaskShapeWitness : IGrammarWitness
{
    internal const string Schema = "laplace/task-shape/relation-read/token-slots/v1";
    internal const string SchemaV2 = "laplace/task-shape/relation-read/token-slots/v2";
    internal static readonly OperationalTaskShapeWitness Instance = new();
    internal static readonly Hash128 SchemaId = Hash128.OfCanonical(Schema);
    internal static readonly Hash128 SlotSchemaId = Hash128.OfCanonical("laplace/task-shape/token-slot/v1");
    internal static readonly Hash128 SlotsEndId = Hash128.OfCanonical("laplace/task-shape/slots-end/v1");
    internal static readonly Hash128 SchemaV2Id = Hash128.OfCanonical(SchemaV2);
    internal static readonly Hash128 SlotSchemaV2Id = Hash128.OfCanonical("laplace/task-shape/token-slot/v2");
    internal static readonly Hash128 SlotsEndV2Id = Hash128.OfCanonical("laplace/task-shape/slots-end/v2");
    internal static readonly Hash128 CurrentFormBindingId = Hash128.OfCanonical("laplace/task-shape/binding/current-form/v1");
    internal static readonly Hash128 WitnessedSemanticBindingId = Hash128.OfCanonical("laplace/task-shape/binding/witnessed-semantic/v1");

    public string ModalityId => "json";

    internal sealed record Slot(Hash128 Id, Hash128 TokenRefId, Hash128 AcceptedTypeId, Hash128 BindingModeId);
    internal sealed record Definition(Hash128 Id, string Schema, Hash128 ExemplarParseId,
        Hash128 PredicateId, Slot[] Slots, Hash128[] Constituents);

    internal static Definition Read(GrammarAst ast, byte[] utf8)
    {
        int documentRoot = -1;
        for (int i = 0; i < ast.NodeCount; i++)
        {
            var node = ast.GetNode(i);
            if (node.IsError != 0)
                throw new InvalidDataException("Task shape JSON contains a grammar error.");
            if (node.Parent == GrammarAst.Root)
            {
                if (documentRoot >= 0)
                    throw new InvalidDataException("Task shape requires one complete JSON document.");
                documentRoot = i;
            }
        }
        if (documentRoot < 0 || !ast.NodeTypeIs(ast.GetNode(documentRoot).NodeTypeId, "document"u8))
            throw new InvalidDataException("Task shape requires a JSON document root.");
        int rootValueCount = 0;
        for (int i = 0; i < ast.NodeCount; i++)
            if (ast.GetNode(i).Parent == (uint)documentRoot)
            {
                rootValueCount++;
                if (!ast.NodeTypeIs(ast.GetNode(i).NodeTypeId, "object"u8))
                    throw new InvalidDataException("Task shape requires one top-level JSON object.");
            }
        if (rootValueCount != 1)
            throw new InvalidDataException("Task shape requires exactly one top-level JSON object.");

        using var document = JsonAstDocument.FromBorrowedAst(ast, utf8);
        var root = document.Root;
        var fields = ReadFields(root, "schema", "exemplar_parse_id", "predicate_id", "slots");
        string? schema = fields["schema"].AsString();
        if (schema is not (Schema or SchemaV2))
            throw new InvalidDataException("Unsupported operational task-shape schema.");
        bool version2 = schema == SchemaV2;
        Hash128 exemplar = ReadId(fields, "exemplar_parse_id", version2);
        Hash128 predicate = ReadId(fields, "predicate_id", version2);
        JsonAstCursor slotArray = fields["slots"];
        if (!slotArray.IsArray)
            throw new InvalidDataException("Task-shape slots must be an ordered array.");

        var slots = new List<Slot>();
        var refs = new HashSet<Hash128>();
        var flat = new List<Hash128> { version2 ? SchemaV2Id : SchemaId, exemplar, predicate };
        foreach (var item in slotArray.Items())
        {
            var slotFields = version2
                ? ReadFields(item, "exemplar_token_ref_id", "accepted_entity_type_id", "binding_mode_id")
                : ReadFields(item, "exemplar_token_ref_id", "accepted_entity_type_id");
            Hash128 token = ReadId(slotFields, "exemplar_token_ref_id", version2);
            Hash128 type = ReadId(slotFields, "accepted_entity_type_id", version2);
            Hash128 mode = version2 ? ReadId(slotFields, "binding_mode_id", version2, bindingMode: true)
                : WitnessedSemanticBindingId;
            if (!refs.Add(token))
                throw new InvalidDataException("A task shape cannot declare the same token slot twice.");
            Hash128 slotId = Hash128.Merkle(EntityTier.Document, version2
                ? [SlotSchemaV2Id, token, type, mode] : [SlotSchemaId, token, type]);
            RequireExternalIdentity(slotId, "computed slot", version2);
            slots.Add(new Slot(slotId, token, type, mode));
            flat.Add(slotId);
            flat.Add(token);
            flat.Add(type);
            if (version2) flat.Add(mode);
        }
        if (slots.Count == 0)
            throw new InvalidDataException("A reusable token-slot shape requires at least one declared slot.");
        flat.Add(version2 ? SlotsEndV2Id : SlotsEndId);
        Hash128[] constituents = flat.ToArray();
        return new Definition(Hash128.Merkle(EntityTier.Document, constituents),
            schema, exemplar, predicate, slots.ToArray(), constituents);
    }

    public void WalkRow(in GrammarComposeContext composed, in RowContext ctx,
        SubstrateChangeBuilder builder)
    {
        Definition shape = Read(composed.Ast, composed.Utf8);
        Hash128 context = ctx.ContextId is { } sourceFile && sourceFile != default
            ? sourceFile
            : throw new InvalidDataException("Task-shape testimony requires an admitted source-file context.");
        Hash128 source = OperationalSource.SourceId;
        bool version2 = shape.Schema == SchemaV2;
        foreach (Hash128 marker in version2
            ? new[] { SchemaV2Id, SlotSchemaV2Id, SlotsEndV2Id }
                .Concat(shape.Slots.Select(slot => slot.BindingModeId)).Distinct()
            : new[] { SchemaId, SlotSchemaId, SlotsEndId })
            builder.AddEntity(marker, EntityTier.Word, EntityTypeRegistry.SourceReference, source);
        builder.AddEntity(shape.Id, EntityTier.Document, EntityTypeRegistry.CodeConcept, source);
        foreach (Slot slot in shape.Slots)
            builder.AddEntity(slot.Id, EntityTier.Document, EntityTypeRegistry.CodeConcept, source);

        Hash128 physicality = PhysicalityId.Compute(shape.Id, PhysicalityType.ParseStructure);
        if (builder.TrySeePhysicality(physicality))
        {
            // The type-8 placement realizes the canonical declaration, whose
            // geometry must not depend on source-file formatting or key order.
            // This is identifier-representation geometry, not the unknown
            // placements of the referenced concepts. Serialize the validated
            // definition once in schema field order, then let the existing
            // native grammar composer realize that structural projection.
            // The original source AST remains borrowed and is never reparsed.
            var canonical = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new System.Text.Json.Utf8JsonWriter(canonical))
            {
                writer.WriteStartObject();
                writer.WriteString("schema", shape.Schema);
                writer.WriteString("exemplar_parse_id", Hex(shape.ExemplarParseId));
                writer.WriteString("predicate_id", Hex(shape.PredicateId));
                writer.WriteStartArray("slots");
                foreach (Slot slot in shape.Slots)
                {
                    writer.WriteStartObject();
                    writer.WriteString("exemplar_token_ref_id", Hex(slot.TokenRefId));
                    writer.WriteString("accepted_entity_type_id", Hex(slot.AcceptedTypeId));
                    if (version2) writer.WriteString("binding_mode_id", Hex(slot.BindingModeId));
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            byte[] canonicalUtf8 = canonical.WrittenSpan.ToArray();
            using var projectionAst = GrammarDecomposer.Parse(canonicalUtf8, "json");
            using var projection = new GrammarRowComposer(canonicalUtf8, projectionAst,
                source, "json", GrammarCompositionMode.FullSource);
            OrderedCompositionComponent placement = projection.RootComponent();
            projection.DrainInto(builder, SourceTrust.SubstrateMandate);
            double[] coord = [placement.CoordX, placement.CoordY, placement.CoordZ, placement.CoordM];
            builder.AddPhysicalityPreSeen(new PhysicalityRow(
                physicality, shape.Id, source, PhysicalityType.ParseStructure,
                coord[0], coord[1], coord[2], coord[3], Hilbert128.Encode(coord),
                Trajectory.Build(shape.Constituents), shape.Constituents.Length, null, null, 0));
        }

        Add(shape.ExemplarParseId, OperationalSource.ExampleOfTypeId, shape.Id);
        Add(shape.Id, OperationalSource.CallsTypeId, shape.PredicateId);
        foreach (Slot slot in shape.Slots)
            Add(shape.Id, OperationalSource.InputTypeId, slot.Id);

        void Add(Hash128 subject, Hash128 type, Hash128 value) =>
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                subject, type, value, source, context, SourceTrust.SubstrateMandate));

        static string Hex(Hash128 id) => Convert.ToHexString(id.ToBytes()).ToLowerInvariant();
    }

    private static Hash128 ReadId(IReadOnlyDictionary<string, JsonAstCursor> fields, string key,
        bool version2, bool bindingMode = false)
    {
        string? value = fields[key].AsString();
        if (value is not { Length: 32 })
            throw new InvalidDataException($"Task-shape field '{key}' requires one exact 16-byte hexadecimal ID.");
        byte[] bytes;
        try { bytes = Convert.FromHexString(value); }
        catch (FormatException error)
        {
            throw new InvalidDataException($"Task-shape field '{key}' is not a hexadecimal ID.", error);
        }
        Hash128 id = Hash128.FromBytes(bytes);
        if (bindingMode)
        {
            if (id != CurrentFormBindingId && id != WitnessedSemanticBindingId)
                throw new InvalidDataException("Unsupported operational task-shape binding mode.");
        }
        else RequireExternalIdentity(id, key, version2);
        return id;
    }

    private static void RequireExternalIdentity(Hash128 id, string key, bool version2)
    {
        if (id == default || id == SchemaId || id == SlotSchemaId || id == SlotsEndId)
            throw new InvalidDataException($"Task-shape field '{key}' cannot use an empty or reserved schema ID.");
        if (version2 && (id == SchemaV2Id || id == SlotSchemaV2Id || id == SlotsEndV2Id
            || id == CurrentFormBindingId || id == WitnessedSemanticBindingId))
            throw new InvalidDataException($"Task-shape field '{key}' cannot use a reserved schema or binding ID.");
    }

    private static Dictionary<string, JsonAstCursor> ReadFields(JsonAstCursor value, params string[] fields)
    {
        if (!value.IsObject)
            throw new InvalidDataException("Task-shape records must be JSON objects.");
        var remaining = new HashSet<string>(fields, StringComparer.Ordinal);
        var result = new Dictionary<string, JsonAstCursor>(StringComparer.Ordinal);
        foreach (var (key, field) in value.Pairs())
        {
            if (!remaining.Remove(key))
                throw new InvalidDataException($"Unexpected or duplicate task-shape field '{key}'.");
            result.Add(key, field);
        }
        if (remaining.Count != 0)
            throw new InvalidDataException("Task shape is missing required fields: " + string.Join(", ", remaining));
        return result;
    }
}
