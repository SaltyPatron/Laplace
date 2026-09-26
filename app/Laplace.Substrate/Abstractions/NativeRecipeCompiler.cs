using System.Text;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Lowers one immutable semantic recipe to the native streaming engine's versioned
/// RCP instruction image. Resolution happens once per recipe, before any source records
/// cross the native boundary.
/// </summary>
public static class NativeRecipeCompiler
{
    private const uint Rcp1 = 0x31504352u;
    private const uint Rcp2 = 0x32504352u;
    private const uint Rcp3 = 0x33504352u;
    private const uint Rcp4 = 0x34504352u;
    private const uint Rcp5 = 0x35504352u;
    private const uint Rcp6 = 0x36504352u;
    // Rcp7: Rcp6 plus the source's identity tables and the fields/subjects resolved through them.
    private const uint Rcp7 = 0x37504352u;
    private const uint Rcp8 = 0x38504352u;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] Compile(SemanticSourceRecipe recipe, int recordDepth = 2)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        if (recordDepth is < 0 or > 128)
            throw new ArgumentOutOfRangeException(nameof(recordDepth));

        var aliases = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.Ordinal);
        foreach (var pair in recipe.ValueAliases.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            int separator = pair.Key.IndexOf('\0');
            if (separator < 0)
                throw new InvalidDataException("Recipe value alias has no property separator.");
            string property = pair.Key[..separator];
            if (!aliases.TryGetValue(property, out var values))
                aliases.Add(property, values = []);
            values.Add(new(pair.Key[(separator + 1)..], pair.Value));
        }

        using var image = new MemoryStream();
        using var writer = new BinaryWriter(image, Utf8, leaveOpen: true);
        bool extended = recipe.DelimitedSyntax is not null
            || recipe.Fields.Any(field => field.ContextField is not null);
        bool hasDefaultSemantics = recipe.Fields.Any(
            field => field.DefaultValue is not null || field.OmitDefaultTestimony);
        bool hasStructures = recipe.Structures.Count != 0
            || recipe.ProviderRoutes.Any(route => route.StructurePaths.Count != 0);
        bool hasInheritedAttributes = recipe.ProviderRoutes.Any(
            static route => route.InheritParentAttributes);
        bool grouped = recipe.DelimitedSyntax?.IsGrouped == true
            || recipe.Fields.Any(static field => field.SubjectMode != SourceSubjectMode.Record
                || field.PairMode != SourcePairMode.None || field.RelationField is not null
                || field.GroupOnce || field.OmitWhenEqualsSubject);
        bool identityTables = recipe.TurtleSyntax is not null || recipe.IdentityTables.Count != 0 || recipe.AttributeVocabularies.Count != 0
            || recipe.DelimitedSyntax is { HeaderLines: > 0 }
            || recipe.ProviderRoutes.Any(static r => r.ParseStructure is not null || r.WitnessFields is { Count: > 0 })
            || recipe.Fields.Any(static f => f.ObjectLiteral is not null || f.ContextLiteral is not null
                || f.ObservationOf is not null || f.ScoreOf is not null || f.Vocabulary is not null
                || f.Aggregate || f.Qualifiers is { Count: > 0 } || f.QualifierFamily is not null);
        bool childSubjects = recipe.ProviderRoutes.Any(static r => r.ChildSubjects is { Count: > 0 }
                || r.ConditionalPrefixes is { Count: > 0 }
                || r.ElementCompositions is { Count: > 0 } || r.Subject.Kind == SourceSubjectBindingKind.Composition)
            || recipe.Fields.Any(static f => f.ObjectScopedToRecord || f.ObjectScopePath is not null
                || f.ObjectIsRecordSubject || f.SubjectMode == SourceSubjectMode.Span || f.ObjectParts is { Count: > 0 }
                || f.OutcomeField is not null || f.DrawPrefix is not null
                || f.ValueListSeparator is not null || f.FlagRelation is not null || f.SignedValues)
            // A static relation named in its inverse direction needs the RCP8 flip bit.
            || recipe.Fields.Any(static f => f.Disposition.HasFlag(SourceFieldDisposition.Testimony)
                && f.PairMode == SourcePairMode.None && f.RelationField is null
                && RelationTypeRegistry.Resolve(f.RelationName ?? f.PropertyName).Flip);
        uint version = childSubjects ? Rcp8 : identityTables ? Rcp7 : grouped ? Rcp6 : hasInheritedAttributes ? Rcp5 : hasStructures ? Rcp4
            : hasDefaultSemantics ? Rcp3 : extended ? Rcp2 : Rcp1;
        bool hasExtendedHeader = version != Rcp1;
        writer.Write(version);
        writer.Write((uint)recordDepth);
        if (hasExtendedHeader) writer.Write(recipe.DelimitedSyntax is not null ? 1u : recipe.TurtleSyntax is not null ? 2u : 0u);
        if (recipe.TurtleSyntax is { } turtle)
        {
            WriteText(writer, turtle.RecordName);
            WriteText(writer, turtle.NamespaceUri);
            var excluded = turtle.ExcludeTypes ?? [];
            writer.Write(checked((uint)excluded.Count));
            foreach (string type in excluded) WriteText(writer, type);
        }
        if (recipe.DelimitedSyntax is { } syntax)
        {
            WriteText(writer, syntax.RecordName);
            WriteText(writer, syntax.NamespaceUri);
            WriteText(writer, syntax.Separator);
            WriteText(writer, syntax.CommentPrefix);
            writer.Write(syntax.TrimFields ? 1u : 0u);
            WriteText(writer, syntax.DirectivePrefix);
            WriteText(writer, syntax.DirectiveRecordName);
            writer.Write(checked((uint)syntax.Columns.Count));
            foreach (string column in syntax.Columns) WriteText(writer, column);
            writer.Write(checked((uint)(syntax.DirectiveColumns?.Count ?? 0)));
            foreach (string column in syntax.DirectiveColumns ?? []) WriteText(writer, column);
            WriteText(writer, syntax.RangeColumn);
            WriteText(writer, syntax.RangeSeparator);
            WriteText(writer, syntax.RangeFirstField);
            WriteText(writer, syntax.RangeLastField);
            writer.Write(checked((uint)syntax.MinimumColumns));
            writer.Write(syntax.AllowTrailingEmptyColumn ? 1u : 0u);
            if (version >= Rcp6)
            {
                writer.Write(syntax.GroupBlankLines ? 1u : 0u);
                WriteText(writer, syntax.GroupAttributeSeparator);
                WriteText(writer, syntax.SkipKeyColumn);
                WriteText(writer, syntax.SkipKeyCharacters);
                writer.Write(checked((uint)(syntax.References?.Count ?? 0)));
                foreach (SourceDelimitedReference reference in syntax.References ?? [])
                {
                    WriteText(writer, reference.Column);
                    WriteText(writer, reference.KeyColumn);
                    WriteText(writer, reference.TargetColumn);
                    WriteText(writer, reference.RootValue);
                    WriteText(writer, reference.PairSeparator);
                    WriteText(writer, reference.PairValueSeparator);
                }
                var constants = (syntax.Constants ?? new Dictionary<string, string>())
                    .OrderBy(static pair => pair.Key, StringComparer.Ordinal).ToArray();
                writer.Write(checked((uint)constants.Length));
                foreach (var constant in constants)
                {
                    WriteText(writer, constant.Key);
                    WriteText(writer, constant.Value);
                }
                if (version >= Rcp7) writer.Write(checked((uint)syntax.HeaderLines));
            }
        }
        writer.Write(checked((uint)recipe.Fields.Count));
        foreach (SourceRecipeField field in recipe.Fields)
        {
            if (!Enum.IsDefined(field.ValueKind) || !Enum.IsDefined(field.ReferenceCodec))
                throw new InvalidDataException($"Unknown native recipe opcode at '{field.SyntaxPath}'.");

            bool testimony = field.Disposition.HasFlag(SourceFieldDisposition.Testimony);
            // A key-value composition keeps the field's one governed relation; only the
            // relation-naming pair modes and relation fields resolve per item.
            bool dynamicRelation = (field.PairMode != SourcePairMode.None
                    && field.PairMode != SourcePairMode.KeyValueComposition)
                || field.RelationField is not null;
            var relation = testimony && !dynamicRelation
                ? RelationTypeRegistry.Resolve(field.RelationName ?? field.PropertyName)
                : default;
            // A static field emits (record subject, relation, value). A name that resolves
            // with a flip (an inverse alias or a flipped retirement) states the claim in the
            // inverse direction: the field program carries the flip and the stream emits
            // (value, relation, record subject).
            bool flip = testimony && !dynamicRelation && relation.Flip;
            double rank = field.RelationRank ?? (testimony && !dynamicRelation ? relation.Rank : 1);
            if (!double.IsFinite(rank) || rank is < 0 or > 1)
                throw new InvalidDataException($"Invalid relation rank at '{field.SyntaxPath}'.");
            Hash128 parent = !testimony || dynamicRelation ? Hash128.Zero : field.RelationParent is { } parentName
                ? RelationTypeRegistry.Resolve(parentName).Id : relation.ParentId ?? Hash128.Zero;
            Hash128 lexical = Hash128.Zero;
            if (testimony && field.PreserveLexicalValue)
            {
                if (string.IsNullOrEmpty(field.LexicalRelationName))
                    throw new InvalidDataException($"Missing lexical relation at '{field.SyntaxPath}'.");
                lexical = RelationTypeRegistry.Resolve(field.LexicalRelationName).Id;
            }

            WriteText(writer, field.SyntaxPath);
            writer.Write((uint)field.ValueKind);
            writer.Write((uint)field.Disposition);
            writer.Write((uint)field.ReferenceCodec);
            WriteText(writer, field.AbsentSentinel);
            WriteText(writer, field.SequenceSeparator);
            WriteText(writer, field.ObjectNamespace ?? $"recipe/value/{field.PropertyName}");
            WriteHash(writer, relation.Id);
            WriteHash(writer, parent);
            // An empty object entity type declares no classification testimony.
            WriteHash(writer, string.IsNullOrEmpty(field.ObjectEntityType)
                ? Hash128.Zero : EntityTypeRegistry.Id(field.ObjectEntityType));
            WriteHash(writer, lexical);
            writer.Write(rank);
            WriteAliases(writer, aliases, field.ValueAliasProperty ?? field.PropertyName);
            if (version >= Rcp3)
            {
                writer.Write(field.DefaultValue is null ? 0u : 1u);
                WriteText(writer, field.DefaultValue);
                writer.Write(field.OmitDefaultTestimony ? 1u : 0u);
            }
            if (hasExtendedHeader) WriteText(writer, field.ContextField);
            if (version >= Rcp6)
            {
                if (dynamicRelation && field.RelationResolver == SourceRelationResolver.None)
                    throw new InvalidDataException($"Dynamic relation at '{field.SyntaxPath}' declares no resolver.");
                writer.Write((uint)field.SubjectMode);
                writer.Write((uint)field.PairMode);
                writer.Write((uint)field.RelationResolver);
                WriteText(writer, field.RelationField);
                WriteText(writer, field.TrunkField);
                WriteText(writer, field.PairValueSeparator);
                writer.Write(field.OmitWhenEqualsSubject ? 1u : 0u);
                writer.Write(field.GroupOnce ? 1u : 0u);
            }
            if (version >= Rcp7)
            {
                WriteText(writer, field.IdentityTable);
                WriteText(writer, field.ObjectLiteral);
                WriteText(writer, field.ContextLiteral);
                WriteText(writer, field.ObservationOf);
                WriteText(writer, field.ScoreOf);
                WriteText(writer, field.Vocabulary);
                writer.Write(field.Aggregate ? 1u : 0u);
                writer.Write(checked((uint)(field.Qualifiers?.Count ?? 0)));
                foreach (string qualifier in field.Qualifiers ?? []) WriteText(writer, qualifier);
                WriteText(writer, field.QualifierFamily);
                WriteText(writer, field.QualifierField);
            }
            if (version >= Rcp8)
            {
                writer.Write(field.ObjectScopedToRecord ? 1u : 0u);
                WriteText(writer, field.ObjectScopePath);
                writer.Write(field.ObjectIsRecordSubject ? 1u : 0u);
                WriteText(writer, field.SpanStartField);
                WriteText(writer, field.SpanEndField);
                WriteParts(writer, field.ObjectParts);
                writer.Write(flip ? 1u : 0u);
                int surfaceQualifier = testimony && !dynamicRelation
                    ? NativeInterop.RelationSurfaceQualifier(field.RelationName ?? field.PropertyName) : -1;
                writer.Write(surfaceQualifier >= 0 ? checked((uint)surfaceQualifier + 1) : 0u);
                WriteText(writer, field.OutcomeField);
                WriteText(writer, field.RefuteValue);
                WriteText(writer, field.DrawPrefix);
                WriteText(writer, field.ValueListSeparator);
                WriteHash(writer, field.FlagRelation is null
                    ? Hash128.Zero : RelationTypeRegistry.Resolve(field.FlagRelation).Id);
                writer.Write(field.SignedValues ? 1u : 0u);
            }
        }

        if (version >= Rcp4)
        {
            writer.Write(checked((uint)recipe.Structures.Count));
            foreach (SourceRecipeStructure structure in recipe.Structures)
            {
                WriteText(writer, structure.SyntaxPath);
                WriteText(writer, structure.SemanticType);
                writer.Write((uint)structure.Disposition);
            }
        }

        writer.Write(checked((uint)recipe.ProviderRoutes.Count));
        foreach (SourceRecipeProviderRoute route in recipe.ProviderRoutes)
        {
            SourceRecipeSubjectBinding subject = route.Subject;
            if (!Enum.IsDefined(subject.Kind))
                throw new InvalidDataException($"Unknown subject binding in route '{route.RecordName}'.");
            if (subject.Kind == SourceSubjectBindingKind.ClassifierField
                && string.IsNullOrEmpty(subject.EntityNamespace))
                throw new InvalidDataException($"Classifier route '{route.RecordName}' has no entity namespace.");
            recipe.TryField($"{route.FieldPrefix}/@{subject.IdentityField}", out var identity);
            SourceReferenceCodec subjectCodec = identity?.ReferenceCodec ?? SourceReferenceCodec.None;
            if (subjectCodec == SourceReferenceCodec.None)
                subjectCodec = identity?.ValueKind switch
                {
                    SourceValueKind.Codepoint => SourceReferenceCodec.UnicodeCodepoint,
                    SourceValueKind.CodepointSequence => SourceReferenceCodec.UnicodeCodepointSequence,
                    _ => SourceReferenceCodec.None,
                };
            if (subjectCodec == SourceReferenceCodec.UPlusCodepointWithQualifier)
                throw new InvalidDataException($"Route '{route.RecordName}' needs one subject, not qualified reference targets.");

            WriteText(writer, route.RecordName);
            WriteText(writer, route.NamespaceUri);
            WriteText(writer, route.FieldPrefix);
            writer.Write((uint)subject.Kind);
            WriteText(writer, subject.IdentityField);
            WriteText(writer, subject.RangeStartField);
            WriteText(writer, subject.LastField);
            WriteText(writer, subject.EntityNamespace);
            WriteText(writer, subject.SequenceSeparator ?? identity?.SequenceSeparator ?? " ");
            writer.Write((uint)subjectCodec);
            WriteHash(writer, string.IsNullOrEmpty(subject.EntityType)
                ? Hash128.Zero : EntityTypeRegistry.Id(subject.EntityType));
            writer.Write(checked((uint)(route.ChildFieldPrefixes?.Count ?? 0)));
            if (route.ChildFieldPrefixes is { } children)
                foreach (var child in children.OrderBy(static p => p.Key, StringComparer.Ordinal))
                {
                    WriteText(writer, child.Key);
                    WriteText(writer, child.Value);
                }

            string? membership = route.RangeRelationName ?? route.RangeRelationProperty;
            bool hasMembershipRange = !string.IsNullOrEmpty(route.RangeStartField)
                || !string.IsNullOrEmpty(route.RangeEndField);
            if (membership is null && hasMembershipRange)
                throw new InvalidDataException($"Range-membership route '{route.RecordName}' has no relation declaration.");
            if (membership is not null && (subject.Kind is SourceSubjectBindingKind.CodepointRange
                    or SourceSubjectBindingKind.CodepointInterval
                || string.IsNullOrEmpty(route.RangeStartField) || string.IsNullOrEmpty(route.RangeEndField)))
                throw new InvalidDataException($"Range-membership route '{route.RecordName}' has incomplete subject/range declarations.");
            WriteText(writer, route.RangeStartField);
            WriteText(writer, route.RangeEndField);
            WriteHash(writer, membership is null ? Hash128.Zero : RelationTypeRegistry.Resolve(membership).Id);

            string property = identity is not null
                ? identity.ValueAliasProperty ?? identity.PropertyName
                : recipe.CanonicalProperty(subject.IdentityField);
            WriteAliases(writer, aliases, property);
            if (version >= Rcp4)
            {
                writer.Write(checked((uint)route.StructurePaths.Count));
                foreach (string structurePath in route.StructurePaths)
                {
                    if (!recipe.TryStructure(structurePath, out _))
                        throw new InvalidDataException(
                            $"Route '{route.RecordName}' references unknown structure '{structurePath}'.");
                    WriteText(writer, structurePath);
                }
                if (version >= Rcp5)
                    writer.Write(route.InheritParentAttributes ? 1u : 0u);
            }
            if (version >= Rcp7)
            {
                WriteText(writer, subject.IdentityTable);
                SourceParseStructure? parse = route.ParseStructure;
                writer.Write(parse is null ? 0u : 1u);
                if (parse is not null)
                {
                    if (recipe.DelimitedSyntax is not { IsGrouped: true })
                        throw new InvalidDataException($"Route '{route.RecordName}' declares a parse structure without grouped delimited syntax.");
                    WriteText(writer, parse.TrunkField);
                    WriteText(writer, parse.IdColumn);
                    WriteText(writer, parse.FormColumn);
                    WriteText(writer, parse.UposColumn);
                    WriteText(writer, parse.HeadColumn);
                    WriteText(writer, parse.DeprelColumn);
                    WriteText(writer, parse.UposVocabulary);
                }
                writer.Write(checked((uint)(route.WitnessFields?.Count ?? 0)));
                foreach (string field in route.WitnessFields ?? []) WriteText(writer, field);
            }
            if (version >= Rcp8)
            {
                WriteParts(writer, route.Subject.IdentityParts);
                writer.Write(checked((uint)(route.ElementCompositions?.Count ?? 0)));
                foreach (SourceElementComposition element in route.ElementCompositions ?? [])
                {
                    WriteText(writer, element.Path);
                    WriteParts(writer, element.Parts);
                    WriteHash(writer, EntityTypeRegistry.Id(element.EntityType));
                    WriteHash(writer, element.Relation is null
                        ? Hash128.Zero : RelationTypeRegistry.Resolve(element.Relation).Id);
                    WriteText(writer, element.ObservationField);
                    WriteText(writer, element.WhenField);
                    WriteText(writer, element.WhenValue);
                }
                writer.Write(checked((uint)(route.ChildSubjects?.Count ?? 0)));
                foreach (SourceChildSubject child in route.ChildSubjects ?? [])
                {
                    if (route.ChildFieldPrefixes is not { } prefixes || !prefixes.ContainsKey(child.ChildPath))
                        throw new InvalidDataException(
                            $"Route '{route.RecordName}' child subject '{child.ChildPath}' has no child field prefix.");
                    WriteText(writer, child.ChildPath);
                    WriteText(writer, child.IdentityField);
                    // The parent relation is a governed surface: an inverse alias reverses
                    // the link and carries its qualifier (IS_MEMBER_OF -> HAS_PART {member}).
                    RelationTypeRegistry.RelationTypeResolution? link = child.ParentRelation is null
                        ? null : RelationTypeRegistry.Resolve(child.ParentRelation);
                    WriteHash(writer, link?.Id ?? Hash128.Zero);
                    WriteHash(writer, EntityTypeRegistry.Id(child.EntityType));
                    writer.Write((child.ChildIsSubject ^ (link?.Flip ?? false) ? 1u : 0u) | (child.Standalone ? 2u : 0u));
                    WriteParts(writer, child.IdentityParts);
                    int linkQualifier = child.ParentRelation is null
                        ? -1 : NativeInterop.RelationSurfaceQualifier(child.ParentRelation);
                    writer.Write(linkQualifier >= 0 ? checked((uint)linkQualifier + 1) : 0u);
                    WriteText(writer, child.OutcomeField);
                    WriteText(writer, child.RefuteValue);
                }
                writer.Write(checked((uint)(route.ConditionalPrefixes?.Count ?? 0)));
                foreach (SourceConditionalPrefix conditional in route.ConditionalPrefixes ?? [])
                {
                    WriteText(writer, conditional.ChildPath);
                    WriteText(writer, conditional.Field);
                    WriteText(writer, conditional.Value);
                    WriteText(writer, conditional.Prefix);
                }
            }
        }

        if (version >= Rcp7)
        {
            writer.Write(checked((uint)recipe.IdentityTables.Count));
            foreach (SourceIdentityTable table in recipe.IdentityTables)
            {
                WriteText(writer, table.Name);
                WriteText(writer, table.RecordName);
                WriteText(writer, table.KeyPath);
                WriteText(writer, table.ValuePath);
                writer.Write(checked((uint)(table.AbsentValues?.Count ?? 0)));
                foreach (string absent in table.AbsentValues ?? []) WriteText(writer, absent);
            }
            writer.Write(checked((uint)recipe.AttributeVocabularies.Count));
            foreach (var pair in recipe.AttributeVocabularies)
            {
                WriteText(writer, pair.Key);
                WriteText(writer, pair.Value);
            }
        }

        writer.Flush();
        return image.ToArray();
    }

    private static void WriteParts(BinaryWriter writer, IReadOnlyList<SourceIdentityPart>? parts)
    {
        writer.Write(checked((uint)(parts?.Count ?? 0)));
        foreach (SourceIdentityPart part in parts ?? [])
        {
            WriteText(writer, part.Path);
            WriteText(writer, part.Vocabulary);
            WriteText(writer, part.Join);
            WriteText(writer, part.ScopePath);
            WriteHash(writer, part.ScopeEntityType is null
                ? Hash128.Zero : EntityTypeRegistry.Id(part.ScopeEntityType));
            WriteText(writer, part.Children);
            WriteText(writer, part.SplitLast);
            writer.Write((uint)part.Side);
            WriteText(writer, part.SpaceMark);
            WriteText(writer, part.Literal);
            writer.Write(part.Nested ? 1u : 0u);
            writer.Write(part.Aliased ? 1u : 0u);
            writer.Write(part.Codepoints ? 1u : 0u);
        }
    }

    private static void WriteText(BinaryWriter writer, string? value)
    {
        byte[] bytes = Utf8.GetBytes(value ?? "");
        writer.Write(checked((uint)bytes.Length));
        writer.Write(bytes);
    }

    private static void WriteHash(BinaryWriter writer, Hash128 value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.WriteBytes(bytes);
        writer.Write(bytes);
    }

    private static void WriteAliases(BinaryWriter writer,
        Dictionary<string, List<KeyValuePair<string, string>>> aliases, string property)
    {
        aliases.TryGetValue(property, out var values);
        writer.Write(checked((uint)(values?.Count ?? 0)));
        if (values is null) return;
        foreach (var pair in values)
        {
            WriteText(writer, pair.Key);
            WriteText(writer, pair.Value);
        }
    }
}
